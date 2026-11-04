# Draft: upstream issue proposal (not opened yet)

Intended title: **Disk-backed rolling replay buffer (up to 30 minutes)**

---

## The problem the RAM buffer runs into

`ReplayBuffer` holds footage in memory, so a long buffer costs RAM at the recording bitrate.
At the default 35 to 70 Mbps VBR range, a 10 minute buffer is about 2.6 to 5.2 GB, and a
30 minute buffer is 7.9 to 15.8 GB before OBS will even be comfortable. That is why
`replayBufferDuration` tops out at 600 s in the settings UI: past a few minutes the feature
stops being something a gaming PC can afford.

I want a replay buffer that keeps 30 minutes of footage available without putting 15 GB of
it into RAM.

## Proposal

A fourth capture mode, **Disk Buffer**. While a game is detected, footage goes to short
files on disk instead of the RAM buffer, and only the most recent N minutes are kept
(N configurable from 30 s to 30 min, default 30 min). Older segments are deleted as new
ones close, so disk use stays flat at roughly N minutes of bitrate.

Pressing the existing **Save Replay Buffer** hotkey forces a split, picks the closed
segments covering the save window, and writes them into one MP4 in `Replay Buffers` via
`ffmpeg -f concat -safe 0 -i concat.txt -map 0 -c copy`. From there it joins the existing
save pipeline unchanged: `ReplayBufferSaved` message, `CreateMetadataFile` with
`ContentType.Buffer`, thumbnail, waveform, library reload, `AddReplayBookmarks`. The clip
editor, bookmarks, waveform and player all work on the result because the output is an
ordinary MP4 in the ordinary place.

What it does not change: the RAM `Buffer` and `Hybrid` modes, `_alwaysOnBuffer`, and the
`Session` path. No "Hybrid + Disk Buffer" combination, because that would write the same
footage twice.

## Shape of the implementation

- New `RecordingMode.DiskBuffer` plus three settings: `DiskBufferDuration` (30 to 1800 s,
  default 1800), `DiskBufferFolder` (default `<ContentFolder>/.diskbuffer`), and
  `DiskBufferSaveSinceLastSave` (default true, mirroring how the RAM buffer resets after a
  save).
- A new `Backend/Recorder/DiskBufferService.cs` holds all of the logic: the segment ledger,
  pruning, the save flow, and the purge. `OBSService` gets a handful of hook calls (a mode
  branch in the output-setup block, `CanSaveReplay`, `SaveReplayWithFeedback`, the stop and
  dispose paths) plus one extracted helper so the RAM save and the disk save share the
  post-save steps. Keeping `OBSService.cs` edits to hooks is deliberate; that file changes
  often and a small diff keeps rebases cheap.
- Segments are MKV through the plain `ffmpeg_muxer`, split every 10 s, which is about 180
  files for a 30 minute window. Segments are tracked by wall-clock time recorded on the
  `file_changed` signal, never by probing 180 files per save.
- The segment folder sits outside the four content folders and is excluded from storage
  accounting, so a rolling buffer neither counts against `StorageLimit` nor gets deleted by
  `EnsureStorageBelowLimit` mid-save. Segments are purged when recording stops and on
  startup, so a crash does not leave 15 GB behind.
- Free space is checked before starting: enough room for the window plus one full save.

## Numbers

| Video bitrate | Rolling 30 min buffer | Peak during a full save |
|---|---|---|
| 35 Mbps | 7.9 GB | 15.8 GB |
| 50 Mbps | 11.3 GB | 22.5 GB |
| 70 Mbps | 15.8 GB | 31.5 GB |

Write rate is constant at the bitrate, 4.4 to 8.8 MB/s, so 16 to 32 GB per hour of play.
That is fine on an SSD and is the reason the segment folder should be user-configurable.

## Already verified, before writing feature code

Measured with OBS 33.0.0 and ObsKit.NET 1.7.0 on Windows (x264 and QSV, 1280x720@60, three
audio tracks). Full log in the fork at `docs/disk-buffer-phase1-findings.md`.

- Splits land every 10.00 ±0.02 s after the first segment, and `file_changed` delivers
  `next_file`. `SplitFile()` returns true with the event arriving 108 to 632 ms later, and
  the schedule continues normally.
- A just-closed segment needs about 30 ms before its handle is released (2 to 61 ms over 27
  events), then it is complete and error-free.
- Concatenating 20 segments preserved 12,000 video frames exactly (0 dropped, 0 duplicated)
  and all three audio tracks, with 0 freeze events and 0 silence events at the joins.
- A window cut with `inpoint` lands on the previous keyframe, starts clean, and shows no
  frozen frames or A/V drift. With `keyint_sec = 1` that means window edges are accurate to
  within 1 s.
- HEVC and AV1 split and concat cleanly; HEVC in MP4 needs `-tag:v hvc1`.
- Killing the recorder mid-segment left every segment readable and concat-able.

Two measurements complicate the plan, and I would like your read on them. `obs_x264` with
`rate_control = VBR` ignores `bitrate`, so a disk budget built from a VBR setting is not
trustworthy for x264 (CBR is honoured). And `-c copy` concat advances the timeline by each
segment's container duration instead of its content, about 19 ms per junction, which inflates
a 30 minute save's reported duration by roughly 3.4 s and would push bookmarks copied by
`AddReplayBookmarks` a few seconds late. My plan is to derive bookmark offsets from the
segment ledger rather than one continuous timeline.

## Questions for you

1. Is a fourth capture mode the right shape, or would you rather see this as a flag on
   `Buffer`? I chose the enum value to keep existing code paths untouched, but that means
   touching the mode card grid and the per-game override UI.
2. Should segments live under the content folder (hidden dot-folder, excluded from
   accounting) or in a separate location entirely, given `CalculateFolderSize` recurses the
   whole content tree?
3. Is 30 minutes the right ceiling, and should the UI warn about the 8 to 16 GB peak a full
   save costs on top of the buffer?
4. Would you take a PR for this, and if so should the shared post-save helper
   (`FinalizeSavedReplayAsync`) land as its own commit first?

Status: built in a local fork on a `feature/disk-buffer` branch, GPL intact. Phase 1 (the
verification spike above) is done; settings, `DiskBufferService`, storage integration and
the frontend come next. I am writing this before building the rest so the effort lands where
you actually want it.
