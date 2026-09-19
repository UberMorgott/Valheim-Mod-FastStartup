using System.Collections.Generic;
using FastStartup.BundleCache;
using FastStartup.Core;
using FastStartup.Profiling;
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
        private const string BundleCacheHarmonyId = "morgott.faststartup.bundlecache";

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
                if (Config.BundleCacheEnabled?.Value == true)
                {
                    var harmony = new Harmony(BundleCacheHarmonyId);
                    Lifecycle.Install(harmony);
                    BundleCacheModule.Install(harmony);
                }
            });
        }
    }
}
