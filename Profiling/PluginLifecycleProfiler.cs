using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using FastStartup.Core;
using HarmonyLib;

namespace FastStartup.Profiling
{
    /// <summary>
    /// BepInEx chainloader lifecycle + per-plugin load cost. <c>Chainloader.Start</c> logs
    /// <c>Loading [{PluginInfo}]</c> right before <c>Assembly.LoadFile</c> + <c>ManagerObject.AddComponent(type)</c>
    /// (BepInEx.dll decompile, Chainloader.Start lines 6243-6249); AddComponent runs the plugin's static constructor
    /// and <c>Awake</c> synchronously. A log listener (BepInEx dispatches log events synchronously,
    /// <c>SendLogEvent</c> line 2778) turns those lines into spans: one plugin = its Loading line to the next one
    /// (or chainloader end), keyed by GUID. No Unity method is patched, so no inlining or early-cctor risk.
    /// </summary>
    internal sealed class PluginLifecycleProfiler : ILogListener
    {
        private const string LoadingPrefix = "Loading [";
        private const string CompleteMessage = "Chainloader startup complete"; // Chainloader.Start line 6280

        private static readonly PluginLifecycleProfiler Instance = new PluginLifecycleProfiler();
        private static readonly List<TraceEvent> Pending = new List<TraceEvent>();
        private static Harmony _harmony;
        private static long _currentStart;
        private static string _currentName;

        /// <summary>Raised at the end of Chainloader.Initialize: Unity is up, game assemblies are resolvable.</summary>
        public static event Action ChainloaderInitialized;

        /// <summary>Raised after Chainloader.Start (all plugins loaded and initialized).</summary>
        public static event Action ChainloaderStarted;

        /// <summary>Patcher Finish: only Chainloader.Initialize (BepInEx type, no Unity type initializers run).</summary>
        public static void Install(Harmony harmony)
        {
            _harmony = harmony;
            harmony.Patch(AccessTools.Method(typeof(Chainloader), nameof(Chainloader.Initialize)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(PluginLifecycleProfiler), nameof(InitPrefix)), Priority.First),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(PluginLifecycleProfiler), nameof(InitPostfix)), Priority.Last));
        }

        private static void InitPrefix(out long __state)
        {
            __state = StartupTrace.Now();
            StartupTrace.Mark("Chainloader.Initialize begin");
        }

        private static void InitPostfix(long __state)
        {
            StartupTrace.Complete("lifecycle", "Chainloader.Initialize", __state, StartupTrace.Now());
            StartupTrace.Mark("Chainloader.Initialize end");
            Log.Guard("Chainloader.Start hook", () => _harmony.Patch(AccessTools.Method(typeof(Chainloader), nameof(Chainloader.Start)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(PluginLifecycleProfiler), nameof(StartPrefix)), Priority.First),
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(PluginLifecycleProfiler), nameof(StartFinalizer)), Priority.Last)));
            Log.Guard("ChainloaderInitialized handlers", () => ChainloaderInitialized?.Invoke());
        }

        private static void StartPrefix(out long __state)
        {
            StartupTrace.Mark("Chainloader.Start begin");
            Log.Guard("Plugin profiler attach", () => Logger.Listeners.Add(Instance));
            __state = StartupTrace.Now();
        }

        // Finalizer: runs after every postfix (incl. StartupAccelerator's deferred Harmony flush) and even on throw.
        private static void StartFinalizer(long __state)
        {
            long end = StartupTrace.Now();
            Log.Guard("Plugin profiler detach", () =>
            {
                Logger.Listeners.Remove(Instance);
                ClosePlugin(end);
                var guids = new Dictionary<string, string>();
                foreach (KeyValuePair<string, PluginInfo> kv in Chainloader.PluginInfos)
                {
                    guids[kv.Value.ToString()] = kv.Key;
                }
                foreach (TraceEvent e in Pending)
                {
                    if (guids.TryGetValue(e.Detail, out string guid))
                    {
                        e.Name = guid;
                    }
                    StartupTrace.Complete(e.Cat, e.Name, e.Start, e.End, e.Detail, e.Detail2);
                }
                Pending.Clear();
                StartupTrace.Complete("lifecycle", "Chainloader.Start", __state, end, $"{Chainloader.PluginInfos.Count} plugins");
            });
            StartupTrace.Mark("Chainloader.Start end");
            Log.Guard("ChainloaderStarted handlers", () => ChainloaderStarted?.Invoke());
        }

        private static void ClosePlugin(long end)
        {
            if (_currentName != null)
            {
                Pending.Add(new TraceEvent
                {
                    Cat = "plugin",
                    Name = _currentName,
                    Start = _currentStart,
                    End = end,
                    Detail = _currentName,
                    Detail2 = "assembly load + static ctor + Awake",
                });
                _currentName = null;
            }
        }

        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            if (eventArgs.Source?.SourceName != "BepInEx" || !(eventArgs.Data is string text))
            {
                return;
            }
            if (text == CompleteMessage)
            {
                // Logged before Chainloader.Start postfixes run: keeps a deferred Harmony flush out of the last plugin.
                ClosePlugin(StartupTrace.Now());
                return;
            }
            if (!text.StartsWith(LoadingPrefix, StringComparison.Ordinal) || !text.EndsWith("]", StringComparison.Ordinal))
            {
                return;
            }
            long now = StartupTrace.Now();
            ClosePlugin(now);
            _currentName = text.Substring(LoadingPrefix.Length, text.Length - LoadingPrefix.Length - 1);
            _currentStart = now;
        }

        public void Dispose()
        {
        }
    }
}
