using System;
using System.Collections.Generic;
using Segra.Backend.Recorder;
using Xunit;

namespace Segra.Tests.Recorder
{
    /// <summary>
    /// Where a bookmark recorded at wall-clock time lands inside a saved disk-buffer replay.
    ///
    /// Stream-copy concat plays the written segments back to back: every junction between two files
    /// adds dead timeline (measured 19-29 ms per junction, 4.2-5.2 s over a real 30-minute save), and a
    /// segment the selection dropped is footage that is not in the file at all. The wall-clock offset
    /// counts both the junctions and the hole, so the resolver has to take the hole back out. Positions
    /// are left unclamped - a bookmark outside the saved window comes back negative or past the end and
    /// the caller's range filter drops it.
    /// </summary>
    public class DiskBufferBookmarkResolverTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 4, 19, 0, 0);

        // 40 s of footage written as 0-10, 10-20, [20-30 dropped by the 1 MiB floor], 30-40, 40-50.
        // Measured output for exactly this shape: 40.075 s, i.e. 3 junctions x 25 ms.
        private static readonly TimeSpan HoleOutputDuration = TimeSpan.FromMilliseconds(40_075);
        private static readonly TimeSpan HoleContentSpan = TimeSpan.FromSeconds(40);

        private static DiskBufferSegment Segment(int startSeconds, int endSeconds) =>
            new DiskBufferSegment($"C:/buffer/seg_{T0.AddSeconds(startSeconds):yyyy-MM-dd_HH-mm-ss}.mkv")
            {
                StartedAt = T0.AddSeconds(startSeconds),
                EndedAt = T0.AddSeconds(endSeconds)
            };

        private static List<DiskBufferSegment> Written() => new()
        {
            Segment(0, 10),
            Segment(10, 20),
            Segment(30, 40),
            Segment(40, 50)
        };

        private static List<DiskBufferSegment> Hole() => new() { Segment(20, 30) };

        // Same 40 s of footage with nothing missing: 0-10, 10-20, 20-30, 30-40.
        private static List<DiskBufferSegment> WrittenContinuous() => new()
        {
            Segment(0, 10),
            Segment(10, 20),
            Segment(20, 30),
            Segment(30, 40)
        };

        private static DiskBufferBookmarkResolver AroundAHole() =>
            new DiskBufferBookmarkResolver(Written(), Hole(), T0, HoleOutputDuration, HoleContentSpan);

        [Fact]
        public void PaddingPerJunctionIsNotInflatedByAHoleInTheWindow()
        {
            // 40.075 s of file for 40 s of playable content is 75 ms of dead timeline over the three
            // junctions between the four files that were written - 25 ms each. Counting the dropped
            // segment as a junction would give 18.75 ms, and counting its footage as content would
            // read as no padding at all.
            Assert.Equal(0.025, AroundAHole().PaddingPerJunctionSeconds, 6);
        }

        [Fact]
        public void ABookmarkBeforeTheHoleIsUnaffected()
        {
            var resolver = AroundAHole();

            // 5 s into the window, inside the first file: no junction crossed, no hole passed.
            Assert.Equal(TimeSpan.FromSeconds(5), resolver.PositionFor(T0.AddSeconds(5)));

            // 15 s in: one junction crossed, still nothing missing.
            Assert.Equal(TimeSpan.FromMilliseconds(15_025), resolver.PositionFor(T0.AddSeconds(15)));
        }

        [Fact]
        public void ABookmarkAfterTheHoleLandsWhereTheMissingFootageSaysItShould()
        {
            // 45 s after the save opened, 5 s into the last written file. The output holds 10 + 10 of
            // footage before the hole and 10 + 5 after it, so the footage offset is 35 s, plus the
            // three junctions crossed (10, 20 and 40 s boundaries) at 25 ms each.
            Assert.Equal(TimeSpan.FromMilliseconds(35_075), AroundAHole().PositionFor(T0.AddSeconds(45)));
        }

        [Fact]
        public void AStubOutsideTheSavedWindowShiftsNothing()
        {
            // The 1,018,129 B remnant the harness measured sat before the window of every later save in
            // that run. Its footage was never going to be in the file and the wall-clock offset never
            // counted it either, so taking its 10 s span back would move every bookmark in the window
            // 10 s early. Here the window's footage is continuous: 25 s in is 25 s of content plus the
            // two junctions already crossed.
            var resolver = new DiskBufferBookmarkResolver(
                WrittenContinuous(),
                new List<DiskBufferSegment> { Segment(-10, 0) },
                T0,
                HoleOutputDuration,
                HoleContentSpan);

            Assert.Equal(TimeSpan.FromMilliseconds(25_050), resolver.PositionFor(T0.AddSeconds(25)));
        }

        [Fact]
        public void ABookmarkOutsideTheSavedWindowIsLeftUnclamped()
        {
            var resolver = AroundAHole();

            // 5 s before the window opened: negative, and the caller's range filter drops it.
            Assert.Equal(TimeSpan.FromSeconds(-5), resolver.PositionFor(T0.AddSeconds(-5)));

            // 20 s after the saved footage ends: 70 s of wall clock, minus the 10 s hole, plus the four
            // closed segments' junctions - 60.1 s against a 40.075 s file. Left past the end instead of
            // parked at it, which would sit a post-save bookmark on the last frame of the replay.
            Assert.Equal(TimeSpan.FromMilliseconds(60_100), resolver.PositionFor(T0.AddSeconds(70)));
        }

        [Fact]
        public void TheMeasuredPaddingOfRealSavesStaysInsideTheMeasuredBand()
        {
            // Harness run 3: an 1800 s window saved as 181 segments and probed at 1805.2 s.
            // Harness run 2: the same recording at a 300 s window, 31 segments, 300.7 s.
            // Both are reported as 19-29 ms per junction; 5.2 s / 180 and 0.7 s / 30.
            var longSave = new DiskBufferBookmarkResolver(
                Tiling(181), new List<DiskBufferSegment>(), T0,
                TimeSpan.FromMilliseconds(1_805_200), TimeSpan.FromSeconds(1800));
            var shortSave = new DiskBufferBookmarkResolver(
                Tiling(31), new List<DiskBufferSegment>(), T0,
                TimeSpan.FromMilliseconds(300_700), TimeSpan.FromSeconds(300));

            Assert.InRange(longSave.PaddingPerJunctionSeconds, 0.019, 0.029);
            Assert.InRange(shortSave.PaddingPerJunctionSeconds, 0.019, 0.029);
        }

        // n closed segments tiling [-5 s, n*10 - 5 s] in ledger order: a window opening at T0 with a
        // 5 s inpoint on the first file covers exactly n*10 - 5 s of wall clock, so contentSpan is the
        // requested window and only the file count drives the junctions.
        private static List<DiskBufferSegment> Tiling(int count)
        {
            var segments = new List<DiskBufferSegment>();
            for (int i = 0; i < count; i++)
                segments.Add(Segment(i * 10 - 5, i * 10 + 5));
            return segments;
        }
    }
}
