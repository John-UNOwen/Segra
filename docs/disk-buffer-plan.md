# Handoff: Segra fork with a disk-backed rolling replay buffer (up to 30 min)

**Audience:** a coding agent (or human) implementing this from scratch.
**Upstream:** https://github.com/Segergren/Segra (GPLv2). Code facts below were checked against upstream `main` at commit `bebb7a6` (2026-11-01). Line numbers drift, so find code by the symbol names given, not by line.

---

## 1. Goal

Add a new capture mode, **Disk Buffer**, to Segra:

- Recording runs continuously while a game is detected, but footage goes to **short files on disk**, not to OBS's RAM replay buffer.
- Only the most recent **N minutes** are kept (user-configurable, **30 s to 30 min, default 30 min**). Older footage is deleted continuously, so disk use stays bounded.
- Pressing the existing **Save Replay Buffer** hotkey writes the last N minutes (see the save rules in 5.4) into one MP4. That MP4 shows up in Segra's library as normal "Replay Buffer" content, with thumbnail, waveform, bookmarks and the clip editor all working.

### Non-goals (v1)

- Don't change the existing RAM `Buffer` or `Hybrid` modes, or the "always-on display buffer" (`_alwaysOnBuffer`). They keep working as they do upstream.
- No "Hybrid + Disk Buffer" combination, which would write footage to disk twice.
- No Linux-specific work. Keep the code platform-neutral (`Backend/Recorder` is shared code), but test on Windows only.

---

## 2. Why this works: facts already verified in the code

