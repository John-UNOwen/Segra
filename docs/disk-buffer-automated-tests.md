# Disk buffer: automated test results

Phase 6 closed out the disk buffer feature. This file records what the automated harness measured
on 2026-11-04 on this machine (one desktop, one NTFS drive, Intel iGPU, OBS 33.0.0): the window
sizes, the pruning, the saves, what a crash leaves behind, and what a kill during a save leaves
behind after the fix. Numbers here come from the harness JSON reports, not from a summary written
afterwards.

## What the harness is

A driver around the real app, not a test project. Each run launches the Debug build of `Segra.exe`
and drives it the way a user would, then reads what the app wrote to disk and to `logs.log`.

- Launch: a `.lnk` shortcut executed by `explorer.exe`, `--restart-minimized`. A GUI process started
  directly by `Start-Process` does not receive injected keyboard input, so the app comes in through
  explorer with the rest of the desktop.
- Control: the app's own websocket, `ws://localhost:44030/`, no `Origin` header. Commands used:
  `NewConnection`, `StartRecording`, `StopRecording`, `UpdateSettings` (which needs the whole
  `Settings` object; the websocket's `Settings` reply is not one). The socket is kept drained while
  recording, because a server blocked in a send stops reading the next command.
- Save hotkey: `Ctrl+Alt+F7`, injected with `keybd_event` from a second explorer-launched helper
  process (150 ms modifier hold), so the app's own hotkey broker handles it.
- OBS: OBS 33.0.0 extracted beside the executable from the repo's `Obs\OBS 33.0.0.zip`, which is how
  the app finds its OBS on this machine.
- Media checks: the bundled `ffmpeg.exe`. `ffmpeg -i file` with no output always exits 1, so a probe
  is judged from its output text, not from the exit code. Decodability is checked with
  `-xerror -map 0:v -an -f null -`.
- Logs: assertions only read `logs.log` lines after the line count taken at launch. Log text has the
  account name masked to `<user>` by `GeneralUtils.RedactUsername`, so paths are built from the known
  content folder rather than parsed out of log lines.
- Location: scripts, `.lnk` files and `<test>-report.json` in `%LOCALAPPDATA%\Temp\segra-harness`,
  footage in `%LOCALAPPDATA%\Temp\segra-harness\content`, sidecars in
  `%APPDATA%\Segra\{metadata,thumbnails,waveforms}`. Nothing from the harness is in the repo and
  nothing here is committed except this file.
- Guard: a run aborts if C: drops below 20 GB free.

All runs used Intel QSV encoders, CBR, separate audio tracks, and a 720p60 or 1080p60 capture of the
desktop. Encoder settings were seeded per run; each seeded state is inside that run's report.

## Results

| # | Run | Measured | Outcome |
|---|-----|----------|---------|
| 1 | 30 s window, CBR 8000, 720p60, 90 s recording, one save | peak 6 segment files / 50,138,181 B; save 4 segments / 31.1 s / 4,788,822 B; probe 31.11 s, 1 video + 2 audio, decode 1,860 frames, 0 errors; sidecars 675 / 55,344 / 6,310 B; `State.Content` 1 item | pass |
| 2 | 300 s window, CBR 8000, 720p60, 360 s recording, saves at 120 s, 300 s and 330 s | peak 34 files / 318,629,669 B; saves 15 segments / 151.4 s / 23,079,894 B, 17 segments / 170.4 s / 25,974,109 B, 31 segments / 300.7 s / 45,836,146 B; 4 prune lines, 9.6 MB each; one 1,018,129 B stub skipped by all three saves | pass |
| 3 | 1800 s window, CBR 35000, 1080p60, 1860 s recording, 31 folder samples | peak 180 files / 7,834,410,223 B; 186 segment boundaries; save 181 segments / 1805.2 s / 797,184,789 B; headroom 270,000,000 B per interval, 0 reclaimed; no low-space warnings | pass |
| 4 | HEVC codec with the save, 720p60, CBR 12000, 60 s save | output 91,850,326 B / 61.2 s, `hevc (Main) (hvc1 / 0x31637668)`, 11,964 kb/s, 1 video + 2 audio; peak 7 files / 90,161,095 B | pass |
| 5 | BT.2020 / SMPTE-2084 mkv segments through the save's concat command | synthetic 3 × 3,501,081 B HEVC segments, `inpoint 3` on the first → 9,494,686 B / 27.14 s, `Main 10`, `hvc1`, `bt2020nc/bt2020/smpte2084` intact; the same command without `-map -0:d -map_chapters -1` gave byte-identical output; 10-bit H.264 set → 13,213,623 B / 27.06 s, `avc1`, colour tags intact | pass on synthetic segments, see Not verified |
| 6 | Hard kill while a save runs | original run left an 808,745,240 B mp4 in `Replay Buffers` that restarted as a normal library item; on the fixed build, killed 208 ms after ffmpeg launched, with the staged file 15,204,400 B into a 63,487,327 B mp4: 16 library items before and after, nothing in `Replay Buffers`, partial purged at the next start | pass after the fix |
| 7 | `alwaysOnReplayBuffer` on while `recordingMode` is DiskBuffer | 0 files in `.diskbuffer` for the whole run, `Disk buffer recording started` count 0, always-on buffer started and saved through the RAM path: 29,469,635 B / 29.33 s | pass |
| 8 | RAM path regression, Buffer mode hotkey save and Hybrid mode stop | hotkey save 29,469,635 B / 29.33 s, 0 probe error lines; session file 54,589,921 B / 55.32 s, decode 3,318 frames, 0 errors; `.diskbuffer` stayed at 0 bytes | pass |
| 9 | Per-game `recordingModeOverride` (DiskBuffer, 45 s) against a global 300 s Buffer | first attempt with a copied `notepad.exe` never registered as a recordable game; second attempt with `7zG.exe` was auto-recorded into `.diskbuffer\7-Zip GUI`, peak 7 files / 60,156,420 B, saved 6 segments / 46.1 s into `Replay Buffers\7-Zip GUI`, 494,958 B / 46.14 s | pass on the second attempt |
| 10 | `Stop-Process -Force` in the middle of recording | 7 files / 60,156,878 B left in the segment folder (last segment 0 B), no muxer process survived, next start logged `Purged 7 disk buffer file(s), 66.7 MB reclaimed`, 0 files after, no orphan or recovery lines | pass |

Two runs on the fixed build re-checked the normal save path at both ends of the size range: 7
segments / 61.2 s / 18,490,507 B (60 s window, CBR 8000, 720p60) and 90 segments / 902.0 s /
272,267,718 B (900 s window, peak 91 files / 901,511,742 B, ffmpeg concat finished in 3.09 s at
291x). Both wrote metadata, thumbnail and waveform sidecars, logged `Disk buffer replay saved to:`,
and left no mp4 behind in the segment folder.

## Kill during a save

The original test 6 killed Segra with `Stop-Process -Force` after the save hotkey and found an
808,745,240 B `2026-11-04_12-54-37.mp4` in `Replay Buffers\Manual Recording`. After a restart it was
listed as a normal library item (`type Buffer`, `fileSize 767.30 MB`, `duration 00:30:06.17`) with no
recovery prompt, and probing it reported `Found duplicated MOOV Atom. Skipped it`.

That evidence does not show what it looked like. The log says `[Process 2] PID 35664 completed with
exit code 0` at 12:54:48.795, then `Disk buffer replay saved to: ... (181 segments, 1806.2 s)` at
12:54:48.865, `File verified ready ... 804,571,580 bytes` at 12:54:48.867, `Metadata file created` at
12:54:48.933, and the next log line is a fresh startup at 12:54:53.004. The concat list in `%TEMP%`
was already gone. The kill landed after ffmpeg had finished, in the finalization window, not mid-
write. Walking the top-level atoms makes that visible: the good save from the same run is
`ftyp / free / mdat / free / moov`, 797,184,789 B, where the old moov was renamed to `free` by
`EmbeddedMetadataService.WriteTail`, and the killed file is `ftyp / free / mdat / moov / moov`,
808,745,240 B, whose atoms run exactly to the end of the file. It is not truncated. It has two
complete indexes because the app was killed between the write
of the new tail and the rename of the old one. ffmpeg skips the duplicate, decodes all 108,120
frames, and reports 1,200 non-monotonic dts warnings. It had a metadata sidecar, which is why the
library listed it: the library is built from sidecars, and `RecoveryService` only reports mp4 files
in the content folders that no sidecar claims.

The fix moves the concat output into the segment folder and into the library only after ffmpeg
succeeds (see `DiskBufferService.SaveAsync`). `FolderNames.GetContentTypeFromPath` returns null for
anything under the disk buffer root, `StorageService` skips those paths, `RecoveryService` never
scans them, and `DiskBufferService.PurgeAll` empties the whole tree at OBS init, before the orphan
scan runs. The codebase already does this for cross-drive library moves
(`ContentMigrationService` writes `*.segramigrating` copies and deletes the leftovers at startup).

Re-verified on the fixed build (`fix3` run, 420 s window, CBR 8000, 720p60). Recording started at
19:42:54 and the save hotkey landed at 19:46:25.574. The log after that reads `Attempting to save disk
buffer replay...` at 19:46:26.219, `Skipping disk segment from the save (stub, 1002404 B)` at
19:46:26.328, and `FFmpeg arguments: ... -c copy "<content>\.diskbuffer\Manual Recording\2026-11-04_19-46-26.mp4"`
at 19:46:26.384. `Stop-Process -Force` on the app pid followed at 19:46:26.592, 208 ms after ffmpeg
launched, when the staged file had 15,204,400 B written of the 63,487,327 B it ended up as. The folder
held 21 segment files / 200,381,019 B at its peak and the concat list in `%TEMP%` had 21 lines.

`ffmpeg.exe` and `obs-ffmpeg-mux.exe` outlived the app and ffmpeg finished the staged file on its own.
Content folders held the same 16 mp4 files before the save, after the kill and after the restart, the
sidecar folders held 16 metadata, 15 thumbnail and 15 waveform files throughout, and no file matching
`2026-11-04_19-46-26` existed anywhere outside the disk buffer folder. The partial was there, as a
63,487,327 B mp4 in `.diskbuffer\Manual Recording`.

The next start, at 19:46:27.862, logged `Purged 24 disk buffer file(s), 262.5 MB reclaimed` and the
segment folder came back 0 files / 0 B. `State.Content` listed 16 items with no entry for the killed
save, the log had no `Recovery`, orphan or untracked lines, the websocket sent no recovery message, and
the whole run logged no `[ERR]` lines. A crash in the middle of a concat now leaves nothing in the
library and nothing for the user to clean up.

What the fix does not change: a kill after `File.Move` puts the file in the library but before
`CreateMetadataFile` leaves an mp4 with no sidecar, which is what `RecoveryService` reports as an
orphan, and a kill inside the metadata embed leaves the double-indexed file described above.

## What these runs pinned down

Pruning. A closed segment is prunable once its end is older than the window plus one 10 s segment,
and the newest ledger entry is never pruned. Measured peaks match that: 6 files at a 30 s window, 34
at 300 s, 180 at 1800 s, and the files are what the bitrate says they should be, about 10 MB per 10 s
at CBR 8000 and 43 MB at CBR 35000. Pruning starts after the
window fills, so a 300 s recording began pruning at 12:04:38 and reclaimed 9.6 MB per prune.

Stubs. A forced segment split leaves a small remnant: test 2 kept `seg_2026-11-04_12-01-47.mkv` at
1,018,129 B, and every later save covering that time logged `Skipping disk segment from the save
(stub, 1018129 B)`. `Stop()` also leaves a 1,003 B final file. The filter is `MinSegmentBytes =
1_048_576`. Why the split left a 1,018,129 B file rather than a few kilobytes was not determined.

Monitor headroom. The 60 s disk monitor logs its estimate: `Disk buffer monitor headroom: 67500000 B
written per interval, 0 B reclaimed by pruning` at CBR 8000 and `270000000 B` at CBR 35000, which is
`(video Mbps + 1) / 8` times 60 s. Once pruning catches up the reclaimed figure appears: 63,602,700 B
reclaimed per interval at 12:05:16 in test 2. During a save the estimate goes back to the full write
rate because pinned segments cannot be reclaimed.

Save windows and junction padding. A save covers the requested window, not everything on disk: 4
segments for a 30 s window, 31 for the full 300 s window, 181 for 1800 s. Concatenating N segments
adds dead timeline at each junction, measured 29 ms per junction over 181 segments (1800 s requested,
1805.2 s in the file) and 0.7 s over 31.

Folder naming. Segments land in `.diskbuffer\<game>\` and replays in `Replay Buffers\<game>\`, with
the game name taken from the executable's file description: `7zG.exe` produced `7-Zip GUI` in both
places. With no game recorded the folder is `Manual Recording`.

Size of a saved replay versus the window it covers. With QSV H.264, the saved file holds about a
tenth of the segment bytes it was built from: 181 segments covering 7,921 MB of MKV produced a
797,184,789 B mp4, and 4 segments covering 40 MB produced 4,788,822 B. Probes of those outputs report
1.2 Mbps and 3.5 Mbps against a CBR 8000 / 35000 target. The RAM buffer path does not drop that
padding: the same 720p60 CBR 8000 desktop content saved through the always-on buffer came out at
29,469,635 B for 29.33 s, 7,999 kb/s. QSV HEVC keeps its bytes (test 4: 91,850,326 B output from 7
segments, 11,964 kb/s).

Purges. Recording stops empty the recording's segment folder; a start that finds leftovers empties
the whole disk buffer tree first (`Purged 7 disk buffer file(s), 66.7 MB reclaimed`,
`Purged 183 disk buffer file(s), 7585.2 MB reclaimed`) before `RecoveryService` scans for orphaned
content.

`diskBufferSaveSinceLastSave`. After a save, later saves take only what arrived since that save;
clearing the flag restores the full window. Test 2 logged `DiskBufferSaveSinceLastSave changed from
'True' to 'False'` at 12:04:48.859, and the save that followed covered the whole 300 s window
(31 segments, 300.7 s) instead of the 170 s since the previous save.

## Not verified

- Frontend behaviour. The harness drives the websocket and reads logs. It never looked at a window, so
  library cards, save feedback, and what a recovery prompt looks like are untested.
- Preflight against a second drive. This machine has one fixed drive, so the branch that checks the
  segment drive and the content drive separately only ever compared a drive with itself.
- A genuinely full disk. Every run aborted below 20 GB free, and no run got near it. The in-recording
  monitor never produced a low-space warning or stopped a recording.
- Sleep, hibernation, or logoff during a save or a recording.
- The `PurgeAll` refuse guards, which need a `DiskBufferFolder` pointing at or containing the content
  folder.
- A real HDR recording from OBS. Test 5 concatenated synthetic BT.2020 / SMPTE-2084 mkv segments
  through the save's exact ffmpeg command; the app's own HDR capture path was not run. The addendum
  over two real OBS segments exited 1 with no error lines captured, so it settles nothing.
- Staging across volumes. `File.Move` becomes a copy plus delete when the segment folder is on another
  drive, which was not testable here.
- Multi-monitor and display-picker behaviour. Every run captured the primary display.

## Reproducing a run

1. `dotnet build Segra.csproj -c Debug -f net10.0-windows10.0.19041.0 -v minimal` in the repo, and OBS
   33.0.0 extracted beside `Segra.exe` (once).
2. Seed `%APPDATA%\Segra\settings.json` before launch: harness content folder, `recordingMode`,
   `diskBufferDuration`, CBR bitrate, resolution, frame rate, `runOnStartup` false,
   `startupWindowMode` Minimized, and `SaveReplayBuffer` bound to Ctrl+Alt+F7.
3. Launch with `Start-Process explorer.exe -ArgumentList '"<shortcut>.lnk"'` and wait for
   `WebSocket server started` plus `OBS initialized successfully` in `logs.log`. Only one Segra may run;
   a Debug build refuses to start alongside another.
4. Connect to `ws://localhost:44030/` without an `Origin` header, send `NewConnection`, read the `State`
   message, and keep draining while recording.
5. Start recording with `{"Method":"StartRecording"}` (or auto-record a game via a `games` entry that
   names a real executable), wait for `Disk buffer recording started to ...`.
6. Inject the save hotkey with `keybd_event` from an explorer-launched helper.
7. Assert on log lines after the launch marker, then probe and decode-scan the output with the bundled
   ffmpeg, and write the run's JSON report.
8. For a crash test, `Stop-Process -Id <pid> -Force` on the pid recorded at launch, then start the app
   again and read the purge, orphan, `State.Content` and sidecar state.

Scripts: `hlib.ps1` plus `run-t1.ps1` ... `run-t10.ps1`, `run-fix1.ps1`, `run-fix3.ps1`, and
`<test>-report.json` beside them in `%LOCALAPPDATA%\Temp\segra-harness`. They are scratch, outside the
repo.

## Follow-ups

- An interrupted `EmbeddedMetadataService` write leaves two complete moov atoms in the file (the
  808,745,240 B file above). Players cope, ffmpeg warns, and the library lists it as an ordinary
  replay. Worth deciding whether the sidecar should be written before or after the embed, or whether a
  start-up check should look for duplicated indexes. The same window exists on the RAM path.
- The 1,018,129 B stub left by a forced split, and why a forced split leaves a file that size.
- QSV H.264 saves hold about a tenth of the bytes their segments cover while QSV HEVC holds nearly all
  of them. The preflight estimate and the monitor use the nominal bitrate, so a H.264 disk buffer
  reserves roughly ten times what a save will need. Whether that is QSV padding in the MKV or something
  the mp4 muxer drops is not established.
- A kill in the short window between the staged file moving into the library and the sidecar being
  written leaves an mp4 that `RecoveryService` will report as an orphan. That path is unchanged by the
  fix and was not exercised.
- `docs/disk-buffer-phase1-findings.md` records that stream-copy concat drops the muxer's CBR padding;
  these runs show the drop for QSV H.264 and no drop for QSV HEVC or for the RAM buffer path.
