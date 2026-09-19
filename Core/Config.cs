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

        public static ConfigEntry<bool> ConfigSaveBatcherEnabled { get; private set; }

        public static ConfigEntry<bool> LocalizationCacheEnabled { get; private set; }

        public static ConfigEntry<bool> HarmonyBatchingEnabled { get; private set; }

        public static ConfigEntry<bool> DumpHarmonyState { get; private set; }

        public static ConfigEntry<bool> ShaderReplacerFixEnabled { get; private set; }

        public static ConfigEntry<bool> DumpModHotspots { get; private set; }

        public static void Load()
        {
            var file = new ConfigFile(Path.Combine(Paths.ConfigPath, "FastStartup.cfg"), true);
            ProfilerEnabled = file.Bind("Profiler", "Enabled", false,
                "Record a startup trace (process start -> main menu) and write BepInEx\\FastStartup\\trace.json (Chrome trace) " +
                "and summary.txt. Costs about 0.5 s of startup (its Harmony hooks), so it is off for normal play; turn it on to " +
                "measure. Read once at launch.");
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
            ConfigSaveBatcherEnabled = file.Bind("ConfigSaveBatcher", "Enabled", true,
                "While the chainloader loads plugins, write each changed .cfg once at the end instead of on every Bind/value " +
                "change (BepInEx rewrites the whole file each time). Also flushed before a pending file is reloaded and on " +
                "process exit. Read once at launch.");
            LocalizationCacheEnabled = file.Bind("LocalizationCache", "Enabled", true,
                "Parse each vanilla localization CSV once per language and replay its rows on later loads (in memory, this " +
                "session only). Other mods' localization patches still run; switching language rebuilds the table exactly as " +
                "vanilla. Read once at launch.");
            HarmonyBatchingEnabled = file.Bind("HarmonyBatching", "Enabled", true,
                "Inside one Harmony.PatchAll(assembly) call, build each patched method's wrapper once at the end of that " +
                "call instead of once per patch. Patch registration is unchanged. Read once at launch.");
            DumpHarmonyState = file.Bind("Diagnostics", "DumpHarmonyState", false,
                "At the main menu write every Harmony-patched method with its prefix/postfix/transpiler/finalizer owners to " +
                "BepInEx\\FastStartup\\harmony-state.txt (FastStartup's own patches excluded), for diffing two setups.");
            ShaderReplacerFixEnabled = file.Bind("ModHotspots", "ShaderReplacer", true,
                "Run the ShaderReplacer helper embedded in blacks7ar mods (OreMines) with one shader lookup instead of one " +
                "per material. Same shader assignments in the same order; only the known slow version of the helper is " +
                "replaced (matched by its IL). Read once at launch.");
            DumpModHotspots = file.Bind("Diagnostics", "DumpModHotspots", false,
                "At the main menu write the state the ModHotspots replacements produce (e.g. every ShaderReplacer material " +
                "and its shader) to BepInEx\\FastStartup\\modhotspots-state.txt, for diffing a toggle off against on.");
        }
    }
}
