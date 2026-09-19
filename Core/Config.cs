using System.IO;
using BepInEx;
using BepInEx.Configuration;

namespace FastStartup.Core
{
    /// <summary>BepInEx\config\FastStartup.cfg. A patcher has no BaseUnityPlugin.Config, so the file is opened directly.</summary>
    internal static class Config
    {
        public static ConfigEntry<bool> ProfilerEnabled { get; private set; }

        public static ConfigEntry<bool> ProfilerTimeModPatches { get; private set; }

        public static ConfigEntry<bool> BundleCacheEnabled { get; private set; }

        public static ConfigEntry<int> BundleCacheMaxSizeMB { get; private set; }

        public static void Load()
        {
            var file = new ConfigFile(Path.Combine(Paths.ConfigPath, "FastStartup.cfg"), true);
            ProfilerEnabled = file.Bind("Profiler", "Enabled", true,
                "Record a startup trace (process start -> main menu) and write BepInEx\\FastStartup\\trace.json (Chrome trace) " +
                "and summary.txt. Read once at launch.");
            ProfilerTimeModPatches = file.Bind("Profiler", "TimeModPatches", false,
                "Also time every mod prefix/postfix/finalizer on the profiled game methods (FejdStartup.Awake etc.) per owner. " +
                "Diagnostic only: hooking forces Mono to compile those methods early, which crashed the game once on a method " +
                "Mono could not compile that way; such a method is skipped automatically from the next launch on " +
                "(BepInEx\\FastStartup\\patch-probe.skip).");
            BundleCacheEnabled = file.Bind("BundleCache", "Enabled", true,
                "Serve LZMA asset bundles that mods embed in their DLLs from an LZ4 copy cached under " +
                "BepInEx\\FastStartup\\cache\\bundles (game folder). Copies are made in the background after the main menu; " +
                "the first launch loads the originals. Read once at launch.");
            BundleCacheMaxSizeMB = file.Bind("BundleCache", "MaxCacheSizeMB", 2048,
                new ConfigDescription("Cache size cap. Least recently used copies beyond it are deleted after the main menu " +
                                      "(copies used in the current session are kept).", new AcceptableValueRange<int>(64, 65536)));
        }
    }
}
