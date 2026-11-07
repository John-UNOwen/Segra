using System;
using System.Collections.Generic;
using System.IO;
using Segra.Backend.Core.Models;
using Segra.Backend.Recorder;
using Segra.Backend.Shared;
using Xunit;

namespace Segra.Tests.Configuration
{
    /// <summary>
    /// The settings screen lets the buffer folder change at any time, with no recording running and no
    /// guard at all, but the folder a recording writes into is fixed when that recording starts. Leftovers
    /// in a folder the app recorded into and then moved out of are invisible to the live setting: they are
    /// never purged, they count as content usage, and the storage-limit cleanup then deletes the oldest
    /// real Full Sessions and Replay Buffers files to free room the leftovers cannot free.
    /// </summary>
    [Collection("Settings")]
    public class DiskBufferRecordedRootTests : IDisposable
    {
        private readonly string _contentBefore = Settings.Instance.ContentFolder;
        private readonly string _bufferBefore = Settings.Instance.DiskBufferFolder;
        private readonly List<string> _recordedBefore = Settings.Instance.DiskBufferRecordedFolders;
        private readonly bool _bulkBefore = Settings.Instance._isBulkUpdating;
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "segra-tests-roots-" + Guid.NewGuid().ToString("N"));

        public DiskBufferRecordedRootTests()
        {
            Directory.CreateDirectory(_dir);
            // Bulk update suppresses the settings save the content folder setter would trigger.
            Settings.Instance._isBulkUpdating = true;
        }

        [Fact]
        public void AFolderRecordingMovedOutOfIsStillRecognisedAsTheBuffer()
        {
            UseBufferFolders("buffer-new", "buffer-old");

            Assert.True(FolderNames.IsDiskBufferPath(
                Path.Combine(_dir, "buffer-old", "Manual Recording", "seg_2026-11-04_19-45-26.mkv")));

            // Recognition is per root, not a wildcard: footage and anything else outside those two folders
            // stays ordinary content.
            Assert.True(FolderNames.IsDiskBufferPath(Path.Combine(_dir, "buffer-new", "seg_2026-11-04_19-46-26.mkv")));
            Assert.False(FolderNames.IsDiskBufferPath(Path.Combine(_dir, "Replay Buffers", "Some Game", "2026-11-04_19-46-26.mp4")));
            Assert.False(FolderNames.IsDiskBufferPath(Path.Combine(_dir, "elsewhere", "clip.mp4")));
        }

        [Fact]
        public void TheStartupPurgeClearsWhatAnOlderBufferFolderStillHolds()
        {
            string recorded = Path.Combine(_dir, "buffer-old");
            Directory.CreateDirectory(Path.Combine(recorded, "Manual Recording"));
            string stale = Path.Combine(recorded, "Manual Recording", "seg_2026-11-04_19-45-26.mkv");
            File.WriteAllText(stale, "stale segment");
            File.WriteAllText(Path.Combine(recorded, "note.txt"), "keep me");

            UseBufferFolders("buffer-new", "buffer-old");

            DiskBufferPurgeReport report = DiskBufferService.PurgeAll();

            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(Path.Combine(recorded, "note.txt")));
            Assert.Equal(1, report.DeletedFiles);
            // "stale segment" is 13 bytes.
            Assert.Equal(13, report.ReclaimedBytes);
        }

        [Fact]
        public void ABufferFolderThatNoLongerExistsIsSkipped()
        {
            string live = Path.Combine(_dir, "buffer-new");
            Directory.CreateDirectory(Path.Combine(live, "Manual Recording"));
            string liveSegment = Path.Combine(live, "Manual Recording", "seg_2026-11-04_19-45-26.mkv");
            File.WriteAllText(liveSegment, "leftover in the live folder");

            UseBufferFolders("buffer-new", "buffer-gone");

            DiskBufferPurgeReport report = DiskBufferService.PurgeAll();

            Assert.False(File.Exists(liveSegment));
            Assert.Equal(1, report.DeletedFiles);
            Assert.Equal(0, report.UndeletedFiles);
        }

        private void UseBufferFolders(string liveName, string recordedName)
        {
            Settings.Instance.ContentFolder = Normalize(_dir);
            Settings.Instance.DiskBufferFolder = Normalize(Path.Combine(_dir, liveName));
            Settings.Instance.DiskBufferRecordedFolders = new List<string> { Normalize(Path.Combine(_dir, recordedName)) };
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
