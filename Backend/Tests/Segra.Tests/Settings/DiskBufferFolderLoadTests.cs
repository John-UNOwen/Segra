using System;
using System.IO;
using Segra.Backend.Core;
using Segra.Backend.Core.Models;
using Xunit;

namespace Segra.Tests.Configuration
{
    /// <summary>
    /// settings.json is read property by property and assigned onto the live settings, so an unsafe
    /// buffer folder arriving through a file (or an older file, or a hand-seeded one) has to be
    /// caught by the load itself, not only by the settings screen.
    /// </summary>
    [Collection("Settings")]
    public class DiskBufferFolderLoadTests : IDisposable
    {
        private readonly string _bufferFolderBefore = Settings.Instance.DiskBufferFolder;
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "segra-tests-load-" + Guid.NewGuid().ToString("N"));

        public DiskBufferFolderLoadTests() => Directory.CreateDirectory(_dir);

        [Fact]
        public void BufferFolderResolvingToADriveRootIsDroppedOnLoad()
        {
            string settingsPath = Path.Combine(_dir, "settings.json");
            File.WriteAllText(settingsPath, """
                { "diskBufferFolder": "D:/Temp/.." }
                """);

            Assert.True(SettingsService.TryLoadSettingsFile(settingsPath));

            // Dropped back to the derived default instead of being used as a drive root.
            Assert.Equal(string.Empty, Settings.Instance.DiskBufferFolder);
        }

        public void Dispose()
        {
            Settings.Instance.DiskBufferFolder = _bufferFolderBefore;
            try { Directory.Delete(_dir, true); } catch { /* temp scratch */ }
        }
    }
}
