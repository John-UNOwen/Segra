using Segra.Backend.Recorder;
using Xunit;

namespace Segra.Tests.Recorder
{
    /// <summary>
    /// The rolling disk buffer's space arithmetic, pulled out of OBSService so the numbers can be
    /// argued with directly instead of through global settings. Rules from docs/disk-buffer-plan.md:
    /// the folder only grows by what pruning fails to reclaim, a save pauses that reclaim, and the
    /// low-space threshold has to cover the save output the buffer is going to concatenate into the
    /// very folder it is guarding. RAM buffer modes keep their own threshold and are not here.
    /// </summary>
    public class DiskBufferSpacePolicyTests
    {
        // The 1080p60 / CBR 35000 case: 35 Mbps of video plus 1 Mbps of audio tracks = 4,500,000 B/s.
        private const long WriteBytesPerSecond = 4_500_000;
        private const long IntervalSeconds = 60;
        private const long WindowSeconds = 1800;
        private const long FloorBytes = 250L * 1024 * 1024;

        private static DiskBufferMonitorSample Monitoring(double reclaimedBytesPerSecond, bool saveInFlight = false) =>
            new(WriteBytesPerSecond, IntervalSeconds, reclaimedBytesPerSecond, WindowSeconds, saveInFlight);

        [Fact]
        public void TheBufferGrowsOnlyByWhatPruningFailedToReclaim()
        {
            // Pruning keeps up exactly: a 60 s interval writes 270,000,000 B and reclaims the same.
            Assert.Equal(0, DiskBufferSpacePolicy.GrowthBytesPerInterval(Monitoring(4_500_000)));

            // Pruning reclaims half of it.
            Assert.Equal(135_000_000, DiskBufferSpacePolicy.GrowthBytesPerInterval(Monitoring(2_250_000)));

            // Nothing reclaimed at all: the whole interval is new footage.
            Assert.Equal(270_000_000, DiskBufferSpacePolicy.GrowthBytesPerInterval(Monitoring(0)));

            // Reclaim cannot be credited beyond what was written: the buffer never grows backwards.
            Assert.Equal(0, DiskBufferSpacePolicy.GrowthBytesPerInterval(Monitoring(9_000_000)));

            // A save pins the footage it reads and writes its concat output into the folder being guarded, so
            // nothing counts as reclaimed during that interval.
            Assert.Equal(270_000_000, DiskBufferSpacePolicy.GrowthBytesPerInterval(Monitoring(4_500_000, saveInFlight: true)));
        }

        [Fact]
        public void TheLowSpaceThresholdReservesRoomForTheSaveItWillWrite()
        {
            // Healthy steady state: pruning keeps up exactly, so the segment folder itself is not
            // growing. Pressing save still concatenates the whole 1800 s window into one file, staged
            // inside the folder the monitor is guarding: 4,500,000 B/s x 1800 s, plus the same 15% mux
            // margin the recording-start preflight uses, is 9,315,000,000 B. Add the 128 MB (134,217,728 B)
            // needed to finalize the recording's own file.
            long threshold = DiskBufferSpacePolicy.FreeSpaceThresholdBytes(Monitoring(4_500_000), FloorBytes);

            Assert.Equal(9_315_000_000 + 134_217_728, threshold);
        }

        [Fact]
        public void TheLowSpaceThresholdAlsoCoversWhatTheFolderIsGrowingBy()
        {
            // Pruning reclaiming only half the write rate leaves the folder gaining 135,000,000 B per
            // interval. The threshold covers 1.5x that (202,500,000 B) so a whole interval of writing
            // cannot burn the headroom before the next check, plus the finalization buffer and the
            // save output.
            long threshold = DiskBufferSpacePolicy.FreeSpaceThresholdBytes(Monitoring(2_250_000), FloorBytes);

            Assert.Equal(202_500_000 + 134_217_728 + 9_315_000_000, threshold);
        }

        [Fact]
        public void TheLowSpaceThresholdNeverDropsBelowTheFloor()
        {
            // A short window at a low bitrate reserves less than the absolute floor: 50,000 B/s over
            // 30 s plus 15% is 1,725,000 B, the finalization buffer is 134,217,728 B, and the folder is
            // not growing. The floor stands at 250 MB (262,144,000 B).
            var sample = new DiskBufferMonitorSample(50_000, 60, 50_000, 30, SaveInFlight: false);

            Assert.Equal(FloorBytes, DiskBufferSpacePolicy.FreeSpaceThresholdBytes(sample, FloorBytes));
        }
    }
}
