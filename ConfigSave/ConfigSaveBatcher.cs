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
    /// A <c>Reload()</c> of a pending file writes it first (Reload would otherwise re-read the older disk copy),
    /// unless the file changed on disk since this module last saw it (an edit by the user or another process, picked
    /// up by a mod's file watcher): then the reload reads that edit, as it would without deferring, and the file stays
    /// pending. Process exit/domain unload also flush (quit before the menu). After the main menu the module is inert.
    /// </summary>
    internal static class ConfigSaveBatcher
    {
        private static readonly object Lock = new object();
        private static readonly List<ConfigFile> Pending = new List<ConfigFile>();

        /// <summary>Pending file -> its disk stamp when this module last saw it (first deferral or last reload).</summary>
        private static readonly Dictionary<ConfigFile, DiskStamp> Stamps = new Dictionary<ConfigFile, DiskStamp>();

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
                prefix: new HarmonyMethod(AccessTools.Method(typeof(ConfigSaveBatcher), nameof(ReloadPrefix)), Priority.First),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(ConfigSaveBatcher), nameof(ReloadPostfix)), Priority.Last));
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
                if (!Stamps.ContainsKey(__instance))
                {
                    Stamps[__instance] = DiskStamp.Read(__instance.ConfigFilePath);
                    Pending.Add(__instance);
                }
                return false;
            }
        }

        private static void ReloadPrefix(ConfigFile __instance, out DiskStamp? __state)
        {
            __state = null;
            lock (Lock)
            {
                if (!_active || !Stamps.TryGetValue(__instance, out DiskStamp seen))
                {
                    return;
                }
                DiskStamp now = DiskStamp.Read(__instance.ConfigFilePath);
                if (!now.Equals(seen))
                {
                    // Edited outside: Reload reads the edit; the merged state is written at the next flush. The stamp
                    // is moved to the edit only once Reload has read it (postfix): if Reload throws (file still locked
                    // by the editor), a retry must still see the edit as unread instead of overwriting it.
                    __state = now;
                    Log.Info($"ConfigSaveBatcher: {Path.GetFileName(__instance.ConfigFilePath)} changed on disk, reloaded before its deferred save");
                    return;
                }
                Stamps.Remove(__instance);
                Pending.Remove(__instance);
            }
            SaveNow(__instance);
        }

        /// <summary>Runs only when Reload ran and returned normally, i.e. the external edit was read.</summary>
        private static void ReloadPostfix(ConfigFile __instance, DiskStamp? __state, bool __runOriginal)
        {
            if (__state == null || !__runOriginal)
            {
                return;
            }
            lock (Lock)
            {
                if (Stamps.ContainsKey(__instance))
                {
                    Stamps[__instance] = __state.Value;
                }
            }
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
                Stamps.Clear();
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

        /// <summary>Last write time + length of a file (both 0 when it does not exist).</summary>
        private struct DiskStamp : IEquatable<DiskStamp>
        {
            private long _ticks;
            private long _length;

            public static DiskStamp Read(string path)
            {
                var info = new FileInfo(path);
                return info.Exists ? new DiskStamp { _ticks = info.LastWriteTimeUtc.Ticks, _length = info.Length } : default;
            }

            public bool Equals(DiskStamp other) => _ticks == other._ticks && _length == other._length;
        }
    }
}