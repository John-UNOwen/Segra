using Serilog;
using ObsKit.NET.Outputs;
using ObsKit.NET.Signals;
using ObsKit.NET.Native.Types;
using Segra.Backend.App;
using Segra.Backend.Media;
using Segra.Backend.Shared;
using Segra.Backend.Core.Models;
using System;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Segra.Backend.Recorder
{
    /// <summary>
    /// Replay-buffer semantics with the footage on disk instead of in RAM. OBSService creates and
    /// configures the mkv RecordingOutput; this service attaches OBS's file splitting (fixed
    /// segments), drives the segment ledger (DiskBufferLedger) that records each boundary, prunes
    /// segments once they fall outside the buffer window, and builds a saved replay by stream-copying
    /// the segments that cover the requested window through the ffmpeg concat demuxer.
    /// The file_changed signal runs on an OBS thread, so it only updates the ledger and hands any
    /// file deletion to a background task.
    /// </summary>
    internal static class DiskBufferService
    {
        // Segment length in seconds. OBS cuts slightly late (measured 10.00 +/- 0.02 s); a save's
        // window therefore reaches back at most SegmentSeconds further than requestedAt-duration.
        public const int SegmentSeconds = 10;

        // Stop() emits a trailing file_changed whose next segment is a ~1 KB unparseable stub;
        // segments below this size are also skipped when selecting (a partial/empty segment).
        private const long MinSegmentBytes = 1_048_576;

        private static readonly object _lock = new();
        private static readonly SemaphoreSlim _saveLock = new(1, 1);

        private static DiskBufferLedger? _ledger;
        private static RecordingOutput? _output;
        private static SignalConnection? _fileChangedConnection;

        private static string _segmentFolder = "";
        private static string _gameFolderName = "";
        private static DateTime? _lastSaveEnd;
        private static bool _stopping;
        private static TaskCompletionSource<string>? _splitSignaled;
        private static int _ffmpegProcessSeq;
        private static int _saveActive;

        public static bool IsActive => _output != null && _ledger != null;

        /// <summary>
        /// True while a save holds the save lock. Pinned segments cannot be pruned during a save, so
        /// the rolling window stops reclaiming space for its duration.
        /// </summary>
        public static bool IsSaving => Volatile.Read(ref _saveActive) == 1;

        /// <summary>
        /// Average bytes per second pruning has reclaimed since it started for this recording, or 0
        /// before the first segment has been pruned. OBSService subtracts it from the write rate to
        /// get the disk buffer's net growth; a broken prune keeps it low and the headroom high.
        /// </summary>
        public static double EstimateReclaimedBytesPerSecond()
        {
            DiskBufferLedger? ledger = _ledger;
            DateTime? firstReclaim = ledger?.FirstReclaimAt;
            long reclaimedBytes = ledger?.ReclaimedBytes ?? 0;

            if (firstReclaim == null || reclaimedBytes <= 0)
                return 0;

            double seconds = (DateTime.Now - firstReclaim.Value).TotalSeconds;
            return seconds > 0 ? reclaimedBytes / seconds : 0;
        }

        internal sealed record DiskBufferSaveResult(
            string OutputPath,
            DateTime RequestedAt,
            Func<DateTime, TimeSpan> BookmarkPositionFor);

        /// <summary>
        /// Folder holding the per-game segment folders: the configured DiskBufferFolder, or
        /// .diskbuffer under the content folder when none is configured.
        /// </summary>
        public static string ResolvedRoot() => FolderNames.GetDiskBufferRoot();

        /// <summary>
        /// Takes over an OBSService-configured output: enables file splitting under the resolved
        /// root, subscribes file_changed (after SetFormat, which recreates the OBS output), starts
        /// recording and seeds the ledger with the first segment. Returns false when the start
        /// failed; the output is then disposed here, so OBSService only has to clear its field.
        /// </summary>
        public static bool Start(RecordingOutput output, string gameFolderName, int durationSec)
        {
            lock (_lock)
            {
                if (_output != null)
                {
                    Log.Warning("Disk buffer is already active");
                    return false;
                }

                _gameFolderName = gameFolderName;
                _ledger = new DiskBufferLedger(durationSec);
                _lastSaveEnd = null;
                _stopping = false;

                _segmentFolder = PathUtils.Combine(ResolvedRoot(), gameFolderName);
                try
                {
                    Directory.CreateDirectory(_segmentFolder);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"Failed to create disk buffer folder {_segmentFolder}");
                    return false;
                }

                try
                {
                    _fileChangedConnection = output.ConnectSignal(OutputSignal.FileChanged, OnFileChanged);
                    output.WithFileSplitting(
                        _segmentFolder,
                        SegmentSeconds,
                        0,
                        "seg_%CCYY-%MM-%DD_%hh-%mm-%ss",
                        "mkv",
                        allowSpaces: false,
                        allowOverwrite: false);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to configure disk buffer file splitting");
                    CleanupOutput(output);
                    return false;
                }
            }

            bool started;
            try
            {
                started = output.Start();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Disk buffer output Start threw");
                started = false;
            }

            if (!started)
            {
                Log.Error($"Failed to start disk buffer recording: {output.LastError ?? "unknown error"}");
                CleanupOutput(output);
                return false;
            }

            lock (_lock)
            {
                _output = output;
                string? path = output.Path;
                if (!string.IsNullOrEmpty(path))
                    _ledger?.Seed(PathUtils.Normalize(path), DateTime.Now);
                Log.Information($"Disk buffer recording started to {_segmentFolder}; first segment: {path ?? "unknown"}");
            }
            return true;
        }

        private static void CleanupOutput(RecordingOutput output)
        {
            SignalConnection? connection;
            lock (_lock)
            {
                connection = _fileChangedConnection;
                _fileChangedConnection = null;
                _output = null;
                _ledger = null;
            }
            if (connection != null)
            {
                try { connection.Dispose(); } catch { /* nothing actionable */ }
            }
            try { output.Dispose(); } catch (Exception ex) { Log.Warning(ex, "Failed to dispose disk buffer output"); }
        }

        private static void OnFileChanged(nint calldata)
        {
            try
            {
                string? next = null;
                try { next = Calldata.GetString(calldata, "next_file"); } catch { /* keep null */ }

                DateTime now = DateTime.Now;
                List<DiskBufferSegment> pruneCandidates;
                lock (_lock)
                {
                    DiskBufferLedger? ledger = _ledger;
                    if (ledger == null)
                        return;

                    if (_stopping)
                    {
                        // Stop() emits an extra file_changed whose "next file" is the unsaveable
                        // trailing stub; keep it out of the ledger.
                        Log.Debug("Disk buffer file_changed while stopping; trailing segment not added");
                    }
                    else
                    {
                        ledger.RecordBoundary(now, next);

                        TaskCompletionSource<string>? tcs = _splitSignaled;
                        if (tcs != null)
                        {
                            _splitSignaled = null;
                            tcs.TrySetResult(next ?? "");
                        }
                    }

                    pruneCandidates = ledger.CollectPrunable(now);
                }

                Log.Debug($"Disk buffer segment boundary at {now:HH:mm:ss.fff}; next file: {next ?? "none"}");
                if (pruneCandidates.Count > 0)
                    _ = Task.Run(() => DeleteSegments(pruneCandidates));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Disk buffer file_changed handler failed");
            }
        }

        /// <summary>
        /// Deletes the segments the ledger offered and accounts the space they gave back. Runs on a
        /// background task: a segment the muxer or a reader still holds is put back for the next
        /// boundary instead of blocking the OBS thread.
        /// </summary>
        private static void DeleteSegments(List<DiskBufferSegment> segments)
        {
            DiskBufferLedger? ledger = _ledger;
            int deleted = 0;
            long freedBytes = 0;
            foreach (DiskBufferSegment segment in segments)
            {
                try
                {
                    long size = 0;
                    var info = new FileInfo(segment.Path);
                    if (info.Exists)
                    {
                        size = info.Length;
                        File.Delete(segment.Path);
                    }
                    ledger?.AccountReclaim(segment, size, DateTime.Now);
                    deleted++;
                    freedBytes += size;
                    Log.Debug($"Deleted disk buffer segment {segment.Path} ({size} bytes)");
                }
                catch (IOException)
                {
                    // Segment file is still open (mux closing or a reader); retry on the next event.
                    ledger?.RetryPrune(segment.Path);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, $"Failed to delete disk buffer segment {segment.Path}");
                    ledger?.RetryPrune(segment.Path);
                }
            }
            if (deleted > 0)
                Log.Information($"Pruned {deleted} disk buffer segment(s), {freedBytes / (1024.0 * 1024.0):F1} MB reclaimed");
        }

        /// <summary>
        /// Stops the output (letting an in-flight save finish first), unsubscribes and disposes it,
        /// and deletes the recording's whole segment folder. When a save is still running after a
        /// generous wait the purge is skipped: the leftover segments are removed by the startup purge
        /// instead of pulling the files out from under ffmpeg.
        /// </summary>
        public static async Task StopAsync()
        {
            RecordingOutput output;
            string segmentFolder;
            lock (_lock)
            {
                if (_output == null)
                    return;
                _stopping = true;
                output = _output;
                segmentFolder = _segmentFolder;
            }

            bool saveCompleted = await _saveLock.WaitAsync(TimeSpan.FromMinutes(15));
            if (!saveCompleted)
                Log.Warning("Disk buffer save still in flight when recording stopped; segment files will be removed by the startup purge");

            try
            {
                bool stopped = false;
                try
                {
                    stopped = output.Stop(waitForCompletion: true, timeoutMs: 30000);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Disk buffer output Stop threw");
                }
                if (!stopped)
                {
                    try { output.ForceStop(); } catch (Exception ex) { Log.Warning(ex, "Disk buffer output ForceStop threw"); }
                }
            }
            finally
            {
                SignalConnection? connection;
                lock (_lock)
                {
                    connection = _fileChangedConnection;
                    _fileChangedConnection = null;
                    _output = null;
                    _ledger = null;
                }
                if (connection != null)
                {
                    try { connection.Dispose(); } catch { /* nothing actionable */ }
                }
                try { output.Dispose(); } catch (Exception ex) { Log.Warning(ex, "Failed to dispose disk buffer output"); }
            }

            if (saveCompleted)
            {
                try
                {
                    PurgeFolder(segmentFolder);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, $"Failed to purge disk buffer folder {segmentFolder} after stop");
                }
                _saveLock.Release();
            }

            Log.Information("Disk buffer recording stopped");
        }

        /// <summary>
        /// Tears the output down without waiting for a graceful completion (sleep/hibernate path);
        /// the regular StopRecording flow still runs StopAsync afterwards to purge.
        /// </summary>
        public static void ForceStop()
        {
            lock (_lock)
            {
                if (_output == null)
                    return;
                _stopping = true;
                try { _output.ForceStop(); } catch (Exception ex) { Log.Warning(ex, "Disk buffer output ForceStop threw"); }
            }
        }

        /// <summary>
        /// Builds a saved replay: forces the current segment to close, selects the closed segments
        /// covering the window (since the previous save when DiskBufferSaveSinceLastSave is on,
        /// otherwise the full buffer duration), and stream-copies them into one mp4, assembled in the
        /// segment folder and moved into Replay Buffers only after ffmpeg succeeded. Returns the
        /// output path, the wall-clock save instant and a bookmark position resolver; null when
        /// nothing could be saved or ffmpeg failed (user is informed here, sidecars are OBSService's
        /// job). Recording keeps running throughout.
        /// </summary>
        public static async Task<DiskBufferSaveResult?> SaveAsync()
        {
            DiskBufferLedger? ledger = _ledger;
            if (!IsActive || ledger == null)
            {
                Log.Warning("Cannot save disk buffer: disk buffer is not active");
                return null;
            }

            _ = MessageService.SendFrontendMessage("ReplayBufferSaveStarted", new { });
            await _saveLock.WaitAsync();
            Volatile.Write(ref _saveActive, 1);

            DateTime requestedAt = DateTime.Now;
            List<DiskBufferSegment> pinned = new();
            List<DiskBufferSegment> selected = new();
            string? concatPath = null;
            string? outputPath = null;
            string? stagedPath = null;
            bool succeeded = false;

            try
            {
                Log.Information("Attempting to save disk buffer replay...");

                // Force the open segment to close so everything recorded so far is saveable. The
                // event normally lands 100-630 ms later; if it does not arrive, proceed with the
                // ledger as-is (the newest closed segments still cover almost the whole window).
                TaskCompletionSource<string> splitSignal = new();
                bool splitAccepted = false;
                RecordingOutput? output = _output;
                if (output != null)
                {
                    lock (_lock) _splitSignaled = splitSignal;
                    try { splitAccepted = output.SplitFile(); }
                    catch (Exception ex) { Log.Warning(ex, "Disk buffer SplitFile threw"); }
                }
                if (!splitAccepted)
                {
                    Log.Warning("Disk buffer SplitFile was not accepted; saving the segments closed so far");
                }
                else
                {
                    Task completed = await Task.WhenAny(splitSignal.Task, Task.Delay(TimeSpan.FromSeconds(5)));
                    if (completed != splitSignal.Task)
                        Log.Debug("Disk buffer split file_changed not observed within 5 s; using the ledger as-is");
                }

                DateTime windowStart;
                lock (_lock)
                {
                    windowStart = requestedAt - TimeSpan.FromSeconds(ledger.WindowSeconds);
                    if (Settings.Instance.DiskBufferSaveSinceLastSave && _lastSaveEnd.HasValue && _lastSaveEnd.Value > windowStart)
                        windowStart = _lastSaveEnd.Value;
                }

                // A closed segment overlaps the window when it ended after the window opened.
                // The newest (open) segment is still being written and is never saveable.
                pinned = ledger.PinOverlapping(windowStart);

                // EnsureFileReady may block for seconds; pinned segments are protected from pruning
                // while this runs.
                foreach (DiskBufferSegment segment in pinned)
                {
                    try
                    {
                        await GeneralUtils.EnsureFileReady(segment.Path);
                        long size = new FileInfo(segment.Path).Length;
                        if (size < MinSegmentBytes)
                        {
                            Log.Information($"Skipping disk segment from the save (stub, {size} B): {segment.Path}");
                            continue;
                        }
                        selected.Add(segment);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"Skipping disk segment from the save ({ex.Message}): {segment.Path}");
                    }
                }

                if (selected.Count == 0)
                {
                    Log.Warning("Disk buffer replay save found no complete segments in the window yet");
                    await MessageService.ShowModal("Replay Save Failed", "The disk buffer has not finished writing a segment yet. Try again in a moment.", "error");
                    return null;
                }

                // The window can open mid-segment. inpoint trims the start of the first segment;
                // with -c copy ffmpeg snaps it to the previous keyframe (keyint is 1 s), so the cut
                // lands up to 1 s early and effectiveWindowStart is the nominal cut point.
                double inpointSeconds = Math.Floor((windowStart - selected[0].StartedAt).TotalSeconds);
                if (inpointSeconds < 0)
                    inpointSeconds = 0;
                DateTime effectiveWindowStart = selected[0].StartedAt + TimeSpan.FromSeconds(inpointSeconds);

                string outputFolder = PathUtils.Combine(Settings.Instance.ContentFolder, FolderNames.Buffers, _gameFolderName);
                Directory.CreateDirectory(outputFolder);
                string fileName = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4";
                outputPath = GetUniqueFilePath(PathUtils.Combine(outputFolder, fileName));
                // ffmpeg assembles into the segment folder and the finished file is moved into the
                // library at the end. The segment folder is never listed as content and the startup
                // purge empties it, so a save killed mid-concat leaves a stale segment there instead
                // of a half-written replay the library would present as complete.
                stagedPath = PathUtils.Combine(_segmentFolder, fileName);

                concatPath = Path.Combine(Path.GetTempPath(), $"segra_disk_buffer_concat_{Guid.NewGuid():N}.txt");
                // Concat demuxer options apply to the file line they follow, so the inpoint goes
                // after the first file, not before it.
                var listLines = new List<string>();
                for (int i = 0; i < selected.Count; i++)
                {
                    listLines.Add(FFmpegService.BuildConcatListLine(selected[i].Path));
                    if (i == 0 && inpointSeconds > 0)
                        listLines.Add($"inpoint {inpointSeconds.ToString(CultureInfo.InvariantCulture)}");
                }
                await File.WriteAllTextAsync(concatPath, string.Join("\n", listLines) + "\n");

                // Playable content the window actually contributes, for the progress estimate and
                // for deriving the per-junction padding used by the bookmark resolver.
                TimeSpan contentSpan = TimeSpan.Zero;
                foreach (DiskBufferSegment segment in selected)
                {
                    DateTime from = segment.StartedAt > effectiveWindowStart ? segment.StartedAt : effectiveWindowStart;
                    DateTime to = segment.EndedAt!.Value < requestedAt ? segment.EndedAt.Value : requestedAt;
                    if (to > from)
                        contentSpan += to - from;
                }

                string? codec = await FFmpegService.DetectVideoCodec(selected[0].Path);
                string tagArgs = codec == "hevc" ? " -tag:v hvc1" : "";
                // -map -0:d drops the mkv segments' chapter data track (a bin_data stream the
                // muxer writes into every segment) and -map_chapters -1 the container chapters;
                // same as ClipService's segment merge. Bookmarks come from the finalize step.
                string arguments = $"-y -f concat -safe 0 -i \"{concatPath}\" -map 0 -map -0:d -map_chapters -1 -c copy{tagArgs} \"{stagedPath}\"";

                try
                {
                    await FFmpegService.RunWithProgress(
                        Interlocked.Increment(ref _ffmpegProcessSeq),
                        arguments,
                        contentSpan.TotalSeconds,
                        _ => { });
                }
                catch (FFmpegException ex)
                {
                    Log.Error($"Disk buffer replay concat failed (exit {ex.ExitCode}): {FFmpegErrors.Summarize(ex)}");
                    await MessageService.ShowModal("Replay Save Failed", FFmpegErrors.DescribeForUser(ex), "error");
                    return null;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Disk buffer replay concat failed unexpectedly");
                    await MessageService.ShowModal("Replay Save Failed", "The replay could not be assembled from the disk buffer segments. Check the log for details.", "error");
                    return null;
                }

                if (!File.Exists(stagedPath))
                {
                    Log.Error($"Disk buffer replay concat reported success but produced no file: {stagedPath}");
                    return null;
                }

                // Only a finished concat is moved into the library folders. A save interrupted before
                // this point leaves its output in the segment folder, where the library never looks
                // and the startup purge deletes it.
                try
                {
                    File.Move(stagedPath, outputPath);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"Failed to move the assembled replay into the library: {outputPath}");
                    await MessageService.ShowModal("Replay Save Failed", "The replay was assembled but could not be added to the library. Check the log for details.", "error");
                    return null;
                }

                TimeSpan outputDuration = await FFmpegService.GetVideoDuration(outputPath);
                Log.Information($"Disk buffer replay saved to: {outputPath} ({selected.Count} segments, {outputDuration.TotalSeconds:F1} s)");

                Func<DateTime, TimeSpan> positionFor = BuildBookmarkResolver(selected, effectiveWindowStart, outputDuration, contentSpan);
                succeeded = true;
                return new DiskBufferSaveResult(outputPath, requestedAt, positionFor);
            }
            finally
            {
                lock (_lock)
                {
                    if (succeeded)
                        _lastSaveEnd = requestedAt;
                }
                ledger.Unpin(pinned);
                if (concatPath != null)
                    DeleteQuietly(concatPath);
                if (!succeeded)
                    DeleteQuietly(stagedPath);
                Volatile.Write(ref _saveActive, 0);
                _saveLock.Release();
            }
        }

        /// <summary>
        /// Maps a bookmark's wall-clock time to its position inside the concatenated output.
        ///
        /// Stream-copy concat plays the segments back to back, but each junction between files
        /// inserts dead timeline (measured ~19-25 ms per junction, ~4.5 s over 30 min) that counts
        /// towards the output duration without adding playable content. The real padding per junction
        /// is derived from the measured output:
        ///   paddingPerJunction = max(0, (outputDuration - contentSpan) / (segmentCount - 1))
        /// where contentSpan is the summed overlap of the selected segments with the window (the
        /// first segment already trimmed by the inpoint). A bookmark recorded at wall time T crosses
        /// the junctions of all selected segments that ended at or before T, so:
        ///   position = (T - effectiveWindowStart) + paddingPerJunction * junctionsBefore(T)
        /// effectiveWindowStart is the nominal inpoint cut; the real cut snaps to the previous
        /// keyframe (up to 1 s early), which shifts early bookmarks by up to ~1 s - the measured
        /// padding absorbs that into paddingPerJunction. The position is intentionally left
        /// unclamped: bookmarks recorded outside the saved window come back negative or past the
        /// file duration and are dropped by the caller's range filter; clamping here instead would
        /// leave a post-save bookmark sitting at the end of the replay.
        /// </summary>
        private static Func<DateTime, TimeSpan> BuildBookmarkResolver(
            List<DiskBufferSegment> selected,
            DateTime effectiveWindowStart,
            TimeSpan outputDuration,
            TimeSpan contentSpan)
        {
            double paddingPerJunction = selected.Count > 1
                ? Math.Max(0, (outputDuration - contentSpan).TotalSeconds) / (selected.Count - 1)
                : 0;

            return wallTime =>
            {
                double position = (wallTime - effectiveWindowStart).TotalSeconds;
                int junctions = 0;
                foreach (DiskBufferSegment segment in selected)
                {
                    if (segment.EndedAt != null && segment.EndedAt.Value <= wallTime)
                        junctions++;
                }
                position += paddingPerJunction * junctions;
                return TimeSpan.FromSeconds(position);
            };
        }

        private static string GetUniqueFilePath(string path)
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

        /// <summary>
        /// Startup cleanup: segments left by a crash are stale by definition. Removes the segment
        /// files under the resolved root (a missing folder is fine) and logs what was reclaimed.
        /// Only files OBS named as segments are touched, so a buffer folder the user pointed at
        /// somewhere that also holds other work keeps that other work.
        /// </summary>
        public static void PurgeAll()
        {
            string root = ResolvedRoot();
            string contentFolder = PathUtils.Normalize(Settings.Instance.ContentFolder);

            if (string.IsNullOrWhiteSpace(root) || root.TrimEnd('/', '\\').Length <= 2)
            {
                Log.Warning($"Refusing to purge disk buffer root '{root}'");
                return;
            }
            if (string.Equals(root, contentFolder, StringComparison.OrdinalIgnoreCase) ||
                contentFolder.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
            {
                // The root must never be or contain the content folders themselves.
                Log.Warning($"Refusing to purge disk buffer root '{root}': it is or contains the content folder");
                return;
            }

            try
            {
                PurgeFolder(root);
                Log.Information($"Disk buffer startup purge completed for {root}");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, $"Disk buffer startup purge failed for {root}");
            }
        }

        // OBS names segments seg_<CCYY-MM-DD>_<hh-mm-ss>.mkv (the format passed to WithFileSplitting).
        // This is the marker that says a file under the buffer root is ours to delete.
        private static bool IsSegmentFileName(string fileName) =>
            fileName.StartsWith("seg_", StringComparison.OrdinalIgnoreCase) &&
            fileName.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase);

        private static void PurgeFolder(string folder)
        {
            if (!Directory.Exists(folder))
                return;

            int deleted = 0;
            long freedBytes = 0;
            foreach (string file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (!IsSegmentFileName(Path.GetFileName(file)))
                    continue;

                try
                {
                    long size = new FileInfo(file).Length;
                    File.Delete(file);
                    deleted++;
                    freedBytes += size;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, $"Failed to delete disk buffer file {file}");
                }
            }

            // Drop now-empty game subfolders, keeping the root itself.
            foreach (string sub in Directory.GetDirectories(folder))
            {
                try
                {
                    if (Directory.GetFileSystemEntries(sub, "*", SearchOption.AllDirectories).Length == 0)
                        Directory.Delete(sub);
                }
                catch { /* leave for the next purge */ }
            }

            Log.Information($"Purged {deleted} disk buffer file(s), {freedBytes / (1024.0 * 1024.0):F1} MB reclaimed from {folder}");
        }
    }
}
