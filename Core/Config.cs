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

        public static ConfigEntry<bool> ProfilerTimeSpawnWindow { get; private set; }

        public static ConfigEntry<bool> ParallelBiomeData { get; private set; }

        public static ConfigEntry<bool> EarlyReject { get; private set; }

        public static ConfigEntry<float> LoadingTimeBudget { get; private set; }

        public static ConfigEntry<bool> DumpLocations { get; private set; }

        public static ConfigEntry<bool> FastLakes { get; private set; }

        public static ConfigEntry<bool> PregenCache { get; private set; }

        public static ConfigEntry<bool> PrefetchBiomeData { get; private set; }

        public static ConfigEntry<bool> PrefetchSectors { get; private set; }

        public static ConfigEntry<bool> ProfilerTimeUnityMessages { get; private set; }

        /// <summary>Any [WorldGen] key that needs the WorldGen hooks.</summary>
        public static bool WorldGenAny => ParallelBiomeData.Value || FastLakes.Value || PregenCache.Value || EarlyReject.Value || LoadingTimeBudget.Value > 0f || DumpLocations.Value;

        public static void Load()
        {
            var file = new ConfigFile(Path.Combine(Paths.ConfigPath, "FastStartup.cfg"), true);
            ProfilerEnabled = file.Bind("Profiler", "Enabled", false,
                "Record a startup trace (process start -> main menu) and write BepInEx\\FastStartup\\trace.json (Chrome trace) " +
                "and summary.txt; recording goes on to the first spawn in a world (trace-world.json, summary-world.txt). Costs about 0.5 s of startup (its Harmony hooks), so it is off for normal play; turn it on to " +
                "measure. Read once at launch.");
            ProfilerTimeModPatches = file.Bind("Profiler", "TimeModPatches", false,
                "Also time every mod prefix/postfix/finalizer on the profiled game methods (FejdStartup.Awake etc.) per owner. " +
                "Diagnostic only: hooking forces Mono to compile those methods early, which crashed the game once on a method " +
                "Mono could not compile that way; such a method is skipped automatically from the next launch on " +
                "(BepInEx\\FastStartup\\patch-probe.skip).");
            BundleCacheEnabled = file.Bind("BundleCache", "Enabled", true,
                "Serve LZMA asset bundles that mods embed in their DLLs from an LZ4 copy cached under " +
                "BepInEx\\FastStartup\\cache\\bundles (game folder). Copies are made in the background after the main menu; " +
                "the first launch loads the originals. Verified prebuilt copies shipped by the modpack in " +
                "BepInEx\\FastStartup\\pack\\bundles are used first. Read once at launch.");
            BundleCacheMaxSizeMB = file.Bind("BundleCache", "MaxCacheSizeMB", 2048,
                new ConfigDescription("Local cache size cap. Least recently used copies beyond it are deleted after the main menu " +
                                      "(copies loaded in the current session are kept; the modpack's copies are never touched).",
                    new AcceptableValueRange<int>(64, 65536)));
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
            ProfilerTimeSpawnWindow = file.Bind("Profiler", "TimeSpawnWindow", false,
                "With [Profiler] Enabled: also time the per-frame world-load methods between the main scene request and the first " +
                "player spawn (zone creation, object creation, terrain readiness, spawn point search, teleport) as count / total / " +
                "max per method (and per mod patch with TimeModPatches). Off after the first spawn. Read once at launch.");
            ParallelBiomeData = file.Bind("WorldGen", "ParallelBiomeData", true,
                "Compute the 2048x2048 biome/height map built on every world load and connect (AltBiomeWorldData." +
                "GenerateBiomePoints) on all CPU cores, same per-cell formula, identical result. Falls back to vanilla while another " +
                "mod patches the world generator methods it calls. Read once at launch.");
            EarlyReject = file.Bind("WorldGen", "EarlyReject", true,
                "New worlds: during location placement, run the cheap later checks of a candidate point (distance to similar " +
                "locations, vegetation, alt-biome) before the 10-sample terrain delta, and skip the samples for a candidate those " +
                "checks reject. Same random sequence and same placed locations as vanilla. Read once at launch.");
            LoadingTimeBudget = file.Bind("WorldGen", "LoadingTimeBudget", 0.25f,
                new ConfigDescription("Seconds of location generation per frame while a new world is generated (vanilla 0.1; the " +
                                      "intro/cinematic budget is left alone). 0 = vanilla. Read once at launch.",
                    new AcceptableValueRange<float>(0f, 2f)));
            DumpLocations = file.Bind("WorldGen", "DumpLocations", false,
                "Diagnostic: when locations are generated (or a world with generated locations loads) write every location " +
                "instance (prefab, zone, position as float bits) and the SHA-256 of the biome/height map to " +
                "BepInEx\\FastStartup\\diag\\locations-<seed>.txt, for diffing WorldGen off against on. Also logs world-gen timings, " +
                "and compares FastLakes against the original lake merge (costs its 1-2 s once per world load).");
            FastLakes = file.Bind("WorldGen", "FastLakes", true,
                "Merge the lake candidates of every world load and connect (WorldGenerator.MergePoints, 1-2 s on the main thread) " +
                "through a spatial grid instead of a full scan per merge step. Same lakes, bit for bit (same list order, distances " +
                "and tie rule). Falls back to vanilla while another mod patches the lake search. Read once at launch.");
            PregenCache = file.Bind("WorldGen", "PregenCache", true,
                "Keep the world generator's river and stream placement of each world (lakes, rivers, streams and the random state " +
                "of their rasterisation, a few hundred KB) in BepInEx\\FastStartup\\cache\\worldgen and rebuild the river data from " +
                "it on the next load / connect instead of searching again. Keyed by game build, Unity version and the generator's " +
                "seed-derived inputs; off while another mod patches the world generator. Read once at launch.");
            PrefetchBiomeData = file.Bind("WorldGen", "PrefetchBiomeData", true,
                "With ParallelBiomeData: start the biome/height map build on worker threads as soon as the world generator exists " +
                "(ZNet.Awake) instead of in ZNet.Start, so it runs while the rest of the main scene loads. Same map. Read once at launch.");
            PrefetchSectors = file.Bind("WorldGen", "PrefetchSectors", true,
                "With PrefetchBiomeData: also build the biome sectors (flood fill of the map into regions, their edges and " +
                "neighbours) on the prefetch worker; the main-thread part (discovered flags, alt biomes) still runs at the vanilla " +
                "moment. Same sectors. Read once at launch.");
            ProfilerTimeUnityMessages = file.Bind("Profiler", "TimeUnityMessages", false,
                "With [Profiler] Enabled: time every Awake/Start of the game's MonoBehaviours during the world load, per method and " +
                "load phase (freeze, scene loaded -> first frame, first frame -> spawn). Adds overhead to every object created " +
                "before the spawn, so the spawn-window numbers are inflated while it is on. Read once at launch.");
        }
    }
}
