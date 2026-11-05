# Disk buffer: manual test checklist

The automated pass measured the disk buffer's machinery. A headless harness drove the real app against real OBS and a real filesystem and reported window sizes, pruning, segment stubs, saves at 30 s, 5 min, 15 min and 30 min, HEVC output, colour tags through the save's concat command, a kill during a save, a kill during recording, the always-on buffer staying in RAM, the RAM mode regression and a per-game override. Those numbers, the crash findings and the harness recipe are in [disk-buffer-automated-tests.md](disk-buffer-automated-tests.md), and the OBS splitting measurements are in [disk-buffer-phase1-findings.md](disk-buffer-phase1-findings.md). This sheet does not repeat them. Where a row depends on a measured figure it links instead of restating it.

What the harness could not see is the part a person touches. It launched the app minimized, so it never drew a settings screen, a library card, a modal or a hotkey sound, and it never looked at a game. It ran on one machine with one fixed drive, one monitor, no HDR capture, no second disk, no sleep or hibernation, and no player other than the bundled ffmpeg. Everything below is a human check on a real machine: the rendered layout, real game capture, playback in the players people actually use, machines with more than one drive, and behaviour over an hour of play.

## Setup

Use a normal build launched from the desktop, with your own recording path and the default keybindings (F8 bookmark, F9 start or stop recording, F10 save) unless a row says otherwise. Keep the buffer folder open in File Explorer while you work: Settings → Video Settings shows it as Disk Buffer Folder when the mode is Disk Buffer, and left empty it is a `.diskbuffer` folder inside your recording path. Keep `%APPDATA%\Segra\logs.log` open. A few rows are only visible there.

Each row is a few minutes unless a duration is written into it. Row ids are for reporting.

## Mode selection and layout

Settings → Capture Mode.

| # | Do | You should see | If this is broken |
|---|----|----------------|-------------------|
| A1 | Look at the cards. | Four cards: Session Recording, Replay Buffer, Hybrid (Session + Buffer), Disk Buffer. The active mode's card has the accent border. | A card missing or duplicated, or the border on the wrong card after a switch. |
| A2 | Read the Disk Buffer card at a 300 s length. | "Keeps the last 5 minutes of gameplay on disk so you can save it as a clip. Press F10 to save. Uses disk space." Below it, "Estimate: ~X GB for 5 minutes." | The card shows the RAM buffer length, the hotkey is missing from the sentence, or the estimate line is absent or 0.0. |
| A3 | Drag the window narrow, then wide. | Two columns of two cards below 1280 px, four in a row at 1280 px and above. Card text and the estimate line wrap without being cut. | Cards overlap, text is clipped, or the estimate line leaves the card. |
| A4 | Change Disk Buffer Length to 30, then to 1800, without leaving Settings. | Card text and estimate follow immediately: "30 seconds", then "30 minutes". | The card keeps the old length until restart. |
| A5 | Disable the Save Replay Buffer hotkey in Settings → Hotkeys, then look at the card. | The sentence reads "Press the Save Replay Buffer hotkey to save." with no key chip. | A stale key is shown, or the sentence drops the hotkey entirely. |
| A6 | Switch to Disk Buffer from another mode while a recording is running. | "(applies to next recording)" next to the Capture Mode and Video Settings headings, and the banner "Some changes will apply from the next recording. The current recording keeps the settings it started with." Both clear when the recording ends. | No hint while a recording runs on older settings, or the hint stays after the recording ends. |

## Settings inputs

Settings → Video Settings, the fields that appear only in Disk Buffer mode.

