using System;
using System.Collections.Generic;

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
        private readonly object _lock = new();
        private readonly List<DiskBufferSegment> _segments = new();
        private readonly int _windowSeconds;

        private DateTime? _firstReclaimAt;
        private long _reclaimedBytes;

        public DiskBufferLedger(int windowSeconds) => _windowSeconds = windowSeconds;

        /// <summary>Buffer window in seconds; pruning keeps window + one segment of footage.</summary>
        public int WindowSeconds => _windowSeconds;

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
        /// segment ends at now and nextPath becomes the new open segment.
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
        /// Pins the closed segments whose span overlaps a window that opened at windowStart, so pruning
        /// cannot delete them while a save reads them. The open (newest) segment is never saveable.
        /// </summary>
        public List<DiskBufferSegment> PinOverlapping(DateTime windowStart)
        {
            lock (_lock)
            {
                var pinned = new List<DiskBufferSegment>();
                foreach (DiskBufferSegment segment in _segments)
                {
                    if (segment.EndedAt == null || segment.EndedAt.Value <= windowStart)
                        continue;
                    segment.Pins++;
                    pinned.Add(segment);
                }
                return pinned;
            }
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
