using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Segra.Backend.Recorder;
using Segra.Backend.Shared;
using Xunit;

namespace Segra.Tests.Recorder
{
    /// <summary>
    /// Publishing a staged disk-buffer replay into the library. ffmpeg assembles the file inside the
    /// segment folder and only a finished assembly may appear under a library name: a rename into the
    /// library folder is atomic on one volume, copies across volumes are not, and a rename on the same
    /// volume can still fail on a transient lock (an antivirus scan or the indexer opening the file
    /// ffmpeg just closed). This machine has a single volume, so the copy paths are driven through the
    /// injected steps rather than a second drive.
    /// </summary>
    public class DiskBufferSaveAssemblyTests : IDisposable
    {
        private const string FileName = "2026-11-04_19-46-26.mp4";

        private readonly string _root = Path.Combine(Path.GetTempPath(), "segra-tests-assembly-" + Guid.NewGuid().ToString("N"));
        private readonly string _staging;
        private readonly string _library;

        public DiskBufferSaveAssemblyTests()
        {
            // The staging side stands in for the buffer folder; the library folder does not exist yet,
            // because a save that arrives after the recording folder moved has to create it.
            _staging = Path.Combine(_root, ".diskbuffer", "Manual Recording");
            _library = Path.Combine(_root, "Replay Buffers", "Manual Recording");
            Directory.CreateDirectory(_staging);
        }

        [Fact]
        public async Task AFinishedConcatIsPublishedUnderTheFinalName()
        {
            var steps = new RenamingSteps();

            var outcome = await DiskBufferSaveAssembly.RunAsync(
                _staging, _library, FileName, ConcatLines(), "", 61.2, steps);

            Assert.True(outcome.Succeeded);
            Assert.Equal(PathUtils.Normalize(Path.Combine(_library, FileName)), outcome.OutputPath);
            Assert.Equal("assembled replay", File.ReadAllText(Path.Combine(_library, FileName)));

            // Nothing left in the buffer folder, and nothing under an interrupted-save marker either.
            Assert.Empty(FilesUnder(_staging));
            Assert.Empty(FilesUnder(_library, "*" + DiskBufferSaveAssembly.InterruptedSaveMarker));

            // The concat list ffmpeg read is written by the assembly and gone with the save.
            Assert.NotNull(steps.ConcatListPath);
            Assert.True(steps.ConcatListExisted);
            Assert.False(File.Exists(steps.ConcatListPath));
        }

        private static List<string> ConcatLines() => new() { "file 'C:/buffer/seg_2026-11-04_19-45-26.mkv'" };

        [Fact]
        public async Task ARenameTheDestinationRefusesFallsBackToAMarkerCopy()
        {
            // Even on one volume the destination can refuse the file: an antivirus scan or the indexer
            // opening what ffmpeg just closed. The fallback copies into the same folder under the
            // interrupted-save marker and renames that - a rename inside one folder - into the final name.
            var steps = new RenameRefusedOnce();

            var outcome = await DiskBufferSaveAssembly.RunAsync(
                _staging, _library, FileName, ConcatLines(), "", 61.2, steps);

            Assert.True(outcome.Succeeded);
            Assert.Equal("assembled replay", File.ReadAllText(Path.Combine(_library, FileName)));

            // One library file, under its real name, and no marker partial left behind anywhere.
            Assert.Single(FilesUnder(_library));
            Assert.Empty(FilesUnder(_library, "*" + DiskBufferSaveAssembly.InterruptedSaveMarker));
            Assert.Empty(FilesUnder(_staging));
        }

        [Fact]
        public async Task ACopyThatAlsoFailsLeavesTheStagedReplayAlone()
        {
            // When the copy cannot finish either, the staged file is the only complete copy of that
            // footage left: the recording's segments are purged as soon as the stop finishes. Nothing may
            // be created under a library name, and the staged original survives.
            var steps = new RenameAndCopyRefused();

            var outcome = await DiskBufferSaveAssembly.RunAsync(
                _staging, _library, FileName, ConcatLines(), "", 61.2, steps);

            Assert.False(outcome.Succeeded);
            Assert.False(string.IsNullOrWhiteSpace(outcome.Reason));
            Assert.False(string.IsNullOrWhiteSpace(outcome.UserMessage));
            Assert.True(outcome.StagedPreserved);
            Assert.Empty(FilesUnder(_library));

            string[] staged = FilesUnder(_staging);
            Assert.Single(staged);
            Assert.Equal("assembled replay", File.ReadAllText(staged[0]));
        }