| # | Do | You should see | If this is broken |
|---|----|----------------|-------------------|
| B1 | Type 30 in Disk Buffer Length and tab out. | 30 stays. The estimate drops to the 30 second figure. | The value jumps to something else, or the estimate does not move. |
| B2 | Type 1800, then 1801. | 1800 stays. 1801 snaps back to 1800 when you leave the field. | 1801 is accepted and a recording keeps more than 30 minutes of segments. |
| B3 | Type 29, then clear the field, then type letters. | 29 becomes 30. Blank and letters become 300. | A blank or zero length reaches the recorder and a recording keeps nothing. |
| B4 | Read the estimate line at 1800 s with the default VBR maximum of 70 Mbps. | "Estimate: ~14.7 GB for 30 minutes of segments. Saving a full window writes a second copy, so the peak is about 14.7 GB more." The Capture Mode card shows the same figure. It is built from the maximum bitrate, so under CBR it is an upper bound, not what the drive writes. | The figure ignores the length or the bitrate you changed. |
| B5 | Leave Disk Buffer Folder empty. | The field shows your recording path with `.diskbuffer` appended, displayed with forward slashes. Recording writes there. | Segments land in the recording path root or inside a content folder. |
| B6 | Type a folder on a second drive, for example `D:\segra-buffer`. | Segments appear under that folder. Saves still land in `Replay Buffers` under the recording path. | Segments still under the recording path, or the finished mp4 left on the buffer drive. |
| B7 | Type a folder inside `Replay Buffers` or `Full Sessions`, then try the recording path itself. | The field snaps back to the accepted value when you leave it. No modal and no message appear. The reason is only in `%APPDATA%\Segra\logs.log`: "Ignoring disk buffer folder ...: inside a content folder". | The path sticks, and mkv segments show up in the storage figure or sit next to saved replays. |
| B8 | Change the length or the folder while a recording runs, then save. | The recording keeps what it started with: same segment folder, same window for the save. The pending hint and banner show. | A save mid-recording covers the new window, or segments start arriving in a second folder. |

## Recording and saving on a real machine

Start from Settings → Capture Mode set to Disk Buffer.

| # | Do | You should see | If this is broken |
|---|----|----------------|-------------------|
| C1 | Manual recording for about 2 minutes: the Display Capture button, then Stop. | Files appear under `<buffer root>\Manual Recording`, a new one about every 10 s, each with a name like `seg_2026-11-05_14-22-31.mkv`. The folder empties a few seconds after Stop, leaving the empty game folder behind. | No files, files far from 10 s long, or files left behind that survive the next start. |
| C2 | Launch a real game with Auto-Record Games on. | Segments under `<buffer root>\<game>`, the recording card in the menu, the recording start sound. | Nothing recorded, or a full session file written alongside the segments. |
| C3 | Press F10 while recording. | A flash on the recording card and the save sound at the instant you press it. The library gains a card when the assembly finishes. A 30 minute save takes noticeably longer, and there is no progress bar in between. | No feedback at all, or no card even though the log says the save finished. |
| C4 | With a 30 minute window, press save and keep playing for the whole save. | Recording keeps writing, the game keeps its frame rate, and the card arrives. | Recording stops during the save, the game drops frames, or the app becomes unresponsive. |
| C5 | Open the new card in the library, then open the clip. | Thumbnail, a duration badge, the waveform in the editor, and the clip plays. The duration reads a few seconds longer than the window you saved (see the junction padding figure in [disk-buffer-automated-tests.md](disk-buffer-automated-tests.md)). | Blank thumbnail, 00:00 duration, no waveform, or a file that will not open. |
| C6 | Press F8 at three known moments, 10 s, 40 s and 120 s before saving. Open the clip and check each marker. | Each marker sits at its offset in the saved clip, and the last one lands near the end. The offsets hold in a 30 minute clip as well as a short one. | Markers clumped at the start or the end, missing, or drifting further off the longer the clip is. |
| C7 | In a game whose integration adds bookmarks late, save first and let the integration add one a few seconds later. | The late bookmark still appears in the saved clip at the right offset. | Late bookmarks never reach the clip. |
| C8 | Set the length to 300, record about 7 minutes and press save at 5, 6 and 7 minutes, leaving Save since last save at its default. | The first clip covers the whole 5 minutes, the next two about 60 s each, oldest footage first, with no footage repeated between files. | Every clip covers the full window and the same minutes appear in several files. |
| C9 | Turn Save since last save off. There is no control for it in Settings: close Segra, edit `diskBufferSaveSinceLastSave` to false in `%APPDATA%\Segra\settings.json`, restart. | The next save covers the whole window again even though a save happened a minute earlier. | Saves keep cutting off at the previous save. |
| C10 | Press F10 within a couple of seconds of the recording starting. | A modal titled "Replay Save Failed" saying the buffer has not finished writing a segment yet and to try again in a moment. Recording continues and nothing is added to the library. | A 0 second or empty clip in the library, or the recording dies. |
| C11 | Press F10 twice inside a second, and press it again while a long save is running. | Saves run one after the other and each produces its own clip. Filenames are the save time, so they differ. | A truncated or unopenable file, a second save that does nothing, or a crash. |

## Disk behaviour you can watch

Watch the buffer folder in File Explorer and the Storage Usage panel in Settings → Storage Settings side by side.

