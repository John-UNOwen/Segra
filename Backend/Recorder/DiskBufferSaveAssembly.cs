using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Segra.Backend.Core.Models;
using Segra.Backend.Media;
using Segra.Backend.Shared;

namespace Segra.Backend.Recorder
{
    /// <summary>
    /// Turns the concat list of disk buffer segments into one saved replay file and publishes it into
    /// the library folders. ffmpeg assembles into the segment folder, where the library never looks and
    /// the startup purge empties the whole tree, so a save killed mid-concat cannot leave a half-written
    /// replay the library would present as complete.
    ///
    /// Publishing is the delicate part. A rename inside the library folder is atomic; a rename across
    /// volumes is a copy followed by a delete, and a copy that is interrupted leaves a truncated mp4
    /// sitting under a library name. So the assembly publishes with a rename, and only falls back to a
    /// copy when the rename fails - the copy goes in under the interrupted-save marker and is renamed
    /// into its final name once it is complete, so a partial never carries a library name. The staged
    /// original is kept until the destination exists: on a folder where a rename fails on a transient
    /// lock (an antivirus scan or the indexer opening the file ffmpeg just closed) the staged file is the
    /// only complete copy of that footage, and the recording's segments are purged as soon as the stop
    /// finishes.
    /// </summary>
    internal static class DiskBufferSaveAssembly
    {
        /// <summary>
        /// Suffix marking a replay save that never reached its final name. Both the buffer purge and the
        /// library startup cleanup delete files carrying it, and nothing else matches it.
        /// </summary>
        public const string InterruptedSaveMarker = ".segrereplay";

        private static int _ffmpegProcessSeq;

        /// <summary>
        /// The outside world the assembly needs: run the concat, move a file, copy a file. Tests drive it
        /// with a rename that fails so the copy paths can be reached on a single-volume machine.
        /// </summary>
        internal interface ISteps
        {
            Task RunConcatAsync(string ffmpegArguments, string stagedPath, double expectedContentSeconds);
            void MoveFile(string source, string destination);
            void CopyFile(string source, string destination);
        }

        /// <summary>
        /// Succeeded, and where the replay ended up; or why it did not work, for the log (Reason) and for
        /// the user (UserMessage). StagedPreserved says the staged file was left alone on purpose - it is
        /// the only complete copy of that footage.
        /// </summary>
        internal sealed record Outcome(
            bool Succeeded,
            string? OutputPath,
            string? Reason,
            string? UserMessage,
            bool StagedPreserved);

        /// <summary>
        /// Assembles the save and publishes it: outputFolder is created here, the concat output is staged
        /// in stagingFolder, and a finished assembly is published under a name that does not collide with
        /// existing content. Never throws - every failure inside comes back as an Outcome carrying the
        /// reason, because the save runs on a task nobody is watching and a throw here is a save the user
        /// was told had started and that then silently never happened.
        /// </summary>
        public static async Task<Outcome> RunAsync(
            string stagingFolder,
            string outputFolder,
            string outputFileName,
            IReadOnlyList<string> concatLines,
            string ffmpegExtraArgs,
            double expectedContentSeconds,
            ISteps? steps = null)
        {
            ISteps io = steps ?? new MuxerSteps();
            string stagedPath = PathUtils.Combine(stagingFolder, outputFileName);
            string concatPath = Path.Combine(Path.GetTempPath(), $"segra_disk_buffer_concat_{Guid.NewGuid():N}.txt");

            try
            {
                Directory.CreateDirectory(outputFolder);
                string outputPath = UniqueFilePath(PathUtils.Combine(outputFolder, outputFileName));

                await File.WriteAllTextAsync(concatPath, string.Join("\n", concatLines) + "\n");

                await io.RunConcatAsync(BuildArguments(concatPath, stagedPath, ffmpegExtraArgs), stagedPath, expectedContentSeconds);

                if (!File.Exists(stagedPath))
                {
                    string missing = $"Disk buffer replay concat reported success but produced no file: {stagedPath}";
                    Log.Error(missing);
                    return new Outcome(false, null, missing, AssembleFailureMessage, false);
                }

                return Publish(io, stagedPath, outputPath);
            }
            catch (FFmpegException ex)
            {
                Log.Error($"Disk buffer replay concat failed (exit {ex.ExitCode}): {FFmpegErrors.Summarize(ex)}");
                DeleteQuietly(stagedPath);
                return new Outcome(false, null, FFmpegErrors.Summarize(ex), FFmpegErrors.DescribeForUser(ex), false);
            }
            catch (Exception ex)
            {
                // The concat list, the output folder, the muxer itself: anything that stops the assembly
                // before there is a finished file to publish. Whatever partial exists is not a replay.
                Log.Error(ex, $"Disk buffer replay assembly failed: {ex.Message}");
                DeleteQuietly(stagedPath);
                return new Outcome(false, null, $"Disk buffer replay assembly failed: {ex.Message}", AssembleFailureMessage, false);
            }
            finally
            {
                DeleteQuietly(concatPath);
            }
        }

        private const string AssembleFailureMessage =
            "The replay could not be assembled from the disk buffer segments. Check the log for details.";

