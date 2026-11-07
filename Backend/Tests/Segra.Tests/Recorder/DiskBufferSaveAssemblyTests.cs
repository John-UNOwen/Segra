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
            // The rename into the library is a copy followed by a delete whenever the buffer folder is on
            // another volume, and even on one volume the destination can refuse the file: an antivirus
            // scan or the indexer opening what ffmpeg just closed. The fallback copies into the same
            // folder under the interrupted-save marker and renames that - a rename inside one folder -
            // into the final name.
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
            Assert.Empty(FilesUnder(_library));

            string[] staged = FilesUnder(_staging);
            Assert.Single(staged);
            Assert.Equal("assembled replay", File.ReadAllText(staged[0]));
        }

        // A folder that never got created holds nothing; a failed publish must not create one either.
        private static string[] FilesUnder(string folder, string pattern = "*") =>
            Directory.Exists(folder) ? Directory.GetFiles(folder, pattern) : Array.Empty<string>();

        /// <summary>The normal case: the staged file is renamed into its library name.</summary>
        private sealed class RenamingSteps : DiskBufferSaveAssembly.ISteps
        {
            public string? ConcatListPath { get; private set; }
            public bool ConcatListExisted { get; private set; }

            public Task RunConcatAsync(string ffmpegArguments, string stagedPath, double expectedContentSeconds)
            {
                ConcatListPath = ExtractQuotedPath(ffmpegArguments, "-i");
                ConcatListExisted = ConcatListPath != "" && File.Exists(ConcatListPath);
                File.WriteAllText(stagedPath, "assembled replay");
                return Task.CompletedTask;
            }

            public void MoveFile(string source, string destination) => File.Move(source, destination);

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
