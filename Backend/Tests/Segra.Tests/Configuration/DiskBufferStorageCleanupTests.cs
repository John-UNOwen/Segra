using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Segra.Backend.Core.Models;
using Segra.Backend.Shared;
using Segra.Backend.Windows.Storage;
using Xunit;

namespace Segra.Tests.Configuration
{
    /// <summary>
    /// When the drive runs low mid-recording, the storage cleanup deletes the oldest Full Sessions and
    /// Replay Buffers files, walking those folders recursively. A disk buffer keeps its rolling segments and
    /// a staged save as ordinary files on disk, and a save can be mid-write while that cleanup runs. The
    /// buffer folders - wherever they point, including inside the library - must never be candidates.
    /// </summary>
    [Collection("Settings")]
    public class DiskBufferStorageCleanupTests : IDisposable
    {
        private readonly string _contentBefore = Settings.Instance.ContentFolder;
        private readonly string _bufferBefore = Settings.Instance.DiskBufferFolder;
        private readonly List<string> _recordedBefore = Settings.Instance.DiskBufferRecordedFolders;
        private readonly bool _bulkBefore = Settings.Instance._isBulkUpdating;
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "segra-tests-cleanup-" + Guid.NewGuid().ToString("N"));

        public DiskBufferStorageCleanupTests()
        {
            Directory.CreateDirectory(_dir);
            Settings.Instance._isBulkUpdating = true;
            Settings.Instance.ContentFolder = Normalize(_dir);
        }

        [Fact]
        public void SegmentsAndAStagedSaveAreNeverDeletionCandidates()
        {
            string oldInside = WriteOld(Path.Combine(_dir, "Replay Buffers", "Some Game", "2026-11-04_19-00-00.mp4"));
            string oldFlat = WriteOld(Path.Combine(_dir, "Replay Buffers", "2026-11-04_19-01-00.mp4"));
            WriteRecent(Path.Combine(_dir, "Replay Buffers", "Some Game", "2026-11-07_19-00-00.mp4"));

            // A buffer folder the user pointed inside the library before the folder rule existed, and the
            // default one outside it. Both hold a segment and a half-written save.
            string rootInside = Path.Combine(_dir, "Replay Buffers", "Some Game", ".buffer-here");
            string rootOutside = Path.Combine(_dir, ".diskbuffer", "Some Game");
            foreach (string root in new[] { rootInside, rootOutside })
            {
                WriteOld(Path.Combine(root, "seg_2026-11-04_19-46-26.mkv"));
                WriteOld(Path.Combine(root, "2026-11-04_19-46-30.mp4.segrereplay"));
            }

            Settings.Instance.DiskBufferRecordedFolders = new List<string>
            {
                Normalize(rootInside),
                Normalize(rootOutside),
            };

            List<string> candidates = StorageService.GetDeletionCandidates(_dir, null)
                .Select(f => Normalize(f.FullName))
                .ToList();

            Assert.Contains(oldInside.Replace('\\', '/'), candidates);
            Assert.Contains(oldFlat.Replace('\\', '/'), candidates);
            Assert.All(candidates, c =>
            {
                Assert.False(c.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase));
                Assert.False(c.EndsWith(".segrereplay", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(".diskbuffer", c, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(".buffer-here", c, StringComparison.OrdinalIgnoreCase);
            });
        }

        [Fact]
        public void AReplaySavedWithoutGameSubfoldersIsStillContent()
        {
            string flat = Path.Combine(_dir, "Replay Buffers", "2026-11-07_05-45-16.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(flat)!);
            File.WriteAllText(flat, "replay");

            Assert.Equal(
                Content.ContentType.Buffer,
                FolderNames.GetContentTypeFromPath(Normalize(flat)));

            string segment = Path.Combine(_dir, ".diskbuffer", "Manual Recording", "seg_2026-11-07_05-45-16.mkv");
            Settings.Instance.DiskBufferRecordedFolders = new List<string> { Normalize(Path.Combine(_dir, ".diskbuffer")) };

            Assert.Null(FolderNames.GetContentTypeFromPath(Normalize(segment)));
        }

        private string WriteOld(string path)
        {
            WriteRecent(path);
            DateTime old = DateTime.Now.AddHours(-3);
            File.SetLastWriteTime(path, old);
            File.SetCreationTime(path, old);
            return path;
        }

        private string WriteRecent(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
            return path;
        }

        private static string Normalize(string path) => path.Replace('\\', '/');

        public void Dispose()
        {
            Settings.Instance.DiskBufferRecordedFolders = _recordedBefore;
            Settings.Instance.DiskBufferFolder = _bufferBefore;
            Settings.Instance.ContentFolder = _contentBefore;
            Settings.Instance._isBulkUpdating = _bulkBefore;
            try { Directory.Delete(_dir, true); } catch { /* temp scratch */ }
        }
    }
}
