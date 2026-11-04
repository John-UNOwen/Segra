# Disk Buffer, Phase 1: OBS file splitting and ffmpeg concat, measured

Phase 1 of the disk-buffer plan (rolling MKV segments on disk, saved to the Replay
Buffers library by a stream-copy concat) was verified before writing any feature code.
The stack was OBS 33.0.0 with ObsKit.NET 1.7.0, .NET 10.0.403, on Windows, Intel Arc
(QSV) plus x264. A throwaway console harness drove OBS directly; no file in this repo
changed, and the harness is not committed. It lives in `%TEMP%\segra-spike` and the
commands to rerun it are at the end of this file.

Short verdict: the approach works. Splitting, forced splits, segment closure, concat
fidelity, HEVC/AV1 and crash survival all behaved as the design needs. Six details in
the design doc were wrong or optimistic, and one of them (timeline drift at segment
junctions) needs a decision in Phase 3.

## What was recorded

Main run: 265 s, x264 at CBR 35000 kbps, `SetFormat(RecordingFormat.Mkv)` with
`WithFileSplitting(dir, maxTimeSeconds: 10, maxSizeMb: 0, "seg_%CCYY-%MM-%DD_%hh-%mm-%ss", "mkv", allowSpaces: false)`,
1280x720@60, one media source (330 s test pattern plus a continuous 1 kHz tone) patched
onto audio tracks 0, 1 and 2 (`AudioMixers = 0b111`, `tracks: 7` in the muxer settings).
28 files, `frames_dropped = 0`. Runs: 10 s splitting, 20 MiB size splitting, a constant
filename format, manual splits, HEVC, AV1, a hard kill, and a 2 s write-rate sampler.
Total recording wall clock about 11.5 minutes.

## Handoff checklist, item by item

1. **Split interval and names.** Checked. Wall-clock gaps between `file_changed` events:
   first 11,683 ms, then 9,980 to 10,013 ms (mean 10,004 ms). Names are exactly the
   filename format expanded at switch time; no `-000` counter is appended unless a name
   collides, in which case OBS appends `_2`, `_3`, ... (`_1` is never used). Size
   splitting works too: `maxSizeMb: 20` gave 22,123,501 B segments (5.5% over) about
   4.99 s apart.
2. **`file_changed` and `next_file`.** Checked. The calldata carries `next_file` only, as
   an absolute path with forward slashes. `RecordingOutput.Path` is set once at `Start()`
   and never updates, so the ledger has to be seeded from `Path` right after `Start()` and
   then chained: closed segment = previous `next_file`, open segment = current `next_file`.
3. **Is the previous segment closed at event time?** Not at the instant of the event.
   An exclusive open inside the handler failed in 26 of 27 events. A poll every 20 ms
   opened every segment, taking 2 to 61 ms (average 30.1 ms, never more than 3 attempts).
   Once openable, the file is complete: `Duration: 00:00:10.03`, 600 video frames, exit 0,
   zero truncation or corruption errors on any probe. A 100 ms delay covers everything
   observed here.
4. **`SplitFile()`.** Checked. Returns true. The matching `file_changed` arrived 108 ms
   and 632 ms after two calls, so budget about 1 s. The manual split does not disturb the
   schedule; the next automatic gap was a normal 10,004 ms and the manual segment
   (4.01 s, 17,698,624 B) behaves like any other.
5. **Segment timestamps.** Checked. Every segment restarts at 0: container `start:`
   0.000000, first video frame `pts_time 0`, `iskey:1`. Audio restarts at 0 with the
   0.021 s AAC encoder delay. `keyint_sec = 1` produced 10 keyframes per 10.03 s segment
   at 0, 1, ... 9 s, for x264, QSV HEVC and QSV AV1 alike. `-f concat` re-times the
   restart-at-0 files correctly.
6. **Concat of 20 segments.** Checked. `-f concat -safe 0 -i concat.txt -map 0 -c copy out.mp4`
   gave 885,142,169 B, duration 200.39 s, exactly 1 video plus 3 audio streams: every
   recording track survives `-map 0`. Decode counts: 12,000 video frames (exactly
   20 × 600), 0 dropped, 0 duplicated; each audio track 9,375 frames / 9,599,984 samples
   (16 samples short of a perfect 200.0 s). `freezedetect(n=0.001, d=0.2)` found 0 freeze
   events, `silencedetect(n=-50dB, d=0.2)` found 0 silence events on all three tracks.
