using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using FastStartup.Core;
using HarmonyLib;

namespace FastStartup.ConfigSave
{
    /// <summary>
    /// BepInEx 5.4.23 <c>ConfigFile.Bind</c> and every value change call <c>Save()</c> while <c>SaveOnConfigSet</c> is
    /// true, and <c>Save()</c> rewrites the whole file: ~1130 writes during startup here (~730 while plugins load, ~400
    /// more from AdventureBackpacks while the menu scene builds). From <c>Chainloader.Start</c> until the main menu is ready,
    /// <c>Save()</c> only records the file (once); every recorded file is written once at the end of
    /// <c>Chainloader.Start</c> (finalizer: also when it throws) and again at the main menu, where deferring stops.
    /// Nothing else changes: <c>SaveOnConfigSet</c> is never touched, so mods read the value they set.
    /// Disk stays authoritative for readers inside BepInEx: a <c>Reload()</c> of a pending file writes it first
    /// (Reload would otherwise re-read the older disk copy). Process exit/domain unload also flush (quit before the
    /// menu). After the main menu the module is inert.
    /// </summary>
    internal static class ConfigSaveBatcher
    {
        private static readonly object Lock = new object();
        private static readonly List<ConfigFile> Pending = new List<ConfigFile>();
        private static readonly HashSet<ConfigFile> PendingSet = new HashSet<ConfigFile>();
        private static bool _active;
        private static int _deferred;

        /// <summary>Set while this module itself writes a file, so its own Save call is not deferred.</summary>
        [ThreadStatic] private static bool _bypass;

        public static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Chainloader), nameof(Chainloader.Start)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(ConfigSaveBatcher), nameof(StartPrefix)), Priority.First),
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(ConfigSaveBatcher), nameof(StartFinalizer)), Priority.Last));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(ConfigFile), nameof(ConfigFile.Save)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(ConfigSaveBatcher), nameof(SavePrefix)), Priority.Last));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(ConfigFile), nameof(ConfigFile.Reload)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(ConfigSaveBatcher), nameof(ReloadPrefix)), Priority.First));
            Lifecycle.MenuReady += () => Flush("main menu", stop: true);
            AppDomain.CurrentDomain.ProcessExit += (_, __) => Flush("process exit", stop: true);
            AppDomain.CurrentDomain.DomainUnload += (_, __) => Flush("domain unload", stop: true);
        }

        private static void StartPrefix()
        {
            lock (Lock)
            {
                _active = true;
            }
        }

        private static void StartFinalizer() => Flush("chainloader end", stop: false);

        private static bool SavePrefix(ConfigFile __instance, bool __runOriginal)
        {
            if (!__runOriginal || _bypass)
            {
                return __runOriginal;
            }
            lock (Lock)
            {
                if (!_active)
                {
                    return true;
                }
                _deferred++;
                if (PendingSet.Add(__instance))
                {
                    Pending.Add(__instance);
                }
                return false;
            }
        }

        private static void ReloadPrefix(ConfigFile __instance)
        {
            lock (Lock)
            {
                if (!_active || !PendingSet.Remove(__instance))
                {
                    return;
                }
                Pending.Remove(__instance);
            }
            SaveNow(__instance);
        }

        /// <summary>Writes every pending file once (in first-save order); with <paramref name="stop"/> deferring ends.</summary>
        private static void Flush(string reason, bool stop)
        {
            ConfigFile[] files;
            int deferred;
            lock (Lock)
            {
                if (!_active)
                {
                    return;
                }
                _active = !stop;
                files = Pending.ToArray();
                deferred = _deferred;
                _deferred = 0;
                Pending.Clear();
                PendingSet.Clear();
            }
            Stopwatch watch = Stopwatch.StartNew();
            int failed = 0;
            foreach (ConfigFile file in files)
            {
                if (!SaveNow(file))
                {
                    failed++;
                }
            }
            Log.Info($"ConfigSaveBatcher: {deferred} saves deferred -> {files.Length} file writes at {reason} " +
                     $"in {watch.ElapsedMilliseconds} ms" + (failed > 0 ? $", {failed} failed" : ""));
        }

        /// <summary>Runs the real Save now. A failure is logged here: the plugin whose Bind triggered it has moved on.</summary>
        private static bool SaveNow(ConfigFile file)
        {
            try
            {
                _bypass = true;
                file.Save();
                return true;
            }
            catch (Exception e)
            {
                Log.Error($"ConfigSaveBatcher: writing {Path.GetFileName(file.ConfigFilePath)} failed: {e}");
                return false;
            }
            finally
            {
                _bypass = false;
            }
        }
    }
}
