namespace Segra.Backend.App
{
    /// <summary>
    /// What kind of build is running. Kept separate from UpdateService so the answer can be read
    /// without constructing Velopack's UpdateManager, which needs a running app's locator.
    /// </summary>
    internal static class BuildProvenance
    {
        // True only for an install that came from the upstream release feed. A locally built app or
        // this fork has nothing to update from: fetching a build over the running one would drop the
        // fork's own work, so no update path runs at all.
#if LOCAL_BUILD || NO_UPSTREAM_AUTO_UPDATE
        public static bool UpdateFeedIsUsable { get; } = false;
#else
        public static bool UpdateFeedIsUsable { get; } = true;
#endif
    }
}
