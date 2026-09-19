using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using BepInEx;
using FastStartup.Core;
using HarmonyLib;
using UnityEngine;

namespace FastStartup.BundleCache
{
    /// <summary>
    /// Serves LZMA bundles that mods embed as manifest resources from an LZ4 copy on disk. The profile shows every
    /// mod bundle arriving through <c>AssetBundle.LoadFromStream</c> on a resource stream (31 loads, 18.8 s, all
    /// LZMA); the vanilla SoftRef file loads are already LZ4 and file/memory loads by mods do not occur, so only
    /// the two internal stream statics every public overload funnels into are hooked
    /// (UnityEngine.AssetBundleModule decompile lines 482/489).
    /// Hit: <c>AssetBundle.LoadFromFile(Async)</c> of the copy (LZ4 is read chunk-wise on demand instead of
    /// decompressing the whole LZMA stream). Miss: the original load runs untouched; the source is queued and,
    /// after the main menu, copied off the main thread and recompressed with
    /// <c>AssetBundle.RecompressAssetBundleAsync(.., BuildCompression.LZ4Runtime, .., ThreadPriority.Low)</c>
    /// (AssetBundleModule line 925, CoreModule BuildCompression), one bundle at a time. Any failure = original load.
    /// </summary>
    internal static class BundleCacheModule
    {
        private const string LogKey = "BundleCache.";

        private static readonly List<ResourceSource> Pending = new List<ResourceSource>();
        private static readonly HashSet<string> Queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static Harmony _harmony;
        private static CacheStore _store;
        private static volatile bool _active;
        private static SynchronizationContext _main;
        private static HashSet<Guid> _loadedMvids;
        private static bool _menuReady;
        private static bool _running;
        private static bool _maintained;
        private static readonly Stopwatch PopulateTime = new Stopwatch();

        private static int _hits;
        private static long _hitBytes;
        private static int _misses;
        private static int _alreadyFast;
        private static int _passedThrough;
        private static int _created;
        private static long _createdBytes;

        public static void Install(Harmony harmony)
        {
            _harmony = harmony;
            Lifecycle.ChainloaderInitialized += () => Log.Guard("BundleCache install", Activate);
            Lifecycle.MenuReady += () => Log.Guard("BundleCache menu ready", OnMenuReady);
        }

        private static void Activate()
        {
            if (!ResourceResolver.Available)
            {
                Log.Warning("BundleCache: Mono resource stream internals not found (runtime changed?), cache disabled");
                return;
            }
            // Everything FastStartup writes stays under BepInEx\FastStartup in the game folder (owner rule: no caches
            // in AppData/LocalLow/%TEMP%), so deleting that folder removes it all.
            _store = new CacheStore(Path.GetFullPath(Path.Combine(Paths.BepInExRootPath, Path.Combine("FastStartup", Path.Combine("cache", "bundles")))),
                Application.unityVersion);
            Hook("LoadFromStreamInternal", nameof(StreamPrefix));
            Hook("LoadFromStreamAsyncInternal", nameof(StreamAsyncPrefix));
            _active = true;
            Log.Info($"BundleCache: active, cache {_store.Dir}");
        }

        private static void Hook(string target, string prefix)
        {
            MethodInfo original = AccessTools.DeclaredMethod(typeof(AssetBundle), target);
            if (original == null)
            {
                Log.Warning($"BundleCache: AssetBundle.{target} not found (Unity changed?), not cached");
                return;
            }
            _harmony.Patch(original, prefix: new HarmonyMethod(AccessTools.Method(typeof(BundleCacheModule), prefix)));
        }

        private static bool StreamPrefix(Stream stream, uint crc, ref AssetBundle __result, bool __runOriginal)
        {
            if (!__runOriginal)
            {
                return false; // an earlier prefix already produced the result
            }
            string cached = Lookup(stream, crc);
            if (cached == null)
            {
                return true;
            }
            AssetBundle bundle = AssetBundle.LoadFromFile(cached);
            if (bundle == null)
            {
                Log.WarningOnce(LogKey + "hitFailed." + cached,
                    $"BundleCache: cached copy {Path.GetFileName(cached)} did not load, using the original stream");
                return true;
            }
            __result = bundle;
            return false;
        }

