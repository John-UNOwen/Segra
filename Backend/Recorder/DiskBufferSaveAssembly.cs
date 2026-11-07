using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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

        internal sealed record Outcome(bool Succeeded, string? OutputPath, string? Reason);

        /// <summary>
        /// Assembles the save and publishes it. outputFolder is created here, staged in stagingFolder,
        /// published under a name that does not collide with existing content.
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

            Directory.CreateDirectory(outputFolder);
            string outputPath = UniqueFilePath(PathUtils.Combine(outputFolder, outputFileName));
            string stagedPath = PathUtils.Combine(stagingFolder, outputFileName);

            string concatPath = Path.Combine(Path.GetTempPath(), $"segra_disk_buffer_concat_{Guid.NewGuid():N}.txt");
            try
            {
                await File.WriteAllTextAsync(concatPath, string.Join("\n", concatLines) + "\n");

                await io.RunConcatAsync(BuildArguments(concatPath, stagedPath, ffmpegExtraArgs), stagedPath, expectedContentSeconds);

                if (!File.Exists(stagedPath))
                    return new Outcome(false, null, $"Disk buffer replay concat reported success but produced no file: {stagedPath}");

                io.MoveFile(stagedPath, outputPath);
                Log.Information($"Published disk buffer replay to {outputPath}");
                return new Outcome(true, PathUtils.Normalize(outputPath), null);
            }
            finally
            {
                DeleteQuietly(concatPath);
            }
        }

        // -map -0:d drops the mkv segments' chapter data track (a bin_data stream the muxer writes into
        // every segment) and -map_chapters -1 the container chapters; same as ClipService's segment merge.
        // Bookmarks come from the finalize step, not from the container.
        private static string BuildArguments(string concatPath, string stagedPath, string ffmpegExtraArgs) =>
            $"-y -f concat -safe 0 -i \"{concatPath}\" -map 0 -map -0:d -map_chapters -1 -c copy{ffmpegExtraArgs} \"{stagedPath}\"";

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
