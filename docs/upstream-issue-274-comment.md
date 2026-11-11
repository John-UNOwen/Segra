# Comment posted on upstream issue #274

Posted 2026-11-11 as <https://github.com/Segergren/Segra/issues/274#issuecomment-6797370494>.
Source of the body below; release `v2.0.0-beta.5-diskbuffer.1` on the fork.

---

Update: this is built and running against current `main` (`2320324`, tagged `v2.0.0-beta.5`). Branch <https://github.com/John-UNOwen/Segra/tree/feature/disk-buffer>, upstream merged in at `57f7025`.

Shape, as in the original proposal: a fourth recording mode `RecordingMode.DiskBuffer`. Segments are MKV through the ffmpeg muxer, split every 10 s, into `<content>/.diskbuffer/<game>/` by default; the folder is configurable and the settings screen refuses one inside the four content folders. A save forces a split, stream-copies the closed segments covering the window into one MP4, then goes through the ordinary post-save path (`FinalizeSavedReplayAsync`), so bookmarks, thumbnail, waveform, the clip editor and the player all work on the result.

Answers to the three questions I asked:

1. Fourth mode, not a flag on `Buffer`. The RAM paths (`ReplayBuffer`, `_alwaysOnBuffer`) are untouched, and upstream's changes to them merged without conflict.
2. Save scoping follows `ClearReplayBufferAfterSave` (#268, which is also what #49 needed). No separate setting. Measured against current main, 60 s window: first save 61.18 s, a second save 29.1 s later 30.11 s. With the setting off the save is the full window, 61.16 s.
3. Kept 30 minutes (30 s to 1800 s, default 1800).

Storage, because your new cleanups interact with it:

- Segments and a staged save are never storage content. With a 1 GB limit and 1.17 GB of old library files, the startup check logged `Current storage usage: 1.17 GB`, so the 1.17 GB of buffer files beside them were excluded, and `Total files eligible for deletion: 3`. A 400 MB segment under `Replay Buffers/<game>/.buffer-here` and a staged `.segrereplay` in the same folder, both stamped 3 h old, were not in that count and never appeared in a `Deleting Buffer file:` line. The only code that removes segment files is the disk buffer's own purge, on stop and on startup.
- Buffer folders are skipped in `CalculateFolderSize`, so a rolling buffer does not consume the `StorageLimit` figure.
- With the "no game subfolders" setting off, a saved replay goes through `StorageService.GetOutputFolder` and lands flat in `Replay Buffers/<ts>.mp4`, and the library still indexes it as Buffer content. With subfolders on it lands in `Replay Buffers/<game>/<ts>.mp4`.
- `DeleteExpiredContent` works from `AppState.Content`, so rolling segments were never in its way.

Testing on one Windows machine, headless (OBS 33.0.0, x264 CBR 8 Mbps, 720p60, saves through the real hotkey): 55 xunit tests in `Backend/Tests/Segra.Tests`, including one that puts a registered buffer root inside `Replay Buffers` and checks `GetDeletionCandidates`. Two live app runs covering both subfolder settings, both save-scope settings, and the storage-limit run above. No `[ERR]` lines in either log.

One thing I have not run down. Decoding a 300 s saved replay end to end (an earlier build, same code path) prints `non monotonically increasing dts to muxer` for about the first 0.9 s, the `inpoint` cut region. A copy-only remux of that file is clean, PTS is strictly increasing across all 18,060 frames, and hand-built concats of the same segments do not reproduce it. Playback and clipping look normal. Say the word if you think it needs to be settled before the clip editor can be trusted on these files.

Installer for anyone who wants to try it: <https://github.com/John-UNOwen/Segra/releases/tag/v2.0.0-beta.5-diskbuffer.1> (`SegraDiskBuffer-win-Setup.exe`, install notes attached). It is Velopack with app id `SegraDiskBuffer`, so it installs next to an upstream Segra instead of replacing it, though both read the same `%APPDATA%\Segra\settings.json`. Unsigned, so SmartScreen will object.

Still happy to send a PR. If a fourth mode is the wrong shape I would rather hear it now than after the PR.