        private const string PublishFailureMessage =
            "The replay was assembled but could not be added to the library. Check the log for details.";

        /// <summary>
        /// Rename the staged file into its library name, and only fall back to a copy when that rename is
        /// refused. The copy lands under the interrupted-save marker in the destination folder and is
        /// renamed into place once it is complete, so a partial never carries a library name; the staged
        /// original is kept until the destination exists.
        /// </summary>
        private static Outcome Publish(ISteps io, string stagedPath, string outputPath)
        {
            try
            {
                io.MoveFile(stagedPath, outputPath);
                return Published(outputPath, "moved");
            }
            catch (Exception moveError)
            {
                Log.Warning(moveError, $"Could not move the assembled replay into the library: {outputPath}; copying instead");
            }

            string markerPath = outputPath + InterruptedSaveMarker;
            try
            {
                io.CopyFile(stagedPath, markerPath);
                io.MoveFile(markerPath, outputPath);
                // The destination exists now, so the staged original is redundant.
                DeleteQuietly(stagedPath);
                return Published(outputPath, "copied");
            }
            catch (Exception copyError)
            {
                // Nothing half-written goes under a library name, and the staged file stays where it is:
                // the buffer purge reaches it, and the recording's segments are gone as soon as the stop
                // finishes, so this is the only complete copy of that footage.
                Log.Error(copyError, $"Could not copy the assembled replay into the library: {outputPath}");
                DeleteQuietly(markerPath);
                return new Outcome(
                    false,
                    null,
                    $"Could not publish the assembled replay to {outputPath}: {copyError.Message}",
                    PublishFailureMessage,
                    StagedPreserved: true);
            }
        }

        private static Outcome Published(string outputPath, string how)
        {
            Log.Information($"Published disk buffer replay to {outputPath} ({how})");
            return new Outcome(true, PathUtils.Normalize(outputPath), null, null, StagedPreserved: false);
        }

        // -map -0:d drops the mkv segments' chapter data track (a bin_data stream the muxer writes into
        // every segment) and -map_chapters -1 the container chapters; same as ClipService's segment merge.
        // Bookmarks come from the finalize step, not from the container.
        private static string BuildArguments(string concatPath, string stagedPath, string ffmpegExtraArgs) =>
            $"-y -f concat -safe 0 -i \"{concatPath}\" -map 0 -map -0:d -map_chapters -1 -c copy{ffmpegExtraArgs} \"{stagedPath}\"";

        /// <summary>
        /// Startup sweep of the library folders for saves that never finished publishing: an interrupted
        /// copy carries the interrupted-save marker and nothing else matches it, so without this a partial
        /// sits next to real footage until somebody notices a file with no player that opens it. Only the
        /// marker-suffixed files are deleted; a real replay and the sidecars beside it are left alone.
        /// </summary>
        public static void CleanupInterruptedLibrarySaves()
        {
            string contentFolder = Settings.Instance.ContentFolder;
            if (string.IsNullOrWhiteSpace(contentFolder))
                return;

            int deleted = 0;
            long freedBytes = 0;

            foreach (var type in Enum.GetValues<Content.ContentType>())
            {
                string folder = FolderNames.GetVideoFolderPath(contentFolder, type);
                if (!Directory.Exists(folder))
                    continue;

                try
                {
                    foreach (string file in Directory.EnumerateFiles(folder, "*" + InterruptedSaveMarker, SearchOption.AllDirectories))
                    {
                        try
                        {
                            long size = new FileInfo(file).Length;
                            File.Delete(file);
                            deleted++;
                            freedBytes += size;
                            Log.Warning($"Deleted partial disk buffer save: {file}");
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, $"Could not delete partial disk buffer save {file}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, $"Could not scan {folder} for interrupted disk buffer saves");
                }
            }

            if (deleted > 0)
                Log.Information($"Removed {deleted} interrupted disk buffer save(s), {freedBytes / (1024.0 * 1024.0):F1} MB reclaimed");
        }

        private static string UniqueFilePath(string path)
        {
            if (!File.Exists(path))
                return path;
            string? dir = Path.GetDirectoryName(path);
            string baseName = Path.GetFileNameWithoutExtension(path);
            string extension = Path.GetExtension(path);
            for (int i = 2; ; i++)
            {
                string candidate = Path.Combine(dir ?? "", $"{baseName}_{i}{extension}");
                if (!File.Exists(candidate))
                    return candidate;
            }
        }

        private static void DeleteQuietly(string? path)
        {
            if (string.IsNullOrEmpty(path))
                return;
            try { File.Delete(path); } catch { /* nothing actionable */ }
        }

        private sealed class MuxerSteps : ISteps
        {
            public Task RunConcatAsync(string ffmpegArguments, string stagedPath, double expectedContentSeconds) =>
                FFmpegService.RunWithProgress(
                    Interlocked.Increment(ref _ffmpegProcessSeq),
                    ffmpegArguments,
                    expectedContentSeconds,
                    _ => { });

            public void MoveFile(string source, string destination) => File.Move(source, destination);

            public void CopyFile(string source, string destination) => File.Copy(source, destination, overwrite: true);
        }
    }
}
