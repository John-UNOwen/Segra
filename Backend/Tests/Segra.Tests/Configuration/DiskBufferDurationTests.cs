using Segra.Backend.Core.Models;
using Xunit;

namespace Segra.Tests.Configuration
{
    /// <summary>
    /// The disk buffer window is 30 s to 30 min everywhere it is read: a per-game override is used
    /// directly as the prune age and the save window, so it cannot carry an out-of-range number.
    /// </summary>
    public class DiskBufferDurationTests
    {
        [Theory]
        [InlineData(20000, 1800)]
        [InlineData(0, 30)]
        [InlineData(-5, 30)]
        [InlineData(45, 45)]
        public void PerGameWindowIsClampedToTheSupportedRange(int supplied, int expected)
        {
            var overrideSettings = new GameRecordingModeOverride { DiskBufferDuration = supplied };

            Assert.Equal(expected, overrideSettings.DiskBufferDuration);
        }

        [Fact]
        public void AbsentPerGameWindowStaysAbsent()
        {
            var overrideSettings = new GameRecordingModeOverride();

            Assert.Null(overrideSettings.DiskBufferDuration);
        }
    }
}
