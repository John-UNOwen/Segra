namespace Segra.Backend.Recorder
{
    /// <summary>
    /// What the disk monitor knows when it works out how much room the rolling buffer needs: the
    /// configured write rate, the length of its check interval, what pruning reclaimed during the last
    /// interval, the buffer window, and whether a save is holding footage right now.
    /// </summary>
    internal readonly record struct DiskBufferMonitorSample(
        long WriteBytesPerSecond,
        long IntervalSeconds,
        double ReclaimedBytesPerSecond,
        long WindowSeconds,
        bool SaveInFlight);

    /// <summary>
    /// The rolling disk buffer's space arithmetic, kept out of OBSService so it can be checked against
    /// explicit numbers instead of global settings. The window is bounded, so the folder only grows by
    /// what pruning fails to reclaim. RAM buffer modes have their own threshold and are not here.
    /// </summary>
    internal static class DiskBufferSpacePolicy
    {
        // Bytes to leave for finalizing the recording's own file.
        public const long FinalizationMarginBytes = 128L * 1024 * 1024;

        // The save concatenates the window with stream copy, which never produces more than the bytes
        // it reads (measured: a QSV H.264 save holds about a tenth of the window it covers, HEVC
        // nearly all of it), so the 15% mux margin the recording-start preflight uses is the margin
        // here too.
        public const int SaveOutputMarginPercent = 15;

        private const double GrowthMarginFactor = 1.5;

        /// <summary>
        /// Bytes the buffer adds during one monitor interval: the interval's write less what pruning
        /// reclaimed during the last interval, never below zero - the buffer cannot grow backwards.
        /// A save pins the footage it reads and writes its concat output into the folder being guarded,
        /// so reclaim is not credited while one is running.
        /// </summary>
        public static long GrowthBytesPerInterval(in DiskBufferMonitorSample sample)
        {
            long writeBytes = sample.WriteBytesPerSecond * sample.IntervalSeconds;
            if (sample.SaveInFlight)
                return writeBytes;

            long reclaimedBytes = (long)(sample.ReclaimedBytesPerSecond * sample.IntervalSeconds);
            long growth = writeBytes - reclaimedBytes;
            return growth > 0 ? growth : 0;
        }

        /// <summary>
        /// What a save from this window puts on disk: the window's footage at the write rate with the
        /// mux margin. The output is staged inside the segment folder before it is moved to the
        /// library, so it lands on the drive the monitor is guarding.
        /// </summary>
        public static long SaveOutputBytes(in DiskBufferMonitorSample sample)
        {
            long windowBytes = sample.WriteBytesPerSecond * sample.WindowSeconds;
            return windowBytes + windowBytes * SaveOutputMarginPercent / 100;
        }

        /// <summary>
        /// Free space at which the recording is stopped: one interval of what the folder is really
        /// growing by, with headroom for the gap until the monitor next looks, plus the finalization
        /// buffer, plus the file the next save will write into the folder being guarded. Steady state
        /// is not zero headroom: pruning bounds the folder, not the save. Never below the absolute
        /// floor the recorder refuses to write under.
        /// </summary>
        public static long FreeSpaceThresholdBytes(in DiskBufferMonitorSample sample, long floorBytes)
        {
            long growthWithMargin = (long)(GrowthBytesPerInterval(sample) * GrowthMarginFactor);
            long threshold = growthWithMargin + FinalizationMarginBytes + SaveOutputBytes(sample);
            return Math.Max(floorBytes, threshold);
        }
    }
}
