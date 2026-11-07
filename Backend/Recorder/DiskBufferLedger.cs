using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Serilog;

namespace Segra.Backend.Recorder
{
    /// <summary>
    /// One mkv file the rolling disk buffer wrote. Times are wall clock: the segment runs from its
    /// start until the next segment starts (OBS's file_changed is what closes it), so a span is never
    /// derived from EndedAt - StartedAt.
    /// </summary>
    internal sealed class DiskBufferSegment
    {
        public DiskBufferSegment(string path) => Path = path;

        public string Path { get; }
        public DateTime StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }
        public int Pins { get; set; }
        public bool DeletePending { get; set; }
    }

    /// <summary>
    /// The ledger the disk buffer decides from: which segments are on disk, which of them a save is
    /// holding, and what pruning has reclaimed. DiskBufferService owns one per recording and feeds it
    /// OBS's file_changed boundaries; the save and the prune walk it.
    ///
    /// A closed segment becomes prunable once its end is older than WindowSeconds + SegmentSeconds
    /// (OBS cuts slightly late) and it is neither pinned by a save nor the newest entry, which is the
    /// file OBS is still writing.
    /// </summary>
    internal sealed class DiskBufferLedger
    {
        // Stop() emits a trailing file_changed whose next segment is a ~1 KB unparseable stub, and a
        // forced split leaves a partial remnant behind (measured 1,018,129 B). A segment below this
        // size is not worth a place in the concat list and is not worth pinning.
        public const long MinSegmentBytes = 1_048_576;

        private readonly object _lock = new();
        private readonly List<DiskBufferSegment> _segments = new();
        private readonly int _windowSeconds;

        private DateTime? _firstReclaimAt;
        private long _reclaimedBytes;
        private bool _stopping;

        public DiskBufferLedger(int windowSeconds) => _windowSeconds = windowSeconds;

        /// <summary>Buffer window in seconds; pruning keeps window + one segment of footage.</summary>
        public int WindowSeconds => _windowSeconds;

        /// <summary>
        /// A stop has been requested for this recording. The output is not closed yet - a stop waits
        /// for an in-flight save - and OBS keeps splitting files until it is, so the ledger keeps
        /// taking boundaries while stopping.
        /// </summary>
        public bool IsStopping
        {
            get { lock (_lock) return _stopping; }
        }

        /// <summary>Marks the recording as stopping; segments keep being tracked until Clear().</summary>
        public void RequestStop()
        {
            lock (_lock) _stopping = true;
        }

        /// <summary>Total bytes pruning has deleted for this recording.</summary>
        public long ReclaimedBytes
        {
            get { lock (_lock) return _reclaimedBytes; }
        }

        /// <summary>When this recording's first segment was reclaimed, or null before that.</summary>
        public DateTime? FirstReclaimAt
        {
            get { lock (_lock) return _firstReclaimAt; }
        }

        /// <summary>Snapshot of the ledger in file order. The entries are the live segments.</summary>
        public List<DiskBufferSegment> Segments
        {
            get { lock (_lock) return new List<DiskBufferSegment>(_segments); }
        }

        /// <summary>Seeds the ledger with the first file the output started writing.</summary>
        public void Seed(string path, DateTime startedAt)
        {
            lock (_lock)
                _segments.Add(new DiskBufferSegment(path) { StartedAt = startedAt });
        }

        /// <summary>
        /// OBS closed the file it was writing and opened nextPath at the same instant: the newest open
        /// segment ends at now and nextPath becomes the new open segment. This is tracked while
        /// stopping as well: a stop waits for an in-flight save before it closes the output, and the
        /// segments OBS split in the meantime are invisible to pruning and to the teardown unless they
        /// are in the ledger.
        /// </summary>
        public void RecordBoundary(DateTime now, string? nextPath)
        {
            lock (_lock)
            {
                for (int i = _segments.Count - 1; i >= 0; i--)
                {
                    if (_segments[i].EndedAt == null)
                    {
                        _segments[i].EndedAt = now;
                        break;
                    }
                }

                if (!string.IsNullOrEmpty(nextPath))
                    _segments.Add(new DiskBufferSegment(nextPath) { StartedAt = now });
            }
        }

        /// <summary>
        /// Segments pruning may delete at this instant, marked pending so a later boundary does not
        /// hand the same file to a second delete. The newest entry is never a candidate.
        /// </summary>
        public List<DiskBufferSegment> CollectPrunable(DateTime now)
        {
            lock (_lock)
            {
                TimeSpan window = TimeSpan.FromSeconds(_windowSeconds + DiskBufferService.SegmentSeconds);
                var candidates = new List<DiskBufferSegment>();
                for (int i = 0; i < _segments.Count - 1; i++)
                {
                    DiskBufferSegment segment = _segments[i];
                    if (segment.EndedAt == null || segment.Pins > 0 || segment.DeletePending)
                        continue;
                    if (now - segment.EndedAt.Value > window)
                    {
                        segment.DeletePending = true;
                        candidates.Add(segment);
                    }
                }
                return candidates;
            }
        }

        /// <summary>
        /// The delete could not run (the muxer or a reader still holds the file); the segment stays on
        /// disk and is offered again on a later boundary.
        /// </summary>
        public void RetryPrune(string path)
        {
            lock (_lock)
            {
                DiskBufferSegment? segment = Find(path);
                if (segment != null)
                    segment.DeletePending = false;
            }
        }

        /// <summary>
        /// The file is gone: drop it from the ledger and account the space it gave back.
        /// </summary>
        public void AccountReclaim(DiskBufferSegment segment, long freedBytes, DateTime now)
        {
            lock (_lock)
            {
                _segments.Remove(segment);
                segment.DeletePending = false;
                _firstReclaimAt ??= now;
                _reclaimedBytes += freedBytes;
            }
        }

        /// <summary>
        /// The segments a save whose window opened at windowStart writes, in ledger order, pinned so
        /// pruning cannot take them while ffmpeg reads them.
        ///
        /// A closed segment belongs to the window when it ended after the window opened; the open
        /// (newest) segment is still being written and is never saveable. sizeProbe reports a
        /// candidate's size, or null when the file cannot be read: a segment below MinSegmentBytes is
        /// the partial remnant a forced split leaves, is dropped, and stays prunable - a save never
        /// holds a file it is not writing.
        ///
        /// The probe waits on the muxer closing each file, which can take seconds, so it runs with no
        /// lock held, and a candidate that has left the ledger in the meantime is skipped.
        /// </summary>
        public async Task<List<DiskBufferSegment>> SelectSaveWindowAsync(
            DateTime windowStart,
            Func<DiskBufferSegment, Task<long?>> sizeProbe)
        {
            List<DiskBufferSegment> overlapping;
            lock (_lock)
            {
                overlapping = new List<DiskBufferSegment>();
                foreach (DiskBufferSegment segment in _segments)
                {
                    if (segment.EndedAt == null || segment.EndedAt.Value <= windowStart)
                        continue;
                    overlapping.Add(segment);
                }
            }

            var selected = new List<DiskBufferSegment>();
            foreach (DiskBufferSegment segment in overlapping)
            {
                long? size = await sizeProbe(segment);
                if (size == null)
                    continue;
                if (size.Value < MinSegmentBytes)
                {
                    Log.Information($"Skipping disk segment from the save (stub, {size.Value} B): {segment.Path}");
                    continue;
                }

                lock (_lock)
                {
                    if (!_segments.Contains(segment))
                        continue;
                    segment.Pins++;
                }
                selected.Add(segment);
            }
            return selected;
        }

        /// <summary>
        /// A save has finished with these segments; they can be pruned again.
        /// </summary>
        public void Unpin(List<DiskBufferSegment> segments)
        {
            lock (_lock)
            {
                foreach (DiskBufferSegment segment in segments)
                    segment.Pins--;
            }
        }

        /// <summary>Drops every entry: the recording's segments are gone with the output.</summary>
        public void Clear()
        {
            lock (_lock)
                _segments.Clear();
        }

        private DiskBufferSegment? Find(string path)
        {
            for (int i = _segments.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_segments[i].Path, path, StringComparison.OrdinalIgnoreCase))
                    return _segments[i];
            }
            return null;
        }
    }
}