        // A folder that never got created holds nothing; a failed publish must not create one either.
        private static string[] FilesUnder(string folder, string pattern = "*") =>
            Directory.Exists(folder) ? Directory.GetFiles(folder, pattern) : Array.Empty<string>();

        [Fact]
        public async Task AFailingConcatExplainsItselfInsteadOfThrowing()
        {
            // %TEMP% full or unwritable, the content folder moved away mid-recording, ffmpeg out of
            // process space: the assembly step is where those land, and the save that reaches them has
            // no catch of its own. A failure has to come back as a failure with a reason, and the
            // partial ffmpeg left behind must not survive as a file with a replay's name.
            var steps = new ConcatFailsPartway();

            var outcome = await DiskBufferSaveAssembly.RunAsync(
                _staging, _library, FileName, ConcatLines(), "", 61.2, steps);

            Assert.False(outcome.Succeeded);
            Assert.False(string.IsNullOrWhiteSpace(outcome.Reason));
            Assert.False(string.IsNullOrWhiteSpace(outcome.UserMessage));
            Assert.False(outcome.StagedPreserved);
            Assert.Empty(FilesUnder(_staging));
            Assert.Empty(FilesUnder(_library));
        }

        [Fact]
        public async Task AnUnusableOutputFolderExplainsItselfInsteadOfThrowing()
        {
            // The recording's library folder deleted or replaced by a file while the recording ran:
            // creating it is the first thing the assembly does, and a throw from here used to vanish
            // into a task nobody was watching.
            string blocked = Path.Combine(_root, "blocked");
            File.WriteAllText(blocked, "not a folder");
            string libraryFolder = Path.Combine(blocked, "Manual Recording");

            var outcome = await DiskBufferSaveAssembly.RunAsync(
                _staging, libraryFolder, FileName, ConcatLines(), "", 61.2, new RenamingSteps());

            Assert.False(outcome.Succeeded);
            Assert.False(string.IsNullOrWhiteSpace(outcome.Reason));
            Assert.False(string.IsNullOrWhiteSpace(outcome.UserMessage));
            Assert.Empty(FilesUnder(_staging));
        }

        /// <summary>ffmpeg writes part of the file and then fails.</summary>
        private sealed class ConcatFailsPartway : DiskBufferSaveAssembly.ISteps
        {
            public Task RunConcatAsync(string ffmpegArguments, string stagedPath, double expectedContentSeconds)
            {
                File.WriteAllText(stagedPath, "half a replay");
                throw new IOException("There is not enough space on the disk");
            }

            public void MoveFile(string source, string destination) => File.Move(source, destination);

            public bool SameVolume(string source, string destination) => true;

            public void CopyFile(string source, string destination) => File.Copy(source, destination, overwrite: true);
        }

        [Fact]
        public async Task TheStagedSaveCarriesTheMarkerThePurgeDeletesBy()
        {
            // ffmpeg assembles under a name the buffer purge recognises as ours. A hard kill mid-concat
            // lets ffmpeg finish the file after Segra is gone, and the next start has to be able to tell
            // that multi-GB leftover from footage somebody should keep.
            var steps = new RenamingSteps();

            var outcome = await DiskBufferSaveAssembly.RunAsync(
                _staging, _library, FileName, ConcatLines(), "", 61.2, steps);

            Assert.True(outcome.Succeeded);
            Assert.Equal(FileName + DiskBufferSaveAssembly.InterruptedSaveMarker, Path.GetFileName(steps.StagedPath));
            Assert.Equal(PathUtils.Normalize(_staging), PathUtils.Normalize(Path.GetDirectoryName(steps.StagedPath) ?? ""));
        }

        [Fact]
        public async Task ACrossVolumeSaveIsCopiedIntoPlaceInsteadOfRenamed()
        {
            // A buffer folder on another drive: File.Move there is a copy followed by a delete, and a kill
            // during it leaves a truncated mp4 under the library name, which is exactly what staging the
            // assembly in the buffer folder was written to prevent. The publish has to copy under the
            // marker and rename the finished copy inside its own folder, where a rename is atomic.
            var steps = new OtherVolumeSteps();

            var outcome = await DiskBufferSaveAssembly.RunAsync(
                _staging, _library, FileName, ConcatLines(), "", 61.2, steps);

            Assert.True(outcome.Succeeded);
            Assert.False(steps.RenameAttempted);
            Assert.Equal("assembled replay", File.ReadAllText(Path.Combine(_library, FileName)));
            Assert.Empty(FilesUnder(_staging));
            Assert.Empty(FilesUnder(_library, "*" + DiskBufferSaveAssembly.InterruptedSaveMarker));
        }

