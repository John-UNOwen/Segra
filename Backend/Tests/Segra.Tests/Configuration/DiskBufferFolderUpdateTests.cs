using System;
using System.IO;
using Segra.Backend.Core;
using Segra.Backend.Core.Models;
using Xunit;

namespace Segra.Tests.Configuration
{
    /// <summary>
    /// The settings screen may move the buffer folder or the content folder at any time, with a
    /// recording running or not. The update path has to hold the buffer folder to the same rule the
    /// load path uses, and a content folder moved inside the folder the buffer is using has to cost
    /// the buffer folder rather than the library.
    /// </summary>
    [Collection("Settings")]
    public class DiskBufferFolderUpdateTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "segra-update-" + Guid.NewGuid().ToString("N"));
        private readonly string _contentBefore = Settings.Instance.ContentFolder;
        private readonly string _bufferBefore = Settings.Instance.DiskBufferFolder;
        private readonly bool _bulkBefore = Settings.Instance._isBulkUpdating;

        public DiskBufferFolderUpdateTests()
        {
            Directory.CreateDirectory(_dir);
            Settings.Instance._isBulkUpdating = true;
        }

        [Fact]
        public void ABufferFolderInsideTheLibraryIsRefusedAndThePreviousValueSurvives()
        {
            Settings.Instance.ContentFolder = Slash(_dir);
            string previous = Slash(Path.Combine(_dir, "buffer"));
            Settings.Instance.DiskBufferFolder = previous;

            bool applied = SettingsService.TryApplyDiskBufferFolderChange(
                Settings.Instance, Slash(Path.Combine(_dir, "Replay Buffers")), "test");

            Assert.False(applied);
            Assert.Equal(previous, Settings.Instance.DiskBufferFolder);
        }

        [Fact]
        public void ASafeBufferFolderIsStoredInCanonicalForm()
        {
            string requested = Slash(Path.Combine(_dir, "buf", "..", "buf2"));

            Assert.True(SettingsService.TryApplyDiskBufferFolderChange(Settings.Instance, requested, "test"));
            Assert.Equal(Slash(Path.Combine(_dir, "buf2")), Settings.Instance.DiskBufferFolder);
        }

        [Fact]
        public void ABufferFolderThatResolvesToADriveRootIsRefused()
        {
            Settings.Instance.DiskBufferFolder = "D:/buffer";

            Assert.False(SettingsService.TryApplyDiskBufferFolderChange(Settings.Instance, "D:/Temp/..", "test"));
            Assert.Equal("D:/buffer", Settings.Instance.DiskBufferFolder);
        }

        [Fact]
        public void MovingTheContentFolderInsideTheBufferFolderClearsTheBufferFolder()
        {
            string buffer = Slash(Path.Combine(_dir, "buffer"));
            Directory.CreateDirectory(Path.Combine(buffer, "Segra"));
            Settings.Instance.DiskBufferFolder = buffer;
            Settings.Instance.ContentFolder = Slash(Path.Combine(buffer, "Segra"));

            Assert.True(SettingsService.SanitizeDiskBufferFolder(Settings.Instance, "content folder moved"));
            Assert.Equal(string.Empty, Settings.Instance.DiskBufferFolder);
        }

        private static string Slash(string path) => path.Replace('\\', '/');

        public void Dispose()
        {
            Settings.Instance.DiskBufferFolder = _bufferBefore;
            Settings.Instance.ContentFolder = _contentBefore;
            Settings.Instance._isBulkUpdating = _bulkBefore;
            try { Directory.Delete(_dir, true); } catch { /* temp scratch */ }
        }
    }
}