        private static bool StreamAsyncPrefix(Stream stream, uint crc, ref AssetBundleCreateRequest __result, bool __runOriginal)
        {
            if (!__runOriginal)
            {
                return false;
            }
            string cached = Lookup(stream, crc);
            if (cached == null)
            {
                return true;
            }
            AssetBundleCreateRequest request = AssetBundle.LoadFromFileAsync(cached);
            if (request == null)
            {
                return true;
            }
            __result = request;
            return false;
        }

        /// <summary>Cached copy path on a hit; null = load the original (and queue it when it is LZMA).</summary>
        private static string Lookup(Stream stream, uint crc)
        {
            if (!_active || crc != 0)
            {
                return null; // a CRC check is the caller's integrity contract on the original bytes: leave it alone
            }
            try
            {
                ResourceSource source = ResourceResolver.Resolve(stream);
                if (source == null)
                {
                    _passedThrough++;
                    Log.InfoOnce(LogKey + "type." + stream?.GetType().FullName,
                        $"BundleCache: {stream?.GetType().FullName ?? "null"} stream is not an embedded resource, loaded uncached");
                    return null;
                }
                string path = _store.PathOf(source);
                if (File.Exists(path))
                {
                    _store.MarkUsed(source.FileName);
                    _hits++;
                    _hitBytes += source.Length;
                    return path;
                }
                switch (BundleHeader.Classify(stream))
                {
                    case BundleCompression.Lzma:
                        _misses++;
                        Enqueue(source);
                        break;
                    case BundleCompression.NotLzma:
                        _alreadyFast++;
                        break;
                    default:
                        _passedThrough++;
                        Log.InfoOnce(LogKey + "unknown." + source.Label, $"BundleCache: {source.Label} is not a readable UnityFS bundle, loaded uncached");
                        break;
                }
            }
            catch (Exception e)
            {
                Log.WarningOnce(LogKey + "lookup", $"BundleCache: lookup failed, loading originals: {e}");
            }
            return null;
        }

        private static void Enqueue(ResourceSource source)
        {
            lock (Pending)
            {
                if (!Queued.Add(source.FileName))
                {
                    return;
                }
                Pending.Add(source);
            }
            if (_menuReady && !_running && SynchronizationContext.Current == _main)
            {
                Next();
            }
        }

        private static void OnMenuReady()
        {
            if (!_active)
            {
                return;
            }
            Log.Info($"BundleCache: {_hits} hits ({_hitBytes / (1024.0 * 1024.0):F1} MB served from LZ4 copies), {_misses} LZMA misses queued, " +
                     $"{_alreadyFast} already LZ4/uncompressed, {_passedThrough} not cacheable");
            _main = SynchronizationContext.Current;
            if (_main == null)
            {
                Log.Warning("BundleCache: no Unity synchronization context on the main thread, cache not populated");
                return;
            }
            _loadedMvids = LoadedMvids();
            _menuReady = true;
            Next();
        }

