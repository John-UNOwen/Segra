using Segra.Backend.Recorder;
using Xunit;

namespace Segra.Tests.Recorder
{
    /// <summary>
    /// The buffer folder a user configures is the folder the startup purge deletes everything under,
    /// so the value that reaches settings must be a safe, canonical, absolute folder that is not
    /// tangled with the library. Rules from docs/disk-buffer-plan.md.
    /// </summary>
    public class DiskBufferPathPolicyTests
    {
        private const string ContentFolder = "C:/Users/me/Videos/Segra";

        [Fact]
        public void BufferFolderResolvingToADriveRootIsRefused()
        {
            // Path.IsPathFullyQualified accepts this and the settings setter only swaps slashes, so
            // today it is stored verbatim and resolved by the OS to "D:/".
            bool accepted = DiskBufferPathPolicy.TryResolve("D:/Temp/..", ContentFolder, out string root, out string reason);

            Assert.False(accepted);
            Assert.NotEmpty(reason);
        }

        [Theory]
        [InlineData("Full Sessions")]
        [InlineData("Replay Buffers")]
        [InlineData("Clips")]
        [InlineData("Highlights")]
        public void BufferFolderInsideALibraryFolderIsRefused(string libraryFolder)
        {
            // Segments living under a library folder would be scanned as content and deleted by the
            // storage-limit cleanup, and the startup purge would run inside it.
            string candidate = ContentFolder + "/" + libraryFolder + "/segments";

            bool accepted = DiskBufferPathPolicy.TryResolve(candidate, ContentFolder, out string root, out string reason);

            Assert.False(accepted);
            Assert.Contains(libraryFolder, reason);
        }
    }
}
