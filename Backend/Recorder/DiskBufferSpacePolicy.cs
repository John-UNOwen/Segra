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
    }
}
