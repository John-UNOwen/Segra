using Segra.Backend.Core.Models;

namespace Segra.Backend.Shared
{
    /// <summary>
    /// Centralized folder name constants and path helpers.
    /// This is the single source of truth for all folder names used in the application.
    /// </summary>
    public static class FolderNames
    {
        // Video content folder names
        public const string Sessions = "Full Sessions";
        public const string Buffers = "Replay Buffers";
        public const string Clips = "Clips";
        public const string Highlights = "Highlights";

        // Rolling disk buffer segment folder (default under the content folder). Not a content type:
        // nothing inside it is ever library content.
        public const string DiskBuffer = ".diskbuffer";

        // Legacy folder names (for migration purposes)
        public const string LegacySessions = "sessions";
        public const string LegacyBuffers = "buffers";
        public const string LegacyClips = "clips";
        public const string LegacyHighlights = "highlights";

        // Metadata folder names (stored in AppData)
        public const string Metadata = "metadata";
        public const string Thumbnails = "thumbnails";
        public const string Waveforms = "waveforms";

        // Legacy metadata folder names (with dot prefix, for migration)
        public const string LegacyMetadata = ".metadata";
        public const string LegacyThumbnails = ".thumbnails";
        public const string LegacyWaveforms = ".waveforms";

        /// <summary>
        /// Gets the cache folder path for Segra (metadata, thumbnails, waveforms).
        /// This is configurable via Settings, with default at C:\Users\{user}\AppData\Roaming\Segra
        /// </summary>
        public static string CacheFolder => PathUtils.Normalize(Settings.Instance.CacheFolder);

        /// <summary>
        /// Gets the root folder holding the disk buffer's per-game segment folders: the configured
        /// DiskBufferFolder, or the DiskBuffer folder under the content folder when none is set.
        /// </summary>
        public static string GetDiskBufferRoot()
        {
            string configured = Settings.Instance.DiskBufferFolder ?? "";
            if (!string.IsNullOrWhiteSpace(configured))
                return PathUtils.Normalize(configured);

            return PathUtils.Combine(Settings.Instance.ContentFolder, DiskBuffer);
        }

        /// <summary>
        /// Every folder the disk buffer has been written into: the root the settings point at now, plus
        /// the roots recordings actually wrote into before that setting moved. A recording fixes its folder
        /// at start while every consumer resolves the setting live, so without the recorded ones the
        /// leftovers of an older folder read as content. Canonicalized, live root first, no duplicates.
        /// </summary>
        public static List<string> GetDiskBufferRoots()
        {
            var roots = new List<string>();
            AddBufferRoot(roots, GetDiskBufferRoot());
            foreach (string recorded in Settings.Instance.DiskBufferRecordedFolders ?? new List<string>())
                AddBufferRoot(roots, recorded);
            return roots;
        }

        private static void AddBufferRoot(List<string> roots, string? candidate)
        {
            string root = NormalizeBufferRoot(candidate);
            if (root.Length == 0)
                return;
            if (!roots.Contains(root, StringComparer.OrdinalIgnoreCase))
                roots.Add(root);
        }

        // Path components are compared normalized: forward slashes, no trailing separator.
        private static string NormalizeBufferRoot(string? path) =>
            (path ?? string.Empty).Replace('\\', '/').TrimEnd('/');