        /// <summary>
        /// Staging folder and library folder on different drives. This machine has one volume, so the drive
        /// question is answered at the seam; what has to be observable is that a cross-volume publish does
        /// not rename. A rename across volumes is File.Move's copy followed by a delete, and a kill in the
        /// middle of it leaves a truncated mp4 sitting under a library name.
        /// </summary>
        private sealed class OtherVolumeSteps : DiskBufferSaveAssembly.ISteps
        {
            public bool RenameAttempted { get; private set; }

            public Task RunConcatAsync(string ffmpegArguments, string stagedPath, double expectedContentSeconds)
            {
                File.WriteAllText(stagedPath, "assembled replay");
                return Task.CompletedTask;
            }

            public bool SameVolume(string source, string destination) => false;

            public void MoveFile(string source, string destination)
            {
                // Only a rename between folders is the cross-volume copy in disguise; the rename from the
                // marker into its final name happens inside the destination folder and is a real rename.
                if (!string.Equals(Path.GetDirectoryName(source), Path.GetDirectoryName(destination), StringComparison.OrdinalIgnoreCase))
                {
                    RenameAttempted = true;
                    throw new IOException("A rename across volumes is a copy, not a rename");
                }

                File.Move(source, destination);
            }

            public void CopyFile(string source, string destination) => File.Copy(source, destination, overwrite: true);
        }

        /// <summary>The normal case: the staged file is renamed into its library name.</summary>
        private sealed class RenamingSteps : DiskBufferSaveAssembly.ISteps
        {
            public string? ConcatListPath { get; private set; }
            public bool ConcatListExisted { get; private set; }
            public string? StagedPath { get; private set; }

            public Task RunConcatAsync(string ffmpegArguments, string stagedPath, double expectedContentSeconds)
            {
                StagedPath = stagedPath;
                ConcatListPath = ExtractQuotedPath(ffmpegArguments, "-i");
                ConcatListExisted = ConcatListPath != "" && File.Exists(ConcatListPath);
                File.WriteAllText(stagedPath, "assembled replay");
                return Task.CompletedTask;
            }

            public void MoveFile(string source, string destination) => File.Move(source, destination);

            public bool SameVolume(string source, string destination) => true;

            public void CopyFile(string source, string destination) => File.Copy(source, destination, overwrite: true);
        }

        /// <summary>
        /// The first rename - the one that would have to move the file between folders - is refused; the
        /// rename inside the destination folder works.
        /// </summary>
        private sealed class RenameRefusedOnce : DiskBufferSaveAssembly.ISteps
        {
            private int _moves;

            public Task RunConcatAsync(string ffmpegArguments, string stagedPath, double expectedContentSeconds)
            {
                File.WriteAllText(stagedPath, "assembled replay");
                return Task.CompletedTask;
            }

            public void MoveFile(string source, string destination)
            {
                if (_moves++ == 0)
                    throw new IOException("The process cannot access the file because it is being used by another process");
                File.Move(source, destination);
            }

            public bool SameVolume(string source, string destination) => true;

            public void CopyFile(string source, string destination) => File.Copy(source, destination, overwrite: true);
        }

        /// <summary>No publish path works: both the rename and the copy are refused.</summary>
        private sealed class RenameAndCopyRefused : DiskBufferSaveAssembly.ISteps
        {
            public Task RunConcatAsync(string ffmpegArguments, string stagedPath, double expectedContentSeconds)
            {
                File.WriteAllText(stagedPath, "assembled replay");
                return Task.CompletedTask;
            }

            public bool SameVolume(string source, string destination) => true;

            public void MoveFile(string source, string destination) =>
                throw new IOException("The process cannot access the file because it is being used by another process");

            public void CopyFile(string source, string destination) =>
                throw new IOException("There is not enough space on the disk");
        }

        private static string ExtractQuotedPath(string arguments, string option)
        {
            int at = arguments.IndexOf(option + " \"", StringComparison.Ordinal);
            if (at < 0)
                return "";
            int start = at + option.Length + 2;
            int end = arguments.IndexOf('"', start);
            return end < 0 ? "" : arguments.Substring(start, end - start);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { /* temp scratch */ }
        }
    }
}
