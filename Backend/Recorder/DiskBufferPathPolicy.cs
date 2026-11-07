using System;
using System.IO;
using Segra.Backend.Shared;

namespace Segra.Backend.Recorder
{
    /// <summary>
    /// Decides which folder the rolling disk buffer may write into. The same decision guards the
    /// startup purge, which deletes everything under that folder, so validation and use have to
    /// agree on one canonical path: a value checked as one path and used as another can put the
    /// purge on top of the library.
    /// </summary>
    internal static class DiskBufferPathPolicy
    {
        /// <summary>
        /// Resolves a configured buffer folder to the canonical path the recorder will use. An empty
        /// setting means the default folder under the content folder. Returns false with a reason
        /// when the value is unusable, in which case the caller keeps the previous setting.
        /// </summary>
        public static bool TryResolve(string? configured, string contentFolder, out string root, out string reason)
        {
            root = "";
            reason = "";

            if (string.IsNullOrWhiteSpace(configured))
            {
                root = Canonical(PathUtils.Combine(contentFolder, FolderNames.DiskBuffer));
                return true;
            }

            if (!Path.IsPathFullyQualified(configured))
            {
                reason = "not an absolute path";
                return false;
            }

            string candidate = Canonical(configured);
            if (IsDriveRoot(candidate))
            {
                reason = $"'{configured}' resolves to the drive root '{candidate}'";
                return false;
            }

            root = candidate;
            return true;
        }

        // Full path resolution collapses '..' segments and drops the trailing separator, so the
        // string that is checked is the string the recorder and the purge work with.
        private static string Canonical(string path) =>
            PathUtils.Normalize(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));

        // GetPathRoot returns the separator style the OS prefers ("D:\"), so both sides are put
        // through the same canonical form before comparing.
        private static bool IsDriveRoot(string canonical) =>
            string.Equals(Canonical(Path.GetPathRoot(canonical) ?? ""), canonical, StringComparison.OrdinalIgnoreCase);
    }
}
