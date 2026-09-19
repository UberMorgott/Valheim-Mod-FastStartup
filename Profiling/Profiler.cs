using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using BepInEx;
using FastStartup.Core;
using HarmonyLib;
using UnityEngine;

namespace FastStartup.Profiling
{
    /// <summary>
    /// Wires the profiling modules. Phases: <see cref="Begin"/> from the patcher's Initialize (no Unity types
    /// touched), <see cref="InstallRuntimeHooks"/> from Finish (pure managed hooks only: Harmony + Chainloader),
    /// Unity/game hooks from the Chainloader.Initialize postfix once the engine is up. Output on main-menu ready: BepInEx\FastStartup\trace.json + summary.txt.
    /// </summary>
    internal static class Profiler
    {
        public const string HarmonyId = "morgott.faststartup.profiler";

        private static Harmony _harmony;
        private static long _initialized;
        private static int _exported;

        private static string OutputDir => Path.Combine(Paths.BepInExRootPath, "FastStartup");

        public static void Begin()
        {
            StartupTrace.Start();
            _initialized = StartupTrace.Now();
        }

        public static void InstallRuntimeHooks()
        {
            StartupTrace.Complete("lifecycle", "BepInEx preloader: patchers + load patched assemblies", _initialized, StartupTrace.Now());
            StartupTrace.Mark("patcher.Finish");
            _harmony = new Harmony(HarmonyId);
            Log.Guard("Harmony profiler install", () => HarmonyProfiler.Install(_harmony));
            Log.Guard("Plugin profiler install", () => PluginLifecycleProfiler.Install(_harmony));
            PluginLifecycleProfiler.ChainloaderInitialized += OnChainloaderInitialized;
            PluginLifecycleProfiler.ChainloaderStarted += () => Log.Guard("Jotunn probe install", () => JotunnProbe.Install(_harmony));
            Log.Info("Profiler: preloader hooks installed");
        }

        /// <summary>Unity types may only be touched from here on: their static constructors call engine internals
        /// that do not exist yet during the preloader (a TypeInitializationException there breaks the game).</summary>
        private static void OnChainloaderInitialized()
        {
            Log.Guard("Bundle profiler install", () => BundleProfiler.Install(_harmony));
            Log.Guard("Game probe install", () => GameLifecycleProbe.Install(_harmony));
            GameLifecycleProbe.MenuReady += OnMenuReady;
            Application.quitting += OnQuitting;
        }

        private static void OnMenuReady()
        {
            long now = StartupTrace.Now();
            List<TraceEvent> events = StartupTrace.StopAndSnapshot();
            double menuReadyMs = StartupTrace.ToMs(now);
            Log.Info(TraceReport.OneLine(events, menuReadyMs));
            // File output off the main thread; the trace is already frozen.
            ThreadPool.QueueUserWorkItem(_ => Export(events, menuReadyMs, "main menu ready"));
        }

        /// <summary>Quit before the menu (crash-free exit mid-startup): write what was recorded, synchronously.</summary>
        private static void OnQuitting()
        {
            if (!StartupTrace.Recording)
            {
                return;
            }
            long now = StartupTrace.Now();
            Export(StartupTrace.StopAndSnapshot(), StartupTrace.ToMs(now), "quit before main menu (partial trace)");
        }

        private static void Export(List<TraceEvent> events, double endMs, string reason)
        {
            if (Interlocked.Exchange(ref _exported, 1) != 0)
            {
                return;
            }
            Log.Guard("Trace export", () =>
            {
                string header = $"FastStartup startup profile - {DateTime.Now:yyyy-MM-dd HH:mm:ss} - {reason} - {events.Count} events";
                AtomicFile.WriteAllText(Path.Combine(OutputDir, "trace.json"), TraceReport.ChromeTrace(events));
                AtomicFile.WriteAllText(Path.Combine(OutputDir, "summary.txt"), TraceReport.Summary(events, endMs, header));
                Log.Info($"Profile written to {OutputDir} (trace.json for chrome://tracing / ui.perfetto.dev, summary.txt)");
            });
        }
    }
}