| # | Do | You should see | If this is broken |
|---|----|----------------|-------------------|
| D1 | Record for 20 minutes with a 5 minute window. | The segment count settles near 34 files and stays there. Old files disappear as new ones arrive, and drive free space stops falling. | The count keeps climbing, or free space keeps dropping for the whole session. |
| D2 | Watch Storage Usage while the buffer fills. | The used figure and the Buffers and Other segments stay where they were. The segment bytes are never counted. | Usage climbs with the segments, or the limit starts deleting recordings because of them. |
| D3 | Set a storage limit below the current library size (the Storage Limit Warning modal appears, apply it), record for 10 minutes in Disk Buffer mode and press save, then Stop. Cleanup runs when a recording ends and again at startup. | Old Full Sessions and Replay Buffers items are deleted down to the limit. The segment folder is untouched and the save you made still plays. | Segments deleted mid-recording, a save failing, or the buffer folder emptied while you were playing. |
| D4 | Search the recording path for `seg_*.mkv`. | They exist only under the buffer folder. Nothing in the library, in Full Sessions or in Clips or Highlights shows a segment. | A segment sitting in a content folder or listed as a card. |
| D5 | Drive the buffer drive low: a small partition, or free space dropped to a few hundred MB while recording. | A modal titled "Recording stopped: running low on disk space" naming the disk buffer drive and the MB free. Recording stops and the last file is still playable. Detection does not immediately restart recording. | Recording continues until OBS fails with no message, or the segment being written is left corrupt. |
| D6 | Ask for a window that cannot fit: CBR at 100 Mbps with an 1800 s window needs about 49 GB free, or point the buffer folder at a nearly full drive. | A warning modal titled "Not enough disk space for disk buffer" naming the drive, the MB free and the MB needed for the window. The recording then runs on the RAM replay buffer, and Settings still shows Disk Buffer for the next recording. | Recording refuses to start, or the mode silently changes and stays changed. |
| D7 | Start a 30 minute save and kill Segra from Task Manager while it runs, then start Segra again. | Nothing new in the library, no recovery prompt, and the buffer folder is emptied at startup. The half-built file never reaches the content folders. Details in the Kill during a save section of [disk-buffer-automated-tests.md](disk-buffer-automated-tests.md). | A half-written mp4 listed as an ordinary replay, or leftovers you have to delete by hand. |
| D8 | Guard test. Close Segra, set `diskBufferFolder` in `%APPDATA%\Segra\settings.json` to the recording path itself or a folder above it, and start Segra. | The startup purge refuses instead of deleting: "Refusing to purge disk buffer root ...: it is or contains the content folder" in the log. Your recordings are still listed at the next start. Storage Usage drops toward 0, because the whole library subtree now counts as buffer territory. | Your recordings deleted at startup, or the purge walking into Full Sessions. |

## Per-game override

Settings → Game Recording & Overrides, with a game in the list.

| # | Do | You should see | If this is broken |
|---|----|----------------|-------------------|
| E1 | Turn on the Recording Mode override for a game. | Four buttons: Hybrid, Session, Replay Buffer, Disk Buffer, with your current global values copied into the override. | No Disk Buffer button, or the override opens empty. |
| E2 | Pick Disk Buffer. | A Disk Buffer Length field appears, prefilled with the global length, with the hint "Leave blank to use the global N sec." There is no per-game folder field. Segments for this game go to the global buffer folder, in the game's own subfolder. | A per-game folder field appears, or the length field is missing. |
| E3 | Set 300 for this game, leave the global at 1800, then play it. | That game's segments folder holds about 34 files and its save covers about 5 minutes. A different game with no override still gets 1800. | The game records at the global window, or the override leaks into another game. |
| E4 | Blank the per-game length, then change the global length and record again. | The game follows the global value you set after blanking it. | The game keeps an old length after the global changed. |
| E5 | Type 29, then letters, in the per-game length. | 30, then the global value. | An out-of-range length reaches the recorder. |
| E6 | Compare the folder names for a game whose program name differs from the entry name in your list. | The segment subfolder and the `Replay Buffers` subfolder use the captured program's own name (7zG.exe gives `7-Zip GUI`), and the two match each other. | The two folders disagree, or the settings entry name is used. |

## Playback and formats