        private static HashSet<Guid> LoadedMvids()
        {
            var mvids = new HashSet<Guid>();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (!assembly.IsDynamic)
                    {
                        foreach (Module module in assembly.GetModules())
                        {
                            mvids.Add(module.ModuleVersionId);
                        }
                    }
                }
                catch (Exception e) when (e is NotSupportedException || e is NotImplementedException)
                {
                    // Such an assembly cannot own a cached resource either.
                }
            }
            return mvids;
        }

        /// <summary>Main thread: starts the next queued copy, or runs maintenance once the queue is empty.</summary>
        private static void Next()
        {
            ResourceSource source;
            lock (Pending)
            {
                source = Pending.Count > 0 ? Pending[0] : null;
                if (source != null)
                {
                    Pending.RemoveAt(0);
                }
            }
            if (source == null)
            {
                _running = false;
                if (PopulateTime.IsRunning)
                {
                    PopulateTime.Stop();
                    Log.Info($"BundleCache: cached {_created} bundles ({_createdBytes / (1024.0 * 1024.0):F1} MB LZ4) in {PopulateTime.Elapsed.TotalSeconds:F1} s");
                }
                if (!_maintained)
                {
                    _maintained = true;
                    ThreadPool.QueueUserWorkItem(_ => Log.Guard("BundleCache maintenance", Maintain));
                }
                return;
            }
            _running = true;
            PopulateTime.Start();
            string input = _store.TempPath(source, "src");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool copied = CopyResource(source, input);
                _main.Post(__ => Log.Guard("BundleCache recompress", () => Recompress(source, input, copied)), null);
            });
        }

        /// <summary>Worker thread: RecompressAssetBundleAsync takes a file path, so the resource is copied out first.</summary>
        private static bool CopyResource(ResourceSource source, string input)
        {
            try
            {
                using (Stream resource = source.Assembly.GetManifestResourceStream(source.ResourceName))
                using (var file = new FileStream(input, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    resource.CopyTo(file);
                }
                return true;
            }
            catch (Exception e)
            {
                Log.WarningOnce(LogKey + "copy." + source.Label, $"BundleCache: could not extract {source.Label}: {e.Message}");
                TryDelete(input);
                return false;
            }
        }

        /// <summary>Main thread (Unity API).</summary>
        private static void Recompress(ResourceSource source, string input, bool copied)
        {
            if (!copied)
            {
                Next();
                return;
            }
            string output = _store.TempPath(source, "out");
            AssetBundleRecompressOperation op;
            try
            {
                op = AssetBundle.RecompressAssetBundleAsync(input, output, BuildCompression.LZ4Runtime, 0u, UnityEngine.ThreadPriority.Low);
            }
            catch (Exception e)
            {
                Log.WarningOnce(LogKey + "recompress." + source.Label, $"BundleCache: recompress of {source.Label} failed to start: {e.Message}");
                op = null;
            }
            if (op == null)
            {
                TryDelete(input);
                TryDelete(output);
                Next();
                return;
            }
            op.completed += _ =>
            {
                bool success = op.success;
                string result = op.humanReadableResult;
                ThreadPool.QueueUserWorkItem(__ =>
                {
                    Log.Guard("BundleCache store", () => Store(source, input, output, success, result));
                    _main.Post(___ => Log.Guard("BundleCache next", Next), null);
                });
            };
        }

        /// <summary>Worker thread: validate the LZ4 output and rename it into place, then persist the index.</summary>
        private static void Store(ResourceSource source, string input, string output, bool success, string result)
        {
            TryDelete(input);
            if (!success || !File.Exists(output) || BundleHeader.Classify(output) != BundleCompression.NotLzma)
            {
                Log.WarningOnce(LogKey + "store." + source.Label, $"BundleCache: recompress of {source.Label} failed ({result}), it stays uncached");
                TryDelete(output);
                return;
            }
            string target = _store.PathOf(source);
            long size = new FileInfo(output).Length;
            try
            {
                File.Move(output, target);
            }
            catch (IOException) when (File.Exists(target))
            {
                TryDelete(output); // another process stored the same copy first
            }
            _store.Add(source.FileName, source.Label);
            _created++;
            _createdBytes += size;
        }

        private static void Maintain()
        {
            long cap = Config.BundleCacheMaxSizeMB.Value * 1024L * 1024L;
            long size = _store.Maintain(_loadedMvids, cap, out int deleted);
            Log.Info($"BundleCache: {size / (1024.0 * 1024.0):F1} MB on disk (cap {Config.BundleCacheMaxSizeMB.Value} MB), {deleted} stale files removed");
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // A leftover temp is removed by the next maintenance pass.
            }
        }
    }
}
