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
    /// segments), keeps a ledger of segments with their wall-clock start/end times, prunes segments
    /// once they fall outside the buffer window, and builds a saved replay by stream-copying the
    /// segments that cover the requested window through the ffmpeg concat demuxer.
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
        private static readonly List<Segment> _segments = new();

        private static RecordingOutput? _output;
        private static SignalConnection? _fileChangedConnection;

        private static string _segmentFolder = "";
        private static string _gameFolderName = "";
        private static int _durationSec;
        private static DateTime? _lastSaveEnd;
        private static bool _stopping;
        private static TaskCompletionSource<string>? _splitSignaled;
        private static int _ffmpegProcessSeq;

        public static bool IsActive => _output != null;

        internal sealed record DiskBufferSaveResult(
            string OutputPath,
            DateTime RequestedAt,
            Func<DateTime, TimeSpan> BookmarkPositionFor);

        private sealed class Segment
        {
            public Segment(string path) => Path = path;

            public string Path { get; }
            public DateTime StartedAt { get; set; }
            public DateTime? EndedAt { get; set; }
            public int Pins { get; set; }
            public bool DeletePending { get; set; }
        }

        /// <summary>
        /// Folder holding the per-game segment folders: the configured DiskBufferFolder, or
        /// .diskbuffer under the content folder when none is configured.
        /// </summary>
        public static string ResolvedRoot()
        {
            string configured = Settings.Instance.DiskBufferFolder ?? "";
            if (!string.IsNullOrWhiteSpace(configured))
                return PathUtils.Normalize(configured);
            return PathUtils.Normalize(PathUtils.Combine(Settings.Instance.ContentFolder, ".diskbuffer"));
        }

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
                _durationSec = durationSec;
                _lastSaveEnd = null;
                _stopping = false;
                _segments.Clear();

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
                    _segments.Add(new Segment(PathUtils.Normalize(path)) { StartedAt = DateTime.Now });
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
                _segments.Clear();
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
                List<Segment> pruneCandidates;
                lock (_lock)
                {
                    if (_stopping)
                    {
                        // Stop() emits an extra file_changed whose "next file" is the unsaveable
                        // trailing stub; keep it out of the ledger.
                        Log.Debug("Disk buffer file_changed while stopping; trailing segment not added");
                    }
                    else
                    {
                        for (int i = _segments.Count - 1; i >= 0; i--)
                        {
                            if (_segments[i].EndedAt == null)
                            {
                                _segments[i].EndedAt = now;
                                break;
                            }
                        }
                        if (!string.IsNullOrEmpty(next))
                            _segments.Add(new Segment(PathUtils.Normalize(next)) { StartedAt = now });

                        TaskCompletionSource<string>? tcs = _splitSignaled;
                        if (tcs != null)
                        {
                            _splitSignaled = null;
                            tcs.TrySetResult(next ?? "");
                        }
                    }

                    pruneCandidates = CollectPrunable(now);
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
        /// Closed segments older than the buffer window (+ one segment for OBS's late cuts) with no
        /// pins and not the newest ledger entry. Caller holds _lock; marking DeletePending here is
        /// what deduplicates concurrent delete attempts.
        /// </summary>
        private static List<Segment> CollectPrunable(DateTime now)
        {
            TimeSpan window = TimeSpan.FromSeconds(_durationSec + SegmentSeconds);
            var candidates = new List<Segment>();
            for (int i = 0; i < _segments.Count - 1; i++)
            {
                Segment segment = _segments[i];
                if (segment.EndedAt == null || segment.Pins > 0 || segment.DeletePending)
                    continue;
                if (now - segment.EndedAt.Value > window)
                {
                    segment.DeletePending = true;
                    candidates.Add(segment);
                }
            }
            return candidates;
        }

        private static void DeleteSegments(List<Segment> segments)
        {
            int deleted = 0;
            long freedBytes = 0;
            foreach (Segment segment in segments)
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
                    lock (_lock)
                    {
                        _segments.Remove(segment);
                        segment.DeletePending = false;
                    }
                    deleted++;
                    freedBytes += size;
                    Log.Debug($"Deleted disk buffer segment {segment.Path} ({size} bytes)");
                }
                catch (IOException)
                {
                    // Segment file is still open (mux closing or a reader); retry on the next event.
                    lock (_lock) segment.DeletePending = false;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, $"Failed to delete disk buffer segment {segment.Path}");
                    lock (_lock) segment.DeletePending = false;
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
                    _segments.Clear();
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
        /// otherwise the full buffer duration), and stream-copies them into one mp4. Returns the
        /// output path, the wall-clock save instant and a bookmark position resolver; null when
        /// nothing could be saved or ffmpeg failed (user is informed here, sidecars are OBSService's
        /// job). Recording keeps running throughout.
        /// </summary>
        public static async Task<DiskBufferSaveResult?> SaveAsync()
        {
            if (!IsActive)
            {
                Log.Warning("Cannot save disk buffer: disk buffer is not active");
                return null;
            }

            _ = MessageService.SendFrontendMessage("ReplayBufferSaveStarted", new { });
            await _saveLock.WaitAsync();

            DateTime requestedAt = DateTime.Now;
            List<Segment> pinned = new();
            List<Segment> selected = new();
            string? concatPath = null;
            string? outputPath = null;
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
                    windowStart = requestedAt - TimeSpan.FromSeconds(_durationSec);
                    if (Settings.Instance.DiskBufferSaveSinceLastSave && _lastSaveEnd.HasValue && _lastSaveEnd.Value > windowStart)
                        windowStart = _lastSaveEnd.Value;

                    // A closed segment overlaps the window when it ended after the window opened.
                    // The newest (open) segment is still being written and is never saveable.
                    foreach (Segment segment in _segments)
                    {
                        if (segment.EndedAt == null || segment.EndedAt.Value <= windowStart)
                            continue;
                        segment.Pins++;
                        pinned.Add(segment);
                    }
                }

                // EnsureFileReady may block for seconds; pinned segments are protected from pruning
                // while this runs.
                foreach (Segment segment in pinned)
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
                outputPath = GetUniqueFilePath(PathUtils.Combine(outputFolder, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4"));

                concatPath = Path.Combine(Path.GetTempPath(), $"segra_disk_buffer_concat_{Guid.NewGuid():N}.txt");
                var listLines = new List<string>();
                if (inpointSeconds > 0)
                    listLines.Add($"inpoint {inpointSeconds.ToString(CultureInfo.InvariantCulture)}");
                foreach (Segment segment in selected)
                    listLines.Add(FFmpegService.BuildConcatListLine(segment.Path));
                await File.WriteAllTextAsync(concatPath, string.Join("\n", listLines) + "\n");

                // Playable content the window actually contributes, for the progress estimate and
                // for deriving the per-junction padding used by the bookmark resolver.
                TimeSpan contentSpan = TimeSpan.Zero;
                foreach (Segment segment in selected)
                {
                    DateTime from = segment.StartedAt > effectiveWindowStart ? segment.StartedAt : effectiveWindowStart;
                    DateTime to = segment.EndedAt!.Value < requestedAt ? segment.EndedAt.Value : requestedAt;
                    if (to > from)
                        contentSpan += to - from;
                }

                string? codec = await FFmpegService.DetectVideoCodec(selected[0].Path);
                string tagArgs = codec == "hevc" ? " -tag:v hvc1" : "";
                string arguments = $"-y -f concat -safe 0 -i \"{concatPath}\" -map 0 -c copy{tagArgs} \"{outputPath}\"";

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
                    DeleteQuietly(outputPath);
                    return null;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Disk buffer replay concat failed unexpectedly");
                    await MessageService.ShowModal("Replay Save Failed", "The replay could not be assembled from the disk buffer segments. Check the log for details.", "error");
                    DeleteQuietly(outputPath);
                    return null;
                }

                if (!File.Exists(outputPath))
                {
                    Log.Error($"Disk buffer replay concat reported success but produced no file: {outputPath}");
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
                    foreach (Segment segment in pinned)
                        segment.Pins--;
                }
                if (concatPath != null)
                    DeleteQuietly(concatPath);
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
        /// clamped to [0, outputDuration]. effectiveWindowStart is the nominal inpoint cut; the real
        /// cut snaps to the previous keyframe (up to 1 s early), so very early bookmarks may sit up
        /// to ~1 s later in the file than computed. Bookmarks at or after the forced split inherit
        /// the maximum padding and get clamped to the output end.
        /// </summary>
        private static Func<DateTime, TimeSpan> BuildBookmarkResolver(
            List<Segment> selected,
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
                foreach (Segment segment in selected)
                {
                    if (segment.EndedAt != null && segment.EndedAt.Value <= wallTime)
                        junctions++;
                }
                position += paddingPerJunction * junctions;
                position = Math.Clamp(position, 0, outputDuration.TotalSeconds);
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
        /// Startup cleanup: segments left by a crash are stale by definition. Deletes everything
        /// under the resolved root (a missing folder is fine) and logs what was reclaimed. Refuses
        /// to touch a folder that could hold real content.
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

        private static void PurgeFolder(string folder)
        {
            if (!Directory.Exists(folder))
                return;

            int deleted = 0;
            long freedBytes = 0;
            foreach (string file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
            {
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
