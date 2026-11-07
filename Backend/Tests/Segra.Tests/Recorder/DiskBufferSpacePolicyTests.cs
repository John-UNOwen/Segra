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

            // A save pins the footage it reads and writes its output into the folder being guarded, so
            // nothing counts as reclaimed during that interval.
            Assert.Equal(270_000_000, DiskBufferSpacePolicy.GrowthBytesPerInterval(Monitoring(4_500_000, saveInFlight: true)));
        }
    }
}