| Fact | Where |
|---|---|
| Segra records through **ObsKit.NET** (the same author's C# wrapper for libobs). | `Backend/Recorder/OBSService.cs`, repo `Segergren/ObsKit.NET` |
| ObsKit exposes OBS file splitting: `RecordingOutput.WithFileSplitting(directory, maxTimeSeconds, maxSizeMb, filenameFormat, extension, …)` and `RecordingOutput.SplitFile()`, which forces a split now. | `ObsKit.NET/src/ObsKit.NET/Outputs/RecordingOutput.cs` |
| OBS emits the `file_changed` signal (ObsKit `OutputSignal.FileChanged`, calldata `next_file`) each time a new split file starts. | `ObsKit.NET/src/ObsKit.NET/Signals/SignalTypes.cs` |
| `RecordingFormat` includes `Mkv`, `Mp4` and `HybridMp4`. Session mode currently uses `HybridMp4` and falls back to the plain ffmpeg muxer. | `RecordingOutput.cs`, `OBSService.cs` (search `useHybridMp4`) |
| The video encoder is set to `keyint_sec = 1`, so a keyframe lands every second and stream-copy cuts are accurate to within 1 s. | `OBSService.cs` (search `keyint_sec`) |
| FFmpeg is already bundled. `FFmpegService` has `BuildConcatListLine`, `RunWithProgress`, `RunSimple`, `GetMetadata`, `ExtractDuration`, `DetectVideoCodec`, and `FFmpegErrors.DescribeForUser` for errors. | `Backend/Media/FFmpegService.cs` |
| The existing RAM-buffer save pipeline handles post-save work, and the disk buffer should reuse it: `ContentService.CreateMetadataFile(..., Content.ContentType.Buffer, ...)`, `CreateThumbnail`, `CreateWaveformFile`, `SettingsService.LoadContentFromFolderIntoState(true)`, `AddReplayBookmarks(recording, contentId, endedAt)`. | `OBSService.SaveReplayBuffer()` |
| `AddReplayBookmarks` derives the replay's start from `content.Duration` and `endedAt`, so it works unchanged for disk-buffer saves. | `OBSService.AddReplayBookmarks` |
| Modes are `enum RecordingMode { Session, Buffer, Hybrid }`. Per-game overrides use `GameRecordingModeOverride`. | `Backend/Core/Models/Settings.cs`, `Backend/Games/GameSettingsService.cs` |
| The hotkey action is `HotkeyAction.SaveReplayBuffer`. It is gated by `OBSService.CanSaveReplay` and calls `OBSService.SaveReplayWithFeedback()`. | `Backend/Windows/Input/HotkeyCaptureService.cs`, `OBSService.cs` |
| Content folders: `FolderNames.Sessions = "Full Sessions"` and `FolderNames.Buffers = "Replay Buffers"`, both under `Settings.Instance.ContentFolder`. | `Backend/Shared/FolderNames.cs` |
| `StorageService` computes usage by **recursing the whole content folder**, and cleanup deletes the oldest files in `Full Sessions` / `Replay Buffers`. | `Backend/Windows/Storage/StorageService.cs` |
| Default video bitrate is VBR 35–70 Mbps. | `Settings.cs` (`_minBitrate`, `_maxBitrate`) |
| The frontend capture mode cards and buffer length input live in `Frontend/src/Components/Settings/CaptureModeSection.tsx` and `VideoSettingsSection.tsx`. The TS type is in `Frontend/src/Models/types.ts` (`recordingMode`). | Frontend |
| The auto-updater points at upstream releases: `UpdateService.Source = new GithubSource("https://github.com/Segergren/Segra", …)`. | `Backend/App/UpdateService.cs` |

---

## 3. Design overview

```
game detected
   │
   ▼
OBS RecordingOutput (ffmpeg_muxer, MKV, split every 10 s)  ──►  <DiskBufferFolder>/<game>/seg_YYYY-MM-DD_hh-mm-ss.mkv  (rolling)
   │  file_changed signal
   ▼
DiskBufferService.Ledger  [path, startedAt (wall clock), pinned]
   │  on each file_changed: prune segments that ended more than (N + 10 s) ago and are not pinned
   │
hotkey "Save Replay Buffer"
   │
   ▼
SplitFile() → wait for file_changed (the previous segment is now closed)
   → pick the newest segments covering the save window
   → pin them → ffmpeg concat (stream copy, inpoint on the first file) → "Replay Buffers/<game>/<timestamp>.mp4"
   → reuse the existing post-save pipeline (metadata, thumbnail, waveform, bookmarks, library reload)
   → unpin → prune
```

---

## 4. Key decisions (with reasons). Don't change these without a reason.

1. **New enum value `RecordingMode.DiskBuffer`.** Don't add a toggle on the existing `Buffer` mode. This keeps upstream code paths untouched and makes rebasing easier.
2. **Separate settings** from the RAM buffer, so switching modes doesn't overwrite the RAM buffer length:
   - `DiskBufferDuration` (seconds, default **1800**, clamped to **30–1800**).
   - `DiskBufferFolder` (string, default `<ContentFolder>/.diskbuffer`).
   - `DiskBufferSaveSinceLastSave` (bool, default **true**; see 5.4).
   - Segment length is an internal constant, `SegmentSeconds = 10`, not a user setting.
3. **Segments are MKV via the plain `ffmpeg_muxer`, not Hybrid MP4.** File splitting is best tested on the ffmpeg muxer, MKV segments survive crashes, and MKV concatenates cleanly. If Phase 1 shows Hybrid MP4 splitting works just as well, it's acceptable, but MKV is the default.
4. **10-second segments.** For 30 minutes that's about 180 files: fine on NTFS, and pruning overshoot stays under 10 s. Save latency doesn't depend on segment length, because a save forces a split.
5. **Track segments by wall-clock time, not ffprobe.** Record `startedAt = DateTime.Now` when each `file_changed` fires. A segment's span is from its start to the next segment's start. Running ffprobe on 180 files per save would take seconds. Only the first selected segment needs an `inpoint`, and the 1 s keyframe snap is acceptable.
6. **Output is MP4, stream copy, without `+faststart`.** Upstream replay files aren't faststart either, and faststart on a 15 GB file rewrites the whole file a second time. For HEVC add `-tag:v hvc1` (use `FFmpegService.DetectVideoCodec`). Use `-map 0` to keep every audio track.
7. **The segment folder sits outside `Full Sessions` and `Replay Buffers`, and is excluded from storage-usage accounting.** Otherwise the library scanner would list raw segments, and the storage-limit cleanup would either count up to 16 GB of rolling buffer against the user's limit or delete segments mid-save.
8. **Purge segments when recording stops** (after any in-flight save finishes) and **on app startup** (to clean up after crashes). This matches the RAM buffer, where footage is lost on stop.
9. **One save at a time.** Use a `SemaphoreSlim`, like `_replaySaveSemaphore` in the RAM path, with a separate instance. Recording keeps running during a save.

---

## 5. Implementation plan

Work in phases. Each phase ends in a buildable, runnable state with its own small commit(s).

### Phase 0: Fork setup

1. Fork `Segergren/Segra`, clone it, and create a branch `feature/disk-buffer`.
2. Build per `CONTRIBUTING.md`: .NET SDK 10.0.x, Node 20+, `./build-local.sh` (pick Windows, or set `SEGRA_BUILD_TARGET=windows`; run it from Git Bash on Windows). Confirm the stock app records in Session and Buffer modes before changing anything.
3. **Stop the upstream updater from replacing the fork.** In `Backend/App/UpdateService.cs`, point `Source` and `BetaSource` at the fork's GitHub repo, or disable update checks behind a build constant. Do this before handing anyone a build.
4. Optional, recommended: `CONTRIBUTING.md` asks contributors to open an issue before starting a feature. Open one upstream proposing "disk-backed replay buffer mode" and link this design. If it's accepted, target upstream and keep the fork minimal.

**Done when:** the fork builds, runs, records normally, and won't auto-update to upstream.

### Phase 1: Spike to verify OBS behavior (do this before writing real code)

Behind a temporary dev flag, start a `RecordingOutput` with `SetFormat(RecordingFormat.Mkv)` and `WithFileSplitting(dir, maxTimeSeconds: 10, extension: "mkv")`, wired to the same encoders and audio tracks as session mode. Then verify:

- [ ] Files split about every 10 s, and `FileChanged` fires with `next_file`.
- [ ] `SplitFile()` returns true, and a new file starts within about 1 s.
- [ ] When `FileChanged` fires, the **previous** file is closed and readable. If not, wait with `EnsureFileReady(path)` on it.
- [ ] Concatenating about 20 segments with `ffmpeg -f concat -safe 0 -i list.txt -map 0 -c copy out.mp4` plays smoothly: no frozen frames, no audio clicks at segment joins, A/V stays in sync, and **all audio tracks** are present.
- [ ] Check timestamps with `ffprobe` (does each segment start near 0 or continue the previous one?). The concat demuxer should handle either, but record what you find.
- [ ] H.264, HEVC and AV1 all work. If an HDR display is available, check that the HDR (PQ/BT.2020) metadata survives MKV→MP4 copy.
- [ ] The write rate looks sane in Task Manager at the default bitrate.

**Done when:** every box is checked or has a documented workaround. Write the findings into the PR description. If splitting is unreliable, **stop and report back** rather than building on it.

### Phase 2: Settings and models

Files: `Backend/Core/Models/Settings.cs`, `Backend/Core/SettingsService.cs`, `Backend/Games/GameSettingsService.cs`, `Frontend/src/Models/types.ts`.

1. Add `DiskBuffer` to `enum RecordingMode`. It uses a JSON string converter, so check that existing settings files still load.
2. Add `DiskBufferDuration`, `DiskBufferFolder` and `DiskBufferSaveSinceLastSave` to the settings model. Copy the existing pattern (backing field, `[JsonPropertyName]`, change logging in `SettingsService`). Clamp the duration to 30–1800 on set.
3. Add `DiskBufferDuration` to `GameRecordingModeOverride` and to the effective-settings resolution (`eff.*` in `OBSService`), so per-game overrides work.
4. Add the TS types: `'DiskBuffer'` in the `RecordingMode` union, plus the three new fields.

**Done when:** the settings round-trip through save/load, and old settings files load with defaults.

### Phase 3: `DiskBufferService` (new file `Backend/Recorder/DiskBufferService.cs`)

Keep the disk buffer logic out of the 4,500-line `OBSService.cs`. `OBSService` should only call into this class.

```csharp
internal static class DiskBufferService
{
    const int SegmentSeconds = 10;
    record Segment(string Path, DateTime StartedAt) { public int Pins; }

    // state: List<Segment> _ledger (guarded by lock), RecordingOutput? _output,
    //        DateTime? _lastSaveEnd, SemaphoreSlim _saveLock = new(1, 1)

    public static bool Start(RecordingOutput output, string gameFolder, int durationSec);
    public static Task StopAsync();      // waits for an in-flight save, stops the output, purges segments
    public static bool IsActive { get; }
    public static Task<bool> SaveAsync(); // the save flow below
    public static void PurgeAll(string root); // used on startup and stop
}
```

#### 3.1 Start

- Called from `OBSService`'s output-setup block (search `if (isReplayBufferMode || isHybridMode)`). Add a branch for `isDiskBufferMode`. Build the output the same way the session branch does (same `tracks` mask, video encoder, audio encoders, `Stopped += OnOutputStopped` for disk-full handling). Then call `SetFormat(RecordingFormat.Mkv)` and `WithFileSplitting(<DiskBufferFolder>/<sanitizedGameName>, SegmentSeconds, 0, "seg_%CCYY-%MM-%DD_%hh-%mm-%ss", "mkv", allowSpaces: false)`.
- Subscribe to `FileChanged`. On each event: set the previous segment's end, append `new Segment(next_file, DateTime.Now)`, then call `Prune()`.
- Seed the ledger with the first file. Either read it from the output after `Start()`, or treat the first `FileChanged` as the start. Phase 1 shows which works.
- **Preflight free space** on the segment drive before starting. Estimate as `maxBitrateMbps / 8 × DiskBufferDuration × 1.15` MB, and if there isn't room for **twice** that (buffer plus one full save), show the existing modal and fall back to the RAM `Buffer` mode with a warning.

#### 3.2 Prune (on every `FileChanged`)

- Delete a segment if its **end** is older than `now − (DiskBufferDuration + SegmentSeconds)` and its `Pins == 0`. Never delete the open (newest) segment.
- If a file is locked, skip it and retry on the next prune.

#### 3.3 Mode switch and hotkey

- `OBSService.CanSaveReplay`: also return true when `DiskBufferService.IsActive`.
- `OBSService.SaveReplayWithFeedback`: if disk buffer mode is active, call `DiskBufferService.SaveAsync()` instead of `SaveReplayBuffer()`. Keep the existing `ReplayBufferSaveStarted` / `ReplayBufferSaved` frontend messages and sounds, so the UI works without changes.
- Handle recording stop: wherever `OBSService` stops and disposes the outputs (search `DisposeRecording`, `_output`), call `DiskBufferService.StopAsync()`.

#### 3.4 Save flow (`SaveAsync`)

1. `await _saveLock.WaitAsync()`. Set `requestedAt = DateTime.Now` and send `ReplayBufferSaveStarted`.
2. Call `_output.SplitFile()`, then wait (up to 5 s) for the next `FileChanged`. The segment that was open is now closed; run `EnsureFileReady` on it.
3. Compute the window:
   - `windowEnd = requestedAt`
   - `windowStart = requestedAt − DiskBufferDuration`
   - if `DiskBufferSaveSinceLastSave` and `_lastSaveEnd` is later, `windowStart = _lastSaveEnd`. This mirrors upstream, which resets the RAM buffer after a save so the next save only contains new footage, and it avoids writing 30 minutes of overlap twice.
4. Select the closed segments whose span overlaps the window, and **pin** them (`Pins++`).
5. Write `concat.txt` in a temp folder using `FFmpegService.BuildConcatListLine`. On the first entry add `inpoint <seconds>`, where seconds = `windowStart − firstSegment.StartedAt`, clamped at ≥ 0.
6. Output path: `<ContentFolder>/Replay Buffers/<sanitizedGameName>/<yyyy-MM-dd_HH-mm-ss>.mp4`, the same folder and naming the RAM path uses, so the library picks it up.
7. Run FFmpeg through `FFmpegService.RunWithProgress`: `-f concat -safe 0 -i concat.txt -map 0 -c copy [-tag:v hvc1 if HEVC] <out.mp4>`. A full 30-min save can be 8–16 GB, so it must run off the UI thread, and recording continues during it.
8. On success, run the same steps as the end of `OBSService.SaveReplayBuffer()`:
   - send `ReplayBufferSaved`
   - `EnsureFileReady`
   - `ContentService.CreateMetadataFile(out, Content.ContentType.Buffer, game, igdbId, audioTrackNames, audioTrackTypes, gameExePath)`
   - `CreateThumbnail`, `CreateWaveformFile`
   - `SettingsService.LoadContentFromFolderIntoState(true)`
   - `AddReplayBookmarks(recording, id, requestedAt)`

   Pull the shared part of `SaveReplayBuffer` into a private helper (for example `FinalizeSavedReplayAsync(path, request, recording, requestedAt)`) and call it from both paths. Keep that refactor in its own commit.
9. On failure, show a modal with `FFmpegErrors.DescribeForUser(ex)` and delete any partial output.
10. `finally`: unpin the segments, set `_lastSaveEnd = requestedAt` (only on success), delete `concat.txt`, release the lock.

#### 3.5 Purge

- `PurgeAll` deletes everything under `DiskBufferFolder`. Call it from app startup (where `StorageService` or `OBSService` initialise) and from `StopAsync()`.

**Done when:** in a real game, holding a 30-minute buffer keeps disk use flat at about N minutes × bitrate, and the hotkey produces a playable MP4 in the library with working thumbnail, waveform, editor and bookmarks.

### Phase 4: Storage integration

File: `Backend/Windows/Storage/StorageService.cs` (plus wherever the content scanner enumerates folders; check `ContentService` and `FolderNames.GetContentTypeFromPath`).

1. Exclude `DiskBufferFolder` from the recursive usage calculation (the `Directory.GetFiles(folderPath, "*", … RecurseSubdirectories = true …)` call). Exclude by path prefix, so this still works if the user points `DiskBufferFolder` somewhere inside the content folder.
2. Make sure the library scanner never lists `.mkv` segments. The default `.diskbuffer` folder is outside the four content folders, but verify it, including the case where a user moves the folder.
3. If `DiskBufferFolder` is on a different drive than the content folder, saves cross drives. That's fine, but the free-space preflight has to check both drives.

**Done when:** a full 30-minute buffer doesn't count toward or trigger the storage limit, and no segment ever shows up in the UI.

### Phase 5: Frontend

Files: `CaptureModeSection.tsx`, `VideoSettingsSection.tsx`, `types.ts`, and the per-game override UI (search for `recordingModeOverride` usages).

1. Add a fourth capture mode card, **"Disk Buffer"**, worded something like: "Keeps the last N minutes on disk instead of RAM. Press <hotkey> to save." Reuse `formatBufferLength` and the `hotkeyFor` helper.
2. Show a duration input for `DiskBuffer`: minutes and seconds, min 30 s, max 30 min. Hide the RAM "max size (MB)" field in this mode.
3. Show a live disk estimate under the input: `≈ {maxBitrate/8 × duration / 1000} GB on disk, plus up to the same again while saving a full window`.
4. Add a folder picker for `DiskBufferFolder`, with a hint to use an HDD or a secondary SSD.
5. Add the option to the per-game override dropdown.
6. Save progress: the existing `ReplayBufferSaveStarted` / `ReplayBufferSaved` messages already drive the feedback. If `RunWithProgress` exposes progress cheaply, an optional progress toast for long saves is a nice addition.

**Done when:** the mode can be selected and configured globally and per game, and settings persist.

### Phase 6: Test checklist

- [ ] Durations of 30 s, 5 min and 30 min. Disk use stays flat over a 60-minute play session.
- [ ] Saving right after the game starts (the buffer is shorter than N) saves whatever exists.
- [ ] Two saves 2 minutes apart with `DiskBufferSaveSinceLastSave = true`: the second file is about 2 minutes long. With it set to false, the second file is the full N.
- [ ] Pressing the hotkey twice quickly: the second save queues and doesn't corrupt anything.
- [ ] Saving a full 30-minute window while still playing: recording continues, no segments are pruned mid-save, and FPS doesn't drop noticeably.
- [ ] Game exits during a save: the save completes, then segments are purged.
- [ ] Kill Segra mid-recording, relaunch: the stale segments are purged.
- [ ] Fill the disk (small test partition): recording stops cleanly through the existing `OnOutputStopped` handling, with a user-visible message.
- [ ] Multi-track audio (game, mic, Discord): every track is present in the saved MP4 and the editor shows them.
- [ ] Bookmarks and auto-highlights (for example CS2) made inside the window show up at the correct times in the saved replay.
- [ ] H.264, HEVC and AV1 outputs play in Segra's player and in VLC.
- [ ] The RAM `Buffer`, `Hybrid` and `Session` modes behave exactly as before (regression).

---

## 6. Disk sizing reference (30-minute window)

| Video bitrate | Rolling buffer | Peak during a full 30-min save |
|---|---|---|
| 35 Mbps | ≈ 7.9 GB | ≈ 15.8 GB |
| 50 Mbps | ≈ 11.3 GB | ≈ 22.5 GB |
| 70 Mbps (upstream VBR max) | ≈ 15.8 GB | ≈ 31.5 GB |

Write rate is constant at the bitrate (35–70 Mbps ≈ 4.4–8.8 MB/s), about 16–32 GB per hour of play. That's well within SSD endurance for typical use, but it's why the folder should be configurable and an HDD is a reasonable target.

---

## 7. Risks and open questions (resolve in Phase 1 or note in the PR)

1. **Segment closure timing.** Does `file_changed` fire after the previous file is fully finalized? If not, rely on `EnsureFileReady`.
2. **Join artifacts.** AAC priming or timestamp resets could cause tiny clicks at segment boundaries. If so, try `-fflags +genpts` on input, or MPEG-TS segments as a fallback container.
3. **Hybrid MP4 splitting.** If it works, MP4 segments would avoid the MKV→MP4 copy step. That's not required.
4. **HDR.** Confirm the color metadata survives. If not, add explicit `-color_primaries/-color_trc/-colorspace` options when copying.
5. **Upstream churn.** `OBSService.cs` changes often. Keep the fork's changes in `OBSService.cs` to a few small hook calls; all real logic lives in `DiskBufferService.cs`.

---

## 8. Rules for the implementing agent

- Keep commits small, and keep commit messages short (one line, imperative). One logical change per commit.
- Never put a machine's or person's user name in commits, code comments or docs. Use a placeholder like `<username>` for paths.
- Don't reformat or reorder unrelated code, especially in `OBSService.cs`. Diffs should be minimal, so rebasing on upstream stays easy.
- Don't change the behavior of the existing RAM `Buffer`, `Hybrid` or `Session` modes.
- Match the existing style: Serilog `Log.Information/Warning/Error`, the `MessageService.ShowModal` error pattern, and `PathUtils` for paths.
- If Phase 1 finds a blocker, stop and report back rather than working around it silently.
