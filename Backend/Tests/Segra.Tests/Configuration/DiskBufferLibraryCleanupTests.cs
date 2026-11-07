using System;
using System.IO;
using Segra.Backend.Core.Models;
using Segra.Backend.Recorder;
using Segra.Backend.Shared;
using Xunit;

namespace Segra.Tests.Configuration
{
    /// <summary>
    /// What a save killed mid-publish leaves in the library: a copy under the interrupted-save marker,
    /// with no mp4 extension in front of it any more, so nothing but a sweep that looks for the marker
    /// itself will ever remove it. A kill during a cross-volume move put one of these next to real
    /// footage, and a genuine replay and its sidecars have to survive the sweep that follows.
    /// </summary>
    [Collection("Settings")]
    public class DiskBufferLibraryCleanupTests : IDisposable
    {
        private readonly string _contentBefore = Settings.Instance.ContentFolder;
        private readonly bool _bulkBefore = Settings.Instance._isBulkUpdating;
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "segra-tests-library-cleanup-" + Guid.NewGuid().ToString("N"));

        public DiskBufferLibraryCleanupTests()
        {
            Directory.CreateDirectory(_dir);
            // Bulk update suppresses the settings save the content folder setter would trigger.
            Settings.Instance._isBulkUpdating = true;
        }

        [Fact]
        public void TheStartupSweepRemovesMarkerPartialsAndKeepsRealContent()
        {
            string buffers = Path.Combine(_dir, "Replay Buffers", "Manual Recording");
            string sessions = Path.Combine(_dir, "Full Sessions", "Some Game");
            Directory.CreateDirectory(buffers);
            Directory.CreateDirectory(sessions);

            // The two shapes an interrupted publish leaves: a staged mp4 that picked up the marker, and
            // a marker file that never had an mp4 in front of it.
            string markerMp4 = Path.Combine(buffers, "2026-13-99_00-00-00.mp4" + DiskBufferSaveAssembly.InterruptedSaveMarker);
            string markerBare = Path.Combine(buffers, "2026-13-99_00-00-00" + DiskBufferSaveAssembly.InterruptedSaveMarker);
            File.WriteAllText(markerMp4, "half a replay");
            File.WriteAllText(markerBare, "half a replay");

            string realReplay = Path.Combine(buffers, "2026-11-04_19-46-26.mp4");
            File.WriteAllText(realReplay, "a saved replay");
            File.WriteAllText(Path.Combine(buffers, "2026-11-04_19-46-26.jpeg"), "thumbnail");
            File.WriteAllText(Path.Combine(buffers, "2026-11-04_19-46-26.peaks.json"), "waveform");

            string realSession = Path.Combine(sessions, "2026-11-01_10-00-00.mp4");
            File.WriteAllText(realSession, "a full session");
            File.WriteAllText(Path.Combine(sessions, "notes.txt"), "keep me");

            Settings.Instance.ContentFolder = PathUtils.Normalize(_dir);

            DiskBufferSaveAssembly.CleanupInterruptedLibrarySaves();

            Assert.False(File.Exists(markerMp4));
            Assert.False(File.Exists(markerBare));
            Assert.True(File.Exists(realReplay));
            Assert.True(File.Exists(Path.Combine(buffers, "2026-11-04_19-46-26.jpeg")));
            Assert.True(File.Exists(Path.Combine(buffers, "2026-11-04_19-46-26.peaks.json")));
            Assert.True(File.Exists(realSession));
            Assert.True(File.Exists(Path.Combine(sessions, "notes.txt")));
        }

        public void Dispose()
        {
            Settings.Instance.ContentFolder = _contentBefore;
            Settings.Instance._isBulkUpdating = _bulkBefore;
            try { Directory.Delete(_dir, true); } catch { /* temp scratch */ }
        }
    }
}
