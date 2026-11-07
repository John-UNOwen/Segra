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
        public async Task ASaveOnlyPinsTheSegmentsItActuallyWrites()
        {
            // A window wide enough to cover every closed file. The one opened at +30 is the 1,018,129 B
            // remnant a forced split leaves behind: under the 1 MiB floor a selection applies, so it
            // never reaches the concat list.
            var ledger = Recording(lastBoundaryOffset: 40);
            var selected = await ledger.SelectSaveWindowAsync(
                T0.AddSeconds(-80),
                segment => Task.FromResult<long?>(segment.Path == SegmentName(30) ? 1_018_129L : 10_485_761L));

            Assert.Equal(new[] { SegmentName(0), SegmentName(10), SegmentName(20) }, CandidatePaths(selected));

            // A file the save dropped is not held: pruning keeps reclaiming it for the whole save
            // instead of leaving it pinned until the save ends.
            Assert.Equal(new[] { SegmentName(30) }, CandidatePaths(ledger.CollectPrunable(T0.AddSeconds(4000))));
        }

        [Fact]
        public async Task ASavePinsTheSegmentsItIsReadingAndReleasesThemAfterwards()
        {
            var ledger = Recording(lastBoundaryOffset: 40);

            // A window that opened at +25 covers the files that closed at +30 and +40, and nothing
            // before them.
            var selected = await ledger.SelectSaveWindowAsync(
                T0.AddSeconds(25),
                _ => Task.FromResult<long?>(10_485_761L));
            Assert.Equal(new[] { SegmentName(20), SegmentName(30) }, CandidatePaths(selected));

            // Pruning cannot reclaim a file out from under the save; the older ones it is not holding
            // still go.
            Assert.Equal(new[] { SegmentName(0), SegmentName(10) }, CandidatePaths(ledger.CollectPrunable(T0.AddSeconds(4000))));

            ledger.Unpin(selected);
            Assert.Equal(new[] { SegmentName(20), SegmentName(30) }, CandidatePaths(ledger.CollectPrunable(T0.AddSeconds(4000))));
        }

        [Fact]
        public void BoundariesKeepBeingTrackedWhileRecordingStops()
        {
            // A stop waits up to 15 minutes for an in-flight save and OBS keeps splitting files into the
            // folder the whole time. Those boundaries have to reach the ledger, or the files they
            // describe are invisible to pruning and to the teardown that has to account for them.
            var ledger = Recording();
            ledger.RequestStop();

            ledger.RecordBoundary(T0.AddSeconds(40), SegmentName(40));
            ledger.RecordBoundary(T0.AddSeconds(50), SegmentName(50));

            var segments = ledger.Segments;
            Assert.Equal(6, segments.Count);
            Assert.Equal(T0.AddSeconds(40), segments[3].EndedAt);
            Assert.Equal(SegmentName(40), segments[4].Path);
            Assert.Equal(T0.AddSeconds(50), segments[4].EndedAt);
            Assert.Null(segments[5].EndedAt);

            // Everything that has left the window is still prunable, including what arrived while
            // stopping. The trailing file OBS emits when the output closes is the newest entry and
            // pruning leaves it alone.
            Assert.Equal(
                new[] { SegmentName(0), SegmentName(10), SegmentName(20), SegmentName(30), SegmentName(40) },
                CandidatePaths(ledger.CollectPrunable(T0.AddSeconds(4000))));
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
