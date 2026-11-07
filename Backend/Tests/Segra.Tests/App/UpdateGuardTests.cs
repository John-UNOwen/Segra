using Segra.Backend.App;
using Xunit;

namespace Segra.Tests.App
{
    /// <summary>
    /// The fork is not published to the upstream release feed. Nothing in the app may replace the
    /// running build with one fetched from upstream, whatever path the update was reached from.
    /// </summary>
    public class UpdateGuardTests
    {
        [Fact]
        public void ThisBuildHasNoUpstreamFeedToUpdateFrom()
        {
            // Guards every update path, including the OBS-version reinstall that bypasses the
            // startup update check.
            Assert.False(BuildProvenance.UpdateFeedIsUsable);
        }
    }
}
