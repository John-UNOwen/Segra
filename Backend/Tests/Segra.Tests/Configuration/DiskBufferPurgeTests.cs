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

        [Fact]
        public void PurgeAllRemovesAStagedSaveTheAppNeverFinished()
        {
            // A hard kill mid-concat leaves a multi-GB staged save behind: ffmpeg and the muxer outlive
            // Segra and finish writing it after the app is gone. It is not a segment and OBS never named
            // it, so the purge only recognises it by the interrupted-save marker it was staged under.
            string root = Path.Combine(_dir, "stagedbuffer");
            string gameFolder = Path.Combine(root, "Manual Recording");
            Directory.CreateDirectory(gameFolder);
            Directory.CreateDirectory(Path.Combine(root, "someone elses folder"));
            File.WriteAllText(Path.Combine(root, "note.txt"), "keep me");
            File.WriteAllText(Path.Combine(root, "someone elses folder", "clip.mp4"), "keep me too");

            string staged = Path.Combine(gameFolder, "2026-11-04_19-46-26.mp4" + DiskBufferSaveAssembly.InterruptedSaveMarker);
            File.WriteAllText(staged, "half a replay");

            Settings.Instance.ContentFolder = PathUtilsNormalize(_dir);
            Settings.Instance.DiskBufferFolder = PathUtilsNormalize(root);

            DiskBufferService.PurgeAll();

            Assert.False(File.Exists(staged));
            Assert.True(File.Exists(Path.Combine(root, "note.txt")));
            Assert.True(File.Exists(Path.Combine(root, "someone elses folder", "clip.mp4")));
        }

        [Fact]
        public void APurgeThatCouldNotDeleteSomethingDoesNotSayItDid()
        {
            // A file the muxer or a scanner still holds, or one marked read-only: the delete fails, the
            // partial stays, and the next start has to be told there is still something to remove.
            string root = Path.Combine(_dir, "stuckbuffer");
            Directory.CreateDirectory(root);
            string staged = Path.Combine(root, "2026-11-04_19-46-26.mp4" + DiskBufferSaveAssembly.InterruptedSaveMarker);
            File.WriteAllText(staged, "half a replay");
            File.SetAttributes(staged, FileAttributes.ReadOnly);

            Settings.Instance.ContentFolder = PathUtilsNormalize(_dir);
            Settings.Instance.DiskBufferFolder = PathUtilsNormalize(root);

            DiskBufferPurgeReport report = DiskBufferService.PurgeAll();

            Assert.Equal(0, report.DeletedFiles);
            Assert.Equal(1, report.UndeletedFiles);
            Assert.True(File.Exists(staged));
        }

        private static string PathUtilsNormalize(string path) => path.Replace('\\', '/');

        public void Dispose()
        {
            Settings.Instance.DiskBufferFolder = _bufferBefore;
            Settings.Instance.ContentFolder = _contentBefore;
            Settings.Instance._isBulkUpdating = _bulkBefore;
            try
            {
                foreach (string file in Directory.GetFiles(_dir, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
            }
            catch { /* temp scratch */ }
            try { Directory.Delete(_dir, true); } catch { /* temp scratch */ }
        }
    }
}
