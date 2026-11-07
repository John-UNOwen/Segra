using Serilog;
using ObsKit.NET.Outputs;
using ObsKit.NET.Signals;
using ObsKit.NET.Native.Types;
using Segra.Backend.App;
using Segra.Backend.Core;
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

        private static readonly object _lock = new();
        private static readonly SemaphoreSlim _saveLock = new(1, 1);

        private static DiskBufferLedger? _ledger;
        private static RecordingOutput? _output;
        private static SignalConnection? _fileChangedConnection;

        private static string _segmentFolder = "";
        private static string _bufferRoot = "";
        private static string _gameFolderName = "";
        private static DateTime? _lastSaveEnd;
        private static TaskCompletionSource<string>? _splitSignaled;

        public static bool IsActive => _output != null && _ledger != null;

        /// <summary>The buffer window the recording is keeping, in seconds; 0 when not recording.</summary>
        public static int WindowSeconds => _ledger?.WindowSeconds ?? 0;

        /// <summary>
        /// True while a save holds the ledger. Pinned segments cannot be pruned during a save, so the
        /// rolling window stops reclaiming space for its duration.
        /// </summary>
        public static bool IsSaving => _ledger?.HasSaveLease ?? false;

        /// <summary>
        /// Bytes per second pruning reclaimed during the disk monitor's last sampling interval, or 0
        /// when it reclaimed nothing during it. OBSService subtracts it from the write rate to get the
        /// disk buffer's net growth, so a prune that stops working raises the headroom back to the full
        /// write rate within one interval. The interval is the monitor's own: the ledger is read when
        /// the monitor looks.
        /// </summary>
        public static double EstimateReclaimedBytesPerSecond(TimeSpan samplingInterval)
        {
            return _ledger?.EstimateReclaimedBytesPerSecond(DateTime.Now, samplingInterval) ?? 0;
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
        /// The root this recording writes into, put through the same rules that decide which folders the
        /// buffer may be pointed at all, so the folder the recorder uses and the folder it remembers are
        /// one path.
        /// </summary>
        private static string ResolveRecordingRoot()
        {
            if (DiskBufferPathPolicy.TryResolve(Settings.Instance.DiskBufferFolder, Settings.Instance.ContentFolder, out string root, out _))
                return root;

            return ResolvedRoot();
        }

        /// <summary>
        /// Adds this recording's buffer root to the settings list of roots the app has recorded into, and
        /// saves it now: a hard kill is exactly the case where that folder has to be findable again, and a
        /// folder nobody remembers recording into is counted as content and cleaned up as content.
        /// </summary>
        private static void RecordBufferRoot(string root)
        {
            try
            {
                var recorded = new List<string>(Settings.Instance.DiskBufferRecordedFolders);
                if (recorded.Contains(root, StringComparer.OrdinalIgnoreCase))
                    return;

                recorded.Add(root);
                Settings.Instance.DiskBufferRecordedFolders = recorded;
                SettingsService.SaveSettings();
                Log.Information($"Disk buffer recording will write into {root}");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, $"Failed to record {root} as a disk buffer folder this app recorded into");
            }
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
                    // The recording already running owns the service's output; this one was built for a
                    // start that is not happening, so release only what was handed to us here.
                    Log.Warning("Disk buffer is already active");
                    DisposeUnstarted(output);
                    return false;
                }

                _gameFolderName = gameFolderName;
                _ledger = new DiskBufferLedger(durationSec);
                _lastSaveEnd = null;

                _bufferRoot = ResolveRecordingRoot();
                _segmentFolder = PathUtils.Combine(_bufferRoot, gameFolderName);
                try
                {
                    Directory.CreateDirectory(_segmentFolder);
                }
                catch (Exception ex)
                {
                    // Same teardown as the branches below: this output already has its format, encoders
                    // and the Stopped signal wired, and dropping it here without disposing leaves the
                    // native muxer and that signal connection waiting on an ObsKit finalizer.
                    Log.Error(ex, $"Failed to create disk buffer folder {_segmentFolder}");
                    CleanupOutput(output);
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

            // Remember which root this recording writes into before its first segment exists: the settings
            // screen can move DiskBufferFolder at any time, and this record is the only thing that later
            // tells the purge and the storage accounting that an older folder holds rolling footage.
            RecordBufferRoot(_bufferRoot);

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

        /// <summary>
        /// Releases an output this service never took ownership of - the start was refused before the
        /// output became the recording's.
        /// </summary>
        private static void DisposeUnstarted(RecordingOutput output)
        {
            try { output.Dispose(); } catch (Exception ex) { Log.Warning(ex, "Failed to dispose a disk buffer output that never started"); }
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

                    // The output stays open until the stop has closed it, so a boundary that lands
                    // while stopping is still recorded: the save's 1 MiB floor and the seg_*.mkv marker
                    // the purge deletes by are what keep Stop()'s trailing stub harmless.
                    ledger.RecordBoundary(now, next);

                    TaskCompletionSource<string>? tcs = _splitSignaled;
                    if (tcs != null)
                    {
                        _splitSignaled = null;
                        tcs.TrySetResult(next ?? "");
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
                    ledger?.AccountReclaim(segment, size);
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
                _ledger?.RequestStop();
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
                    _ledger?.MarkGone();
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
                _ledger?.RequestStop();
                try { _output.ForceStop(); } catch (Exception ex) { Log.Warning(ex, "Disk buffer output ForceStop threw"); }
            }
        }

        /// <summary>
        /// Builds a saved replay: takes a save lease from the ledger (a recording that is stopping or
        /// gone is refused, with the reason, before anything is announced to the frontend), forces the
        /// current segment to close, selects the closed segments covering the window (since the previous
        /// save when DiskBufferSaveSinceLastSave is on, otherwise the full buffer duration), and
        /// stream-copies them into one mp4, assembled in the segment folder and moved into Replay
        /// Buffers only after ffmpeg succeeded. Returns the output path, the wall-clock save instant and
        /// a bookmark position resolver; null when the save was refused, nothing could be saved or
        /// ffmpeg failed (user is informed here, sidecars are OBSService's job). Recording keeps
        /// running throughout. saveAnnounced runs here, at the moment the lease was granted and the
        /// frontend was told the save started, so feedback for a save the ledger refused is never played.
        /// </summary>
        public static async Task<DiskBufferSaveResult?> SaveAsync(Action? saveAnnounced = null)
        {
            DiskBufferLedger? ledger = _ledger;
            if (!IsActive || ledger == null)
            {
                Log.Warning("Cannot save disk buffer: disk buffer is not active");
                return null;
            }

            // Ask the ledger for permission before announcing anything: a save that arrives after a
            // stop began queues behind the save that stop is waiting for, and by the time it runs the
            // ledger has been cleared. Announcing it first made the frontend play the save flash and
            // then report a buffer that had never written a segment, for a window that was full a few
            // seconds earlier.
            if (!ledger.TryAcquireSaveLease(out DiskBufferSaveBlockReason blocked))
            {
                string reason = blocked switch
                {
                    DiskBufferSaveBlockReason.Stopping => "the recording is stopping",
                    _ => "the recording has stopped",
                };
                Log.Warning($"Cannot save disk buffer: {reason}.");
                await MessageService.ShowModal("Replay Save Failed", $"The replay was not saved because {reason}.", "error");
                return null;
            }

            _ = MessageService.SendFrontendMessage("ReplayBufferSaveStarted", new { });

            // The save's own feedback (the bookmark sound) belongs to this decision, not to the key press:
            // a save the ledger refused above announced nothing and must not click either.
            try
            {
                saveAnnounced?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Disk buffer save feedback failed");
            }

            await _saveLock.WaitAsync();

            DateTime requestedAt = DateTime.Now;
            List<DiskBufferSegment> selected = new();
            List<DiskBufferSegment> dropped = new();
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
                    windowStart = requestedAt - TimeSpan.FromSeconds(ledger.WindowSeconds);
                    if (Settings.Instance.DiskBufferSaveSinceLastSave && _lastSaveEnd.HasValue && _lastSaveEnd.Value > windowStart)
                        windowStart = _lastSaveEnd.Value;
                }

                // Only the segments that reach the concat list are pinned; everything else, including
                // a stub the selection dropped, keeps being reclaimed while the save runs. The probe
                // waits on the muxer closing each file, which can take seconds. A dropped segment is
                // footage missing from the output, and the bookmark resolver needs its span to keep
                // later bookmarks from sitting late by exactly that hole.
                selected = await ledger.SelectSaveWindowAsync(windowStart, async segment =>
                {
                    try
                    {
                        await GeneralUtils.EnsureFileReady(segment.Path);
                        return new FileInfo(segment.Path).Length;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"Skipping disk segment from the save ({ex.Message}): {segment.Path}");
                        return null;
                    }
                }, dropped);

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

                // Concat demuxer options apply to the file line they follow, so the inpoint goes
                // after the first file, not before it.
                var listLines = new List<string>();
                for (int i = 0; i < selected.Count; i++)
                {
                    listLines.Add(FFmpegService.BuildConcatListLine(selected[i].Path));
                    if (i == 0 && inpointSeconds > 0)
                        listLines.Add($"inpoint {inpointSeconds.ToString(CultureInfo.InvariantCulture)}");
                }

                string? codec = await FFmpegService.DetectVideoCodec(selected[0].Path);
                string tagArgs = codec == "hevc" ? " -tag:v hvc1" : "";

                // ffmpeg assembles into the segment folder and the finished file is published into the
                // library only after that succeeded. DiskBufferSaveAssembly owns the staging name, the
                // publish and the partials either leaves behind, and reports every failure instead of
                // throwing: this save runs on a task nobody is watching, and the user has already been
                // told it started.
                var assembly = await DiskBufferSaveAssembly.RunAsync(
                    _segmentFolder,
                    PathUtils.Combine(Settings.Instance.ContentFolder, FolderNames.Buffers, _gameFolderName),
                    $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4",
                    listLines,
                    tagArgs,
                    contentSpan.TotalSeconds);

                if (!assembly.Succeeded || assembly.OutputPath == null)
                {
                    await MessageService.ShowModal("Replay Save Failed", assembly.UserMessage ?? "The replay could not be saved. Check the log for details.", "error");
                    return null;
                }

                outputPath = assembly.OutputPath;
                TimeSpan outputDuration = await FFmpegService.GetVideoDuration(outputPath);
                Log.Information($"Disk buffer replay saved to: {outputPath} ({selected.Count} segments, {outputDuration.TotalSeconds:F1} s)");

                Func<DateTime, TimeSpan> positionFor = new DiskBufferBookmarkResolver(
                    selected, dropped, effectiveWindowStart, outputDuration, contentSpan).PositionFor;
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
                ledger.Unpin(selected);
                ledger.ReleaseSaveLease();
                _saveLock.Release();
            }
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
        /// Startup cleanup: segments left by a crash and staged saves a killed assembly left behind are
        /// stale by definition. Sweeps the live buffer root and every root a recording actually wrote
        /// into while it still exists - the folder a recording uses is fixed at start, and the setting can
        /// move at any time - and reports what it reclaimed, including what it could not delete and will
        /// try again next start. A folder that is gone is skipped quietly.
        /// </summary>
        public static DiskBufferPurgeReport PurgeAll()
        {
            string contentFolder = PathUtils.Normalize(Settings.Instance.ContentFolder);
            int deleted = 0;
            int undeleted = 0;
            long freedBytes = 0;

            foreach (string root in FolderNames.GetDiskBufferRoots())
            {
                if (root.TrimEnd('/', '\\').Length <= 2)
                {
                    Log.Warning($"Refusing to purge disk buffer root '{root}'");
                    continue;
                }
                if (string.Equals(root, contentFolder, StringComparison.OrdinalIgnoreCase) ||
                    contentFolder.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                {
                    // A root must never be or contain the content folders themselves.
                    Log.Warning($"Refusing to purge disk buffer root '{root}': it is or contains the content folder");
                    continue;
                }
                if (!Directory.Exists(root))
                    continue;

                try
                {
                    DiskBufferPurgeReport one = PurgeFolder(root);
                    deleted += one.DeletedFiles;
                    undeleted += one.UndeletedFiles;
                    freedBytes += one.ReclaimedBytes;
                    Log.Information($"Disk buffer startup purge completed for {root}");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, $"Disk buffer startup purge failed for {root}");
                }
            }

            return new DiskBufferPurgeReport(deleted, freedBytes, undeleted);
        }

        // Two markers say a file under the buffer root is ours to delete: the seg_<CCYY-MM-DD>_<hh-mm-ss>.mkv
        // name OBS was given for the segments, and the interrupted-save marker a staged replay is assembled
        // under. Anything else the folder happens to hold is not ours.
        private static bool IsPurgeableFileName(string fileName) =>
            (fileName.StartsWith("seg_", StringComparison.OrdinalIgnoreCase) &&
             fileName.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
            || fileName.EndsWith(DiskBufferSaveAssembly.InterruptedSaveMarker, StringComparison.OrdinalIgnoreCase);

        private static DiskBufferPurgeReport PurgeFolder(string folder)
        {
            if (!Directory.Exists(folder))
                return new DiskBufferPurgeReport(0, 0, 0);

            int deleted = 0;
            int undeleted = 0;
            long freedBytes = 0;
            foreach (string file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (!IsPurgeableFileName(Path.GetFileName(file)))
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
                    // Still on disk, so it is still somebody's problem: counted, not forgotten.
                    undeleted++;
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

            Log.Information($"Purged {deleted} disk buffer file(s), {freedBytes / (1024.0 * 1024.0):F1} MB reclaimed from {folder}"
                + (undeleted > 0 ? $", {undeleted} could not be deleted and will be tried again" : ""));
            return new DiskBufferPurgeReport(deleted, freedBytes, undeleted);
        }
    }

    /// <summary>
    /// What a disk buffer purge did: files gone, bytes back, and files it could not delete this time.
    /// </summary>
    internal sealed record DiskBufferPurgeReport(int DeletedFiles, long ReclaimedBytes, int UndeletedFiles);
}