7. **Window cut with `inpoint`.** Checked, with a caveat. `inpoint 3.5` on the first entry
   landed on the 3.0 s keyframe: the first segment contributed 420 frames (7.00 s of its
   10.03 s), the first decoded frame is a keyframe, audio was cut at the same instant, and
   there were no frozen frames, no blank frames and no A/V desync (a constant 13 ms video
   offset from B-frame reordering, identical at every junction). Window edges are
   keyframe-quantised, so at `keyint_sec = 1` the start is up to 1 s early.
8. **HEVC and AV1.** Checked. Available encoders on this box: `obs_x264`, `obs_qsv11_v2`,
   `obs_qsv11_hevc`, `obs_qsv11_av1`, `ffmpeg_svt_av1`, `ffmpeg_aom_av1`. HEVC ran at
   34.8 Mbps, split and concat normally, and its MP4 needed `-tag:v hvc1` for Apple
   players (with the tag the file is byte-identical apart from the tag). AV1 ran, but at
   13.9 Mbps actual against a 35 Mbps CBR target at `target_usage=TU4`. MKV segments need
   no tag.
9. **Write rate.** Checked. OBS `TotalBytes` held 4.19 to 4.27 MB/s, averaging 4.21 MB/s
   (33.6 Mbps) over 264.8 s. Each closed 10 s segment was 44,245,536 to 44,246,719 B.
   `frames_dropped = 0` throughout, including during switches.
10. **HDR.** Not tested. No HDR encode run was done.

## Corrections to the design doc's assumptions

1. `obs_x264` with `rate_control = VBR` ignores `bitrate`. VBR at 8000 wrote 2.87 Mbps and
   VBR at 35000 wrote 2.88 Mbps; CBR at 35000 wrote 34 to 35.4 Mbps. OBS logs accept the
   setting without honouring it. Any disk budget that assumes 35 to 70 Mbps from a VBR
   x264 setting is fiction; budget on CBR, or on measured bytes.
2. `bufsize` is not an x264 key in OBS. x264 wants `use_bufsize` and `buffer_size`. (Segra's
   encoder setup uses `bufsize`, so it is silently doing nothing for x264.)
3. `ObsKit.WithFileSplitting` returns `RecordingOutput` (chainable), not `string?`, and its
   4th argument is the filename format. `RecordingOutput` has no `SetFilenameFormat`; that
   method belongs to `ReplayBuffer`. The default filename format contains a space, so
   `allowSpaces: false` matters for a path we later parse.
4. The first segment is longer than `maxTimeSeconds`: 11.68 s wall, 11.05 s of media,
   660 frames. The muxer's split timer starts about 1 s after media starts flowing. A
   rolling window must not assume segment 1 is 10 s long.
5. `Stop()` fires one extra `file_changed` and leaves a 1,003-byte final file that ffmpeg
   cannot open. Concat logs `Impossible to open ...` and still exits 0, but the ledger must
   filter stubs (size floor, or a quick `ffmpeg -i` validation) rather than trust the count.
6. Directory sizes lie while the muxer holds a file open. At event time the closed
   segment's directory size was 39,817,216 B against a final 44,246,667 B, and the whole
   directory lagged OBS's own counter by 39,975,894 B (about 9 s) at the end, growing in
   ~20 MB jumps only on close. Quota and pruning math should use `RecordingOutput.TotalBytes`
   or the sizes of already-closed segments.
7. A killed process takes `obs-ffmpeg-mux.exe` with it, so no orphan writer keeps appending
   into the buffer. The segment that was open at the kill stayed readable (3,566,795 B,
   9.82 s, 586 frames) and concat-able; only its last ~0.2 s was lost. `-fflags +genpts`
   produced a byte-identical file, so it is not needed.
8. `SupportedVideoCodecs` and `SupportedAudioCodecs` are empty for `ffmpeg_muxer`. Neither
   ObsKit nor OBS rejects a codec/container pair, so any validation has to live in Segra.
