using System;
using System.Collections.Generic;

namespace Segra.Backend.Recorder
{
    /// <summary>
    /// Maps a bookmark's wall-clock instant to its offset inside a saved disk-buffer replay.
    ///
    /// Stream-copy concat plays the selected segments back to back, but two things move a bookmark off
    /// its wall-clock offset from the start of the saved window:
    ///
    /// Dead timeline at each junction. Concatenating files inserts a gap that counts towards the output
    /// duration without adding playable content - measured 19-29 ms per junction, 4.2 s over the 30
    /// junctions of a 300 s save and 5.2 s over the 180 junctions of a 1800 s one. The real padding is
    /// derived from the measured output:
    ///   paddingPerJunction = max(0, outputDuration - contentSpan) / (selected.Count - 1)
    /// where contentSpan is the footage the window actually contributes, which is what the file holds.
    /// A segment the selection dropped is in neither term: it contributes no content and it joins no two
    /// files, so a hole leaves the padding alone.
    ///
    /// Footage that is simply missing. A segment below the ledger's 1 MiB floor never reaches the concat
    /// list, so its wall-clock span is a hole in the saved timeline. A bookmark after that hole is at
    /// (T - window start) late by exactly the footage that is not there, so the missing span of every
    /// dropped segment that ended at or before T comes back out, counted only over the part of it that
    /// fell inside the saved window.
    ///
    /// A bookmark recorded at wall time T crosses the junctions of every written segment that ended at or
    /// before T, so:
    ///   position = (T - effectiveWindowStart) - missingBefore(T) + paddingPerJunction * junctionsBefore(T)
    /// effectiveWindowStart is the nominal inpoint cut; the real cut snaps to the previous keyframe (up to
    /// 1 s early), which shifts early bookmarks by up to ~1 s - the measured padding absorbs that into
    /// paddingPerJunction. The position is intentionally left unclamped: bookmarks recorded outside the
    /// saved window come back negative or past the file duration and are dropped by the caller's range
    /// filter; clamping here instead would leave a post-save bookmark sitting at the end of the replay.
    /// </summary>
    internal sealed class DiskBufferBookmarkResolver
    {
        private readonly List<DiskBufferSegment> _selected;
        private readonly List<DiskBufferSegment> _dropped;
        private readonly DateTime _effectiveWindowStart;

        public DiskBufferBookmarkResolver(
            List<DiskBufferSegment> selected,
            List<DiskBufferSegment>? dropped,
            DateTime effectiveWindowStart,
            TimeSpan outputDuration,
            TimeSpan contentSpan)
        {
            _selected = selected;
            _dropped = dropped ?? new List<DiskBufferSegment>();
            _effectiveWindowStart = effectiveWindowStart;

            // Junctions are between files that were written: count-1 of them, never more, and the padding
            // they add is what the measured output has beyond the footage it holds.
            PaddingPerJunctionSeconds = _selected.Count > 1
                ? Math.Max(0, (outputDuration - contentSpan).TotalSeconds) / (_selected.Count - 1)
                : 0;
        }

        /// <summary>Dead timeline each junction between written segments adds, in seconds.</summary>
        public double PaddingPerJunctionSeconds { get; }

        public TimeSpan PositionFor(DateTime wallTime)
        {
            double position = (wallTime - _effectiveWindowStart).TotalSeconds - MissingSecondsBefore(wallTime);

            int junctions = 0;
            foreach (DiskBufferSegment segment in _selected)
            {
                if (segment.EndedAt != null && segment.EndedAt.Value <= wallTime)
                    junctions++;
            }

            return TimeSpan.FromSeconds(position + PaddingPerJunctionSeconds * junctions);
        }

        /// <summary>
        /// Seconds of footage missing from the saved file before this instant: the part of each dropped
        /// segment's span that lies between the window's start and T. A stub that sat before the window
        /// was never going to be in the file, and the wall-clock offset never counted it, so it takes
        /// nothing back.
        /// </summary>
        private double MissingSecondsBefore(DateTime wallTime)
        {
            double missing = 0;
            foreach (DiskBufferSegment segment in _dropped)
            {
                if (segment.EndedAt == null || segment.EndedAt.Value > wallTime)
                    continue;

                DateTime from = segment.StartedAt > _effectiveWindowStart ? segment.StartedAt : _effectiveWindowStart;
                DateTime to = segment.EndedAt.Value < wallTime ? segment.EndedAt.Value : wallTime;
                if (to > from)
                    missing += (to - from).TotalSeconds;
            }
            return missing;
        }
    }
}
