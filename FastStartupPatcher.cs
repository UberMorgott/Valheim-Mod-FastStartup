using System.Collections.Generic;
using FastStartup.BundleCache;
using FastStartup.ConfigSave;
using FastStartup.Core;
using FastStartup.HarmonyBatch;
using FastStartup.ModHotspots;
using FastStartup.Profiling;
using FastStartup.Translations;
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
                bool hotspots = Config.ShaderReplacerFixEnabled?.Value == true;
                bool hotspotsDump = Config.DumpModHotspots?.Value == true;
                if (bundles || configSave || harmonyBatching || localization || dump || hotspots || hotspotsDump)
                {
                    Lifecycle.Install(new Harmony(LifecycleHarmonyId));
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
                    ModHotspotsDump.Install();
                }
            });
        }
    }
}