9. `Obs.Initialize` makes OBS modules create config directories (`rtmp-services\`,
   `win-capture\`, `text-freetype2\`) in the process working directory. Segra already pins
   its CWD to the app directory (`Program.cs`), so this only bites a harness that forgets to.

## The one thing that needs a decision before Phase 3

With `-c copy`, the concat demuxer advances the timeline by each file's container duration
(10.03 s) rather than by its content (10.00 s). 20 segments produced 12,000 frames but
200.383 s of timeline for 200.000 s of content, about 19 ms of dead time per junction. No
frames are lost, but the saved file's reported duration is inflated. Over a 30 minute save
(roughly 180 segments) that is about 3.4 s of inflation, and `AddReplayBookmarks` derives
the replay start as `endedAt - content.Duration`. Bookmarks copied into a long disk-buffer
replay would therefore sit up to a few seconds late, worsening with buffer length. Options,
in the order I would try them:

- Compute bookmark offsets from the segment ledger (segment index and the segment's own
  wall-clock start) instead of assuming one continuous timeline.
- Keep the linear math but subtract the known per-junction overhead, which is computable
  from the segment count.
- Do nothing, and accept a few seconds of bookmark drift on long saves.

The plan also needs small changes: seed the ledger from `Path` after `Start()` and chain
`next_file` from there; retry opening a just-closed segment for about 100 ms; treat the
first segment and any trailing stub as special cases; prune and preflight with `TotalBytes`
or closed-segment sizes; document that a saved window starts up to 1 s early because cuts
are keyframe-quantised; and base the free-space preflight on CBR-style numbers rather than
on a VBR setting the encoder may ignore.

## Not verified here

- HDR (PQ/BT.2020) metadata surviving MKV to MP4 copy. No HDR run.
- Segra's own capture path (game window capture, hooks, per-game overrides) and the
  existing Session, Buffer and Hybrid modes. The app builds clean with SDK 10 and the spike
  used the same OBS 33 bundle and ObsKit version, but the interactive recording modes were
  not exercised.
- NVENC and AMF (no such GPU on this machine), so encoder coverage is x264 plus QSV only.
- Playback of the concat output in Segra's player and in VLC, and behaviour on an HDD
  rather than NVMe.

## Reproduce

```
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build "$env:TEMP\segra-spike\DiskBufferSpike\DiskBufferSpike.csproj" -c Release -o "$env:TEMP\segra-spike\out"
pwsh -NoProfile -File "$env:TEMP\segra-spike\spike.ps1" -Mode record -Name main -Seconds 265 -SplitTime 10 -SplitSizeMb 0 `
  -Encoder obs_x264 -Format Mkv -Extension mkv -RateControl CBR -Bitrate 35000 -EncSet 'use_bufsize=true,buffer_size=35000' `
  -Profile high -AudioTracks 3 -Mixers 7 -ProbeEvents '2,3' -SplitFileAt '35' -SampleEvery 2 -StatusEvery 10 -Warmup 2
pwsh -NoProfile -File "$env:TEMP\segra-spike\tools\summarize-events.ps1" -RunName main
pwsh -NoProfile -File "$env:TEMP\segra-spike\tools\analyze-run.ps1" -RunName main -SegInfoCount 3 -ConcatStartIndex 5 -ConcatCount 20
pwsh -NoProfile -File "$env:TEMP\segra-spike\tools\window-cut.ps1" -RunName main -StartIndex 5 -Count 4 -Inpoint 3.5 -Tag win35
pwsh -NoProfile -File "$env:TEMP\segra-spike\tools\crash-test.ps1" -Name crash -KillAfterSec 40
pwsh -NoProfile -File "$env:TEMP\segra-spike\tools\analyze-crash.ps1" -RunName crash
```

`out\` holds the extracted `Obs\OBS 33.0.0.zip` next to the harness exe, which is how
Segra gets OBS too. Each run keeps `events.log`, `obs.log`, `samples.csv`,
`segments.jsonl` and the ffmpeg probe logs under `runs\<name>\`. The raw findings file with
every command and log quote is `%TEMP%\segra-spike\FINDINGS.md`.
