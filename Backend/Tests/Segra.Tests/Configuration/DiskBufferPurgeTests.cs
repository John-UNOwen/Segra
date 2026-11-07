using System;
using System.IO;
using Segra.Backend.Core.Models;
using Segra.Backend.Recorder;
using Xunit;

namespace Segra.Tests.Configuration
{
    /// <summary>
    /// The startup purge deletes everything under the buffer folder. A folder the user picked is
    /// only allowed to hold segments: anything else it contains has to survive.
    /// </summary>
    public class DiskBufferPurgeTests : IDisposable
    {
        private readonly string _contentBefore = Settings.Instance.ContentFolder;
        private readonly string _bufferBefore = Settings.Instance.DiskBufferFolder;
        private readonly bool _bulkBefore = Settings.Instance._isBulkUpdating;
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "segra-tests-purge-" + Guid.NewGuid().ToString("N"));

        public DiskBufferPurgeTests()
        {
            Directory.CreateDirectory(_dir);
            // Bulk update suppresses the settings save the content folder setter would trigger.
            Settings.Instance._isBulkUpdating = true;
        }

        [Fact]
        public void PurgeAllRemovesOnlySegmentFiles()
        {
            string root = Path.Combine(_dir, "buffer");
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            Directory.CreateDirectory(Path.Combine(root, "Manual Recording"));
            File.WriteAllText(Path.Combine(root, "note.txt"), "keep me");
            File.WriteAllText(Path.Combine(root, "sub", "keep.txt"), "keep me too");
            File.WriteAllText(Path.Combine(root, "Manual Recording", "seg_2026-01-01_00-00-00.mkv"), "stale segment");

            Settings.Instance.ContentFolder = PathUtilsNormalize(_dir);
            Settings.Instance.DiskBufferFolder = PathUtilsNormalize(root);

            DiskBufferService.PurgeAll();

            Assert.True(File.Exists(Path.Combine(root, "note.txt")));
            Assert.True(File.Exists(Path.Combine(root, "sub", "keep.txt")));
            Assert.False(File.Exists(Path.Combine(root, "Manual Recording", "seg_2026-01-01_00-00-00.mkv")));
        }

        private static string PathUtilsNormalize(string path) => path.Replace('\\', '/');

        public void Dispose()
        {
            Settings.Instance.DiskBufferFolder = _bufferBefore;
            Settings.Instance.ContentFolder = _contentBefore;
            Settings.Instance._isBulkUpdating = _bulkBefore;
            try { Directory.Delete(_dir, true); } catch { /* temp scratch */ }
        }
    }
}