| # | Do | You should see | If this is broken |
|---|----|----------------|-------------------|
| F1 | Save with an HEVC codec selected, then open the mp4 in Windows Media Player, in VLC, and on a phone. | All three play it, with sound, and the file is not re-encoded first. | A phone or QuickTime refusing the file, which means the save wrote the wrong codec tag. |
| F2 | Open the same HEVC clip inside Segra on a machine without Microsoft's HEVC Video Extension. | The HEVC banner in Video Settings, or in the player a panel saying the video is H.265 and needs the extension, with an Install button. | A black player with no explanation. |
| F3 | On an HDR monitor, turn on Record in HDR, record a game and save. Play the clip on that monitor, then on an SDR screen or a phone. | HDR looks correct on the HDR display. It looks flat or washed out on the SDR display, which is what the setting describes. The automated pass only concatenated synthetic segments, so this row is the first real check of the app's own HDR path. | Dull, blown out or wrongly dark on the HDR monitor itself, or a clip that is SDR although the game was captured in HDR. |
| F4 | Record with separate audio tracks on (game, mic, a voice app), save, and open the clip. | The player's audio menu lists the tracks by name. Muting or lowering one affects only that track. | One mixed track only, or tracks present but unnamed. |
| F5 | In a 30 minute save, scrub to 0:02, 0:10 and 0:30, then scrub back to the very start. | Each jump lands within about a second of where you asked, and the picture resumes cleanly. Cuts are keyframe-based, so a landing a second early is normal. | A long freeze, a jump to the wrong place, or a garbled frame near the start. |
| F6 | Compare the card's file size for a 30 minute save against the estimate line. | The mp4 is far smaller than the segments it was assembled from, and far smaller than the estimate. Measured figures are in [disk-buffer-automated-tests.md](disk-buffer-automated-tests.md). | A save larger than the estimate, which points at segments not being trimmed. |

## Modes that must not change

| # | Do | You should see | If this is broken |
|---|----|----------------|-------------------|
| G1 | Session mode, record a game from launch to close. | One file in Full Sessions, bookmarks and the editor working as before. No segments anywhere. | A segment folder appearing, or the session file missing. |
| G2 | Replay Buffer mode, save with F10. | A clip in Replay Buffers, the buffer resetting so the next save holds only new footage, and the log naming the RAM path rather than the disk one. | The save going through the segment pipeline, or segments appearing in Buffer mode. |
| G3 | Hybrid mode. | A full session file and a working hotkey save, with no `.diskbuffer` folder written. | The hybrid save taking the disk path. |
| G4 | Always-on Replay Buffer on, mode Disk Buffer, no game running. Save with F10. | The green Replay Buffer label above the Start and Stop button. The save uses the RAM path, and the buffer folder stays empty for the whole run. | The always-on buffer writing segments, or its label disappearing in Disk Buffer mode. |
| G5 | Look at Buffer Duration and Buffer Maximum Size in each mode. | They appear for Replay Buffer and Hybrid, and also in Disk Buffer mode when Always-on Replay Buffer is on. Their values survive a switch to Disk Buffer and back, and are never overwritten by the disk length. | The fields vanishing in a RAM mode, or the RAM lengths being replaced by the disk buffer length. |

## Known rough edges

These came out of the automated pass and are still unsettled. The linked section has the evidence.

- Frontend behaviour was never observed by the harness. It drove the websocket and read logs and never looked at a window, so nothing in the Mode selection, Settings inputs and Per-game override groups above has ever been rendered and checked.
- Preflight on a second drive is code-verified only. The machine it was tested on has one fixed drive, so the branch that checks the segment drive separately from the content drive only ever compared a drive with itself. Row B6 and row D6 are the first real runs of it.
- A genuinely full disk was never tested. Every automated run aborted below 20 GB free and the in-recording monitor never produced a low-space warning or stopped a recording. Row D5 is unexplored territory.
- Sleep, hibernation and logoff during a recording or a save were not exercised, including the teardown path that stops the output without waiting for a graceful finish.
- A kill inside the metadata embed step can leave a file with two complete indexes. Players cope, ffmpeg warns, and the library lists it as an ordinary replay. A kill between the finished file moving into the library and its sidecar being written leaves an mp4 that the recovery scan reports as an orphan. Both windows are unchanged by the recent fix and the second one was never exercised. The same window exists on the RAM buffer path.
- Two things the UI does not expose at all: Save since last save has no control, so row C9 needs a settings edit, and a long save shows no progress, so row C3 has only the flash and sound at the start to go on.

## When a row fails

Note the row id, the mode and window length you were using, what you saw and what you expected, and the lines around that moment in `%APPDATA%\Segra\logs.log`. If the failure involves missing, leftover or half-written files, leave the recording path and the buffer folder as they are and note the file names and sizes instead of cleaning up.