        /// <summary>
        /// True when a path is a disk buffer root or lives under one - the live root or any root a
        /// recording wrote into. Compared on normalized path components, not against the folder name, so a
        /// buffer the user moved somewhere else is still recognized.
        /// </summary>
        public static bool IsDiskBufferPath(string path)
        {
            string candidate = NormalizeBufferRoot(path);
            if (candidate.Length == 0)
                return false;

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            foreach (string bufferRoot in GetDiskBufferRoots())
            {
                if (string.Equals(candidate, bufferRoot, comparison) ||
                    candidate.StartsWith(bufferRoot + "/", comparison))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Gets the video folder name for a content type.
        /// </summary>
        public static string GetVideoFolderName(Content.ContentType type)
        {
            return type switch
            {
                Content.ContentType.Session => Sessions,
                Content.ContentType.Buffer => Buffers,
                Content.ContentType.Clip => Clips,
                Content.ContentType.Highlight => Highlights,
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown content type")
            };
        }

        /// <summary>
        /// Gets the legacy video folder name for a content type (for migration purposes).
        /// </summary>
        public static string GetLegacyVideoFolderName(Content.ContentType type)
        {
            return type switch
            {
                Content.ContentType.Session => LegacySessions,
                Content.ContentType.Buffer => LegacyBuffers,
                Content.ContentType.Clip => LegacyClips,
                Content.ContentType.Highlight => LegacyHighlights,
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown content type")
            };
        }

        /// <summary>
        /// Gets the metadata subfolder name for a content type (e.g., "Full Sessions", "Clips").
        /// Uses the same names as video folders for consistency.
        /// </summary>
        public static string GetMetadataSubfolderName(Content.ContentType type)
        {
            return GetVideoFolderName(type);
        }

        /// <summary>
        /// Gets the full path to a video folder for a content type.
        /// </summary>
        public static string GetVideoFolderPath(string contentFolder, Content.ContentType type)
        {
            return PathUtils.Combine(contentFolder, GetVideoFolderName(type));
        }

        /// <summary>
        /// Gets the full path to the metadata folder for a content type.
        /// Metadata is stored in AppData/Roaming/Segra/metadata/{ContentType}
        /// </summary>
        public static string GetMetadataFolderPath(Content.ContentType type)
        {
            return PathUtils.Combine(CacheFolder, Metadata, GetMetadataSubfolderName(type));
        }

        /// <summary>
        /// Gets the full path to the thumbnails folder for a content type.
        /// Thumbnails are stored in AppData/Roaming/Segra/thumbnails/{ContentType}
        /// </summary>
        public static string GetThumbnailsFolderPath(Content.ContentType type)
        {
            return PathUtils.Combine(CacheFolder, Thumbnails, GetMetadataSubfolderName(type));
        }

        /// <summary>
        /// Gets the full path to the waveforms folder for a content type.
        /// Waveforms are stored in AppData/Roaming/Segra/waveforms/{ContentType}
        /// </summary>
        public static string GetWaveformsFolderPath(Content.ContentType type)
        {
            return PathUtils.Combine(CacheFolder, Waveforms, GetMetadataSubfolderName(type));
        }

        /// <summary>
        /// Gets the metadata file path for a content id.
        /// </summary>
        public static string GetMetadataFilePath(Content.ContentType type, string id)
        {
            return PathUtils.Combine(GetMetadataFolderPath(type), $"{id}.json");
        }

        /// <summary>
        /// Gets the thumbnail file path for a content id.
        /// </summary>
        public static string GetThumbnailFilePath(Content.ContentType type, string id)
        {
            return PathUtils.Combine(GetThumbnailsFolderPath(type), $"{id}.jpeg");
        }

        /// <summary>
        /// Gets the waveform file path for a content id.
        /// </summary>
        public static string GetWaveformFilePath(Content.ContentType type, string id)
        {
            return PathUtils.Combine(GetWaveformsFolderPath(type), $"{id}.peaks.json");
        }

        /// <summary>
        /// Tries to determine the content type from a file path.
        /// </summary>
        public static Content.ContentType? GetContentTypeFromPath(string path)
        {
            // Disk buffer segments are raw rolling footage, never content, wherever the buffer
            // folder points.
            if (IsDiskBufferPath(path))
                return null;

            string normalizedPath = path.Replace("\\", "/").ToLower();

            // Check new folder names first
            if (normalizedPath.Contains($"/{Sessions.ToLower()}/"))
                return Content.ContentType.Session;
            if (normalizedPath.Contains($"/{Buffers.ToLower()}/"))
                return Content.ContentType.Buffer;
            if (normalizedPath.Contains($"/{Clips.ToLower()}/"))
                return Content.ContentType.Clip;
            if (normalizedPath.Contains($"/{Highlights.ToLower()}/"))
                return Content.ContentType.Highlight;

            // Check legacy folder names for backwards compatibility
            if (normalizedPath.Contains($"/{LegacySessions}/"))
                return Content.ContentType.Session;
            if (normalizedPath.Contains($"/{LegacyBuffers}/"))
                return Content.ContentType.Buffer;
            if (normalizedPath.Contains($"/{LegacyClips}/"))
                return Content.ContentType.Clip;
            if (normalizedPath.Contains($"/{LegacyHighlights}/"))
                return Content.ContentType.Highlight;

            return null;
        }
    }
}
