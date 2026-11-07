using System;
using System.Collections.Generic;
using Segra.Backend.Recorder;
using Xunit;

namespace Segra.Tests.Recorder
{
    /// <summary>
    /// The segment ledger is everything the disk buffer decides from: which files a save concatenates
    /// and which files pruning may delete. Settled decisions it has to keep honouring: 10 s segments,
    /// wall-clock spans (a segment runs from its start to the next segment's start, never
    /// endedAt - startedAt), a closed segment is prunable once its end is older than the window plus
    /// one segment, and the newest entry - the file OBS is still writing - is never pruned.
    /// </summary>
    public class DiskBufferLedgerTests
    {
        private const int WindowSeconds = 120;
        private static readonly DateTime T0 = new DateTime(2026, 11, 4, 12, 0, 0);

        private static string SegmentName(int offsetSeconds) =>
            $"C:/buffer/seg_{T0.AddSeconds(offsetSeconds):yyyy-MM-dd_HH-mm-ss}.mkv";

        private static DiskBufferLedger Recording(int lastBoundaryOffset = 30)
        {
            // Seed plus boundaries: files opened at 0, 10, 20 and 30, the 30 s one still being written.
            var ledger = new DiskBufferLedger(WindowSeconds);
            ledger.Seed(SegmentName(0), T0);
            for (int offset = 10; offset <= lastBoundaryOffset; offset += 10)
                ledger.RecordBoundary(T0.AddSeconds(offset), SegmentName(offset));
            return ledger;
        }

        [Fact]
        public void ABoundaryClosesTheOpenSegmentAndOpensTheNextAtTheSameInstant()
        {
            var ledger = Recording();

            var segments = ledger.Segments;
            Assert.Equal(4, segments.Count);
            Assert.Equal(SegmentName(0), segments[0].Path);
            Assert.Equal(T0, segments[0].StartedAt);
            Assert.Equal(T0.AddSeconds(10), segments[0].EndedAt);
            Assert.Equal(SegmentName(30), segments[3].Path);
            Assert.Equal(T0.AddSeconds(30), segments[3].StartedAt);
            Assert.Null(segments[3].EndedAt);
        }

        [Fact]
        public void PruningTakesClosedSegmentsPastTheWindowPlusOneSegment()
        {
            var ledger = Recording();

            // Window 120 s plus one 10 s segment: at +160 the closed ends are 150 s, 140 s and 130 s
            // old, so the first two are offered and the one sitting exactly on the boundary is not.
            var offered = ledger.CollectPrunable(T0.AddSeconds(160));
            Assert.Equal(new[] { SegmentName(0), SegmentName(10) }, CandidatePaths(offered));

            // An offer marks the segment while its delete runs: the same file is never handed to a
            // second delete task on the next boundary, only the file that has just left the window.
            Assert.Equal(new[] { SegmentName(20) }, CandidatePaths(ledger.CollectPrunable(T0.AddSeconds(165))));

            // The deletes land: the files leave the ledger.
            ledger.AccountReclaim(offered[0], 10_485_760, T0.AddSeconds(165));
            ledger.AccountReclaim(offered[1], 10_485_760, T0.AddSeconds(165));
            Assert.Equal(new[] { SegmentName(20), SegmentName(30) }, CandidatePaths(ledger.Segments));

            // A delete that could not run (the muxer still holds the file) is offered again later.
            ledger.RetryPrune(SegmentName(20));
            Assert.Equal(new[] { SegmentName(20) }, CandidatePaths(ledger.CollectPrunable(T0.AddSeconds(170))));
        }

        [Fact]
        public void TheSegmentOBSIsStillWritingIsNeverPruned()
        {
            var ledger = Recording();

            // Four minutes past the window: everything closed is offered except the open newest entry.
            var late = CandidatePaths(ledger.CollectPrunable(T0.AddSeconds(4000)));
            Assert.Equal(new[] { SegmentName(0), SegmentName(10), SegmentName(20) }, late);
            Assert.DoesNotContain(SegmentName(30), late);
        }

        [Fact]
        public void ASavePinsTheSegmentsItIsReadingAndReleasesThemAfterwards()
        {
            var ledger = Recording();

            // A window that opened at +15 overlaps the files that closed at +20 and +30.
            var pinned = ledger.PinOverlapping(T0.AddSeconds(15));
            Assert.Equal(new[] { SegmentName(10), SegmentName(20) }, CandidatePaths(pinned));

            // Pruning cannot reclaim them out from under the save; the file the save is not holding
            // still goes.
            Assert.Equal(new[] { SegmentName(0) }, CandidatePaths(ledger.CollectPrunable(T0.AddSeconds(4000))));

            ledger.Unpin(pinned);
            Assert.Equal(new[] { SegmentName(10), SegmentName(20) }, CandidatePaths(ledger.CollectPrunable(T0.AddSeconds(4000))));
        }

        private static string[] CandidatePaths(IEnumerable<DiskBufferSegment> segments)
        {
            var paths = new List<string>();
            foreach (var segment in segments)
                paths.Add(segment.Path);
            return paths.ToArray();
        }
    }
}
