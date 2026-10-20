using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using BepInEx;
using FastStartup.BundleCache;
using FastStartup.ConfigSave;
using FastStartup.Core;
using FastStartup.HarmonyBatch;
using FastStartup.ModHotspots;
using FastStartup.Profiling;
using FastStartup.Translations;
using FastStartup.WorldGen;
using HarmonyLib;
using Mono.Cecil;

namespace FastStartup
{
    /// <summary>
    /// BepInEx 5 preloader patcher entry point. Contract (BepInEx.Preloader AssemblyPatcher, decompiled
    /// lines 944-988): public static <c>TargetDLLs</c>, public static <c>Patch(AssemblyDefinition)</c>, optional
    /// <c>Initialize()</c> (after all Managed DLLs are read, before patching) and <c>Finish()</c> (after patched
    /// assemblies are loaded, before the chainloader). No assembly is rewritten: all work is runtime hooks.
    /// </summary>
    public static class FastStartupPatcher
    {
        // Every FastStartup Harmony ID starts with "morgott.faststartup." so its own patches are recognizable.
        public const string LocalizationHarmonyId = "morgott.faststartup.localization";
        private const string LifecycleHarmonyId = "morgott.faststartup.lifecycle";
        private const string BundleCacheHarmonyId = "morgott.faststartup.bundlecache";
        private const string ConfigSaveHarmonyId = "morgott.faststartup.configsave";
        private const string HarmonyBatchHarmonyId = "morgott.faststartup.harmonybatch";
        private const string ModHotspotsHarmonyId = "morgott.faststartup.modhotspots";
        public const string WorldGenHarmonyId = "morgott.faststartup.worldgen";

        public static IEnumerable<string> TargetDLLs { get; } = new string[0];

        public static void Patch(AssemblyDefinition assembly)
        {
        }

        public static void Initialize()
        {
            Log.Guard("Initialize", () =>
            {
                Config.Load();
                if (Config.ProfilerEnabled.Value)
                {
                    Profiler.Begin();
                }
                if (Config.BundleCacheEnabled.Value && Config.BundleCacheEagerFirstBuild.Value)
                {
                    // Before Unity reports Application.unityVersion: UnityPlayer.dll's product version carries the same
                    // string ("6000.0.75f1 (26349cd2a5c8)"); BundleCacheModule checks the guess once Unity is up.
                    string unity = FileVersionInfo.GetVersionInfo(Path.Combine(Paths.GameRootPath, "UnityPlayer.dll")).ProductVersion?.Split(' ')[0];
                    if (!string.IsNullOrEmpty(unity))
                    {
                        EagerBuilder.Start(CacheStore.DirFor(BundleCacheModule.CacheRoot, unity));
                    }
                }
            });
        }

        public static void Finish()
        {
            Log.Guard("Finish", () =>
            {
                if (Config.ProfilerEnabled?.Value == true)
                {
                    Profiler.InstallRuntimeHooks();
                }
                bool bundles = Config.BundleCacheEnabled?.Value == true;
                bool configSave = Config.ConfigSaveBatcherEnabled?.Value == true;
                bool harmonyBatching = Config.HarmonyBatchingEnabled?.Value == true;
                bool localization = Config.LocalizationCacheEnabled?.Value == true;
                bool dump = Config.DumpHarmonyState?.Value == true;
                bool hotspots = Config.ShaderReplacerFixEnabled?.Value == true || Config.VneiIndexingEnabled?.Value == true;
                bool hotspotsDump = Config.DumpModHotspots?.Value == true;
                bool worldGen = Config.ParallelBiomeData != null && Config.WorldGenAny;
                bool spikes = Config.SpikeProbe?.Value == true;
                if (bundles || configSave || harmonyBatching || localization || dump || hotspots || hotspotsDump || worldGen || spikes)
                {
                    Lifecycle.Install(new Harmony(LifecycleHarmonyId));
                }
                if (spikes)
                {
                    // Game and Unity types: only once the engine is up.
                    Lifecycle.ChainloaderInitialized += () =>
                        Log.Guard("SpikeProbe install", () => SpikeProbe.Install(new Harmony(SpikeProbe.HarmonyId)));
                }
                if (configSave)
                {
                    Log.Guard("ConfigSaveBatcher install", () => ConfigSaveBatcher.Install(new Harmony(ConfigSaveHarmonyId)));
                }
                if (harmonyBatching)
                {
                    Log.Guard("HarmonyBatching install", () => HarmonyBatcher.Install(new Harmony(HarmonyBatchHarmonyId)));
                }
                if (bundles)
                {
                    Log.Guard("BundleCache install", () => BundleCacheModule.Install(new Harmony(BundleCacheHarmonyId)));
                }
                if (localization)
                {
                    // Touches assembly_guiutils types: only once the engine is up.
                    Lifecycle.ChainloaderInitialized += () =>
                        Log.Guard("LocalizationCache install", () => LocalizationCache.Install(new Harmony(LocalizationHarmonyId)));
                }
                if (dump)
                {
                    HarmonyStateDump.Install();
                }
                if (hotspots)
                {
                    ModHotspotsModule.Install(new Harmony(ModHotspotsHarmonyId));
                }
                if (hotspotsDump)
                {
                    ModHotspotsDump.Install(new Harmony(ModHotspotsHarmonyId));
                }
                if (worldGen)
                {
                    // Game types: only once the engine is up.
                    bool profiler = Config.ProfilerEnabled?.Value == true;
                    Lifecycle.ChainloaderInitialized += () =>
                        Log.Guard("WorldGen install", () => WorldGenModule.Install(new Harmony(WorldGenHarmonyId), profiler));
                }
            });
        }
    }
}
