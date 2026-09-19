# FastStartup

BepInEx 5 preloader patcher for Valheim startup speed. Personal build, not published.

Modules: **startup profiler**, **bundle cache** (replaces Fast AssetBundle Loader), **config save batcher**,
**localization cache** and **Harmony batching** (these three replace StartupAccelerator and LocalizationCache).
Design notes: `E:\DEV\Valheim\docs\designs\startup-accel-analysis.md`.

ValheimPlus overlap: none. ValheimPlus has no startup-speed, bundle-cache or profiling feature.

Everything FastStartup writes to disk lives under `Valheim\BepInEx\FastStartup\` (profile, bundle cache, Harmony
state dump); nothing goes to AppData, LocalLow or %TEMP%. Deleting that folder resets it all. The localization
cache is in memory only.

## Results

Menu ready (end of the first `FejdStartup.Start`), 31 plugins, warm = bundle cache filled:

| setup | cold | warm |
| --- | --- | --- |
| no accelerators (2026-09-19 morning) | - | 41 s |
| FastStartup, all modules, profiler off | - | 18.6 / 18.8 / 19.1 s |
| FastStartup, all modules, profiler on | 38.2 s (cache built after the menu) | 20.0 / 19.2 / 19.4 s |
| FastStartup bundle cache + StartupAccelerator + LocalizationCache (before) | 43.7 s | 21-23 s |

Per module (warm, profiler sub-timings, same session, other load on the machine):

| module | before | after |
| --- | --- | --- |
| BundleCache | 18.7 s of LZMA bundle loads | ~0.12 s |
| HarmonyBatching | wrapper builds 6.6-7.0 s, menu 27-29 s (4 runs) | 3.7-3.9 s, menu 20-21 s (4 runs, interleaved) |
| ConfigSaveBatcher | 1131 `.cfg` writes, 0.8-1.2 s | 33 writes, 0.05-0.09 s |
| LocalizationCache | vanilla `LoadCSV` bodies 0.6-0.8 s | 0.16-0.28 s |
| profiler itself | - | about +0.5 s (hence off by default) |

For comparison, StartupAccelerator's three features and LocalizationCache measured 2 runs each on the same
machine (menu ms; the machine was loaded by other test runs, +-3 s): none 29.3/25.1, Delay Patching 21.6/27.0,
Merge Localization 23.7/29.3, Delay Config Save 31.8/33.1, LocalizationCache 26.4/25.7. Sub-timings: Delay Patching
cut Harmony self time 5.4-6.2 s -> 3.2-3.8 s; Merge Localization / LocalizationCache cut `SetupLanguage`
1.1-1.2 s -> 0.45-0.6 s.

## Install

Copy `bin\Release\FastStartup.dll` to `Valheim\BepInEx\patchers\`. It is a patcher, not a plugin, so it does not
go into `plugins`.

Config `BepInEx\config\FastStartup.cfg`:

- `[Profiler] Enabled` (default `false`): record process start -> main menu and write the report. Costs about
  0.5 s of startup; turn it on to measure.
- `[Profiler] TimeModPatches` (default `false`): also time each mod's prefix/postfix on the profiled game methods
  (see "Mod patches by owner" below).
- `[BundleCache] Enabled` (default `true`): serve mods' embedded LZMA bundles from LZ4 copies.
- `[BundleCache] MaxCacheSizeMB` (default `2048`): size cap, least recently used copies are evicted.
- `[ConfigSaveBatcher] Enabled` (default `true`): one write per `.cfg` instead of one per `Bind`/value change during
  startup.
- `[LocalizationCache] Enabled` (default `true`): parse each vanilla localization CSV once per language.
- `[HarmonyBatching] Enabled` (default `true`): one wrapper build per patched method per `Harmony.PatchAll(assembly)`.
- `[Diagnostics] DumpHarmonyState` (default `false`): write the Harmony patch registry at the main menu to
  `BepInEx\FastStartup\harmony-state.txt` for diffing two setups.

All keys are read once at launch.

## Config save batcher

BepInEx 5.4.23 `ConfigFile.Bind` and every value change call `Save()`, which rewrites the whole file (1131 writes
here: 727 while plugins load, 395 more from AdventureBackpacks while the menu scene builds).

- From `Chainloader.Start` until the main menu, `ConfigFile.Save()` only records the file (once per file).
- Recorded files are written at the end of `Chainloader.Start` (a finalizer, so also when it throws) and again at
  the main menu, where deferring stops for good. Process exit and domain unload also flush.
- `ConfigFile.Reload()` of a recorded file writes it first, so a reload never reads an older disk copy. If the
  file changed on disk since FastStartup last saw it (last write time + length), e.g. the user edited it and a
  mod's file watcher reloads it, the reload reads that edit instead and the merged state is written at the next
  flush.
- `SaveOnConfigSet` is never touched: mods read back exactly what they set.
- Limits: a crash (native, no managed exit) before the main menu loses values set in memory since the last flush;
  `Bind` defaults are written again on the next launch anyway. A write error is logged by FastStartup instead of
  surfacing inside the mod's `Bind` call. Code reading a `.cfg` with plain file IO during startup sees the copy
  from the last flush.

## Localization cache

Vanilla `Localization.SetupLanguage` runs `LoadCSV` for 13 CSVs and runs 8 times during startup here (English +
the user language, again whenever a mod re-runs it). `LoadCSV` reads `TextAsset.text`, splits the CSV and calls
`AddWord(key, text)` per row.

- The first `LoadCSV` of each (TextAsset instance, language) records its `AddWord` calls; later calls replay them
  through the real `AddWord` and return `true` without parsing. In memory, this session only.
- The live dictionary is filled by the same `AddWord` sequence, so overlay order, `SetLanguage` (which clears the
  dictionary) and other mods' `AddWord` patches behave as vanilla. Other mods' `LoadCSV` prefixes/postfixes still
  run. Verified: the translation table (count + SHA-256) is identical with the cache on and off at the menu, after
  switching to English, and after switching back (twice).
- Not cached: a load another prefix skipped, a load that returned `false` (language column missing), a load that
  added no rows. The cache is bypassed while another mod transpiles `LoadCSV` or patches `DoQuoteLineSplit` /
  `StripCitations` (which a replay would skip).

## Harmony batching

HarmonyX 2.9 registers a patch and then rebuilds the whole replacement (`PatchFunctions.UpdateWrapper`: IL copy, all
patches, JIT, detour). A method patched by N classes of one `PatchAll(assembly)` was built N times (1712 builds for
956 methods here).

- Inside the outermost `Harmony.PatchAll(Assembly)` on a thread (also `PatchAll()`, which calls it),
  `UpdateWrapper` records the method instead of building it. When that `PatchAll` returns (finalizer, also on
  exception) every recorded method is built once from its current `PatchInfo`, in first-patched order, under
  Harmony's patch lock. `Harmony.Patch`, `PatchAll(Type)` and everything outside `PatchAll` build immediately.
- Patch registration is untouched: `Harmony.GetPatchInfo` is the same at every point outside the batch.
- Inlining: a wrapper is JIT-compiled when it is built, and Mono inlines small callees not yet marked NoInlining;
  a detour placed on such a callee later is bypassed by that caller. So every recorded method is pinned
  (`MonoMod DetourHelper.Pin`: NoInlining + prepare, the same call its detour makes) at the moment it would have
  been detoured, and unpinned (our pin only) after the batch.
- Verified: `DumpHarmonyState` at the main menu is byte-identical with batching off and on (790 methods), and
  identical to a run with every FastStartup module off except for 4 entries that are FastStartup's own profiler
  hook methods (Jotunn's ModQuery patches them). MorgottTweaks' intro skip works; autotest world layer passes with
  no new log errors.
- Limits: code that runs inside that same `PatchAll` (a patch class's `Prepare`/`TargetMethod(s)`/`Cleanup`, static
  constructors) sees methods patched earlier in that call unpatched. If a wrapper build fails, the error is
  rethrown from `PatchAll` after the other methods are built: later classes of that `PatchAll` are registered and
  built (immediate mode would have stopped at the failing class), and the failing class's `Cleanup` does not get
  the build exception. A transpiler runs once per batch instead of once per patch.

### Why StartupAccelerator lost the intro patch

StartupAccelerator deferred every wrapper build until the chainloader ended and then built them in `HashSet`
order. `FejdStartup.Start` (first patched by StarLevelSystem) was built before `FejdStartup.TryPlayIntroCinematic`
(patched by MorgottTweaks); Mono inlined the unpatched iterator factory into the new `Start`, so its detour was
bypassed. Evidence (same flush order in both runs): the prefix was registered and ran on a direct reflection call,
but not from `Start` (intro played); pinning `TryPlayIntroCinematic` (NoInlining) before the flush made the intro
skip work. In immediate mode later plugins rebuild `Start` after the callee is detoured, which hid the problem.

## Bundle cache

Mods ship their asset bundles as LZMA-compressed resources inside their DLLs and load them with
`AssetBundle.LoadFromStream`. Unity then decompresses each whole bundle on the main thread: 31 bundles, 18.7 s of
a 40.9 s startup here. The cache stores an LZ4 copy of each one and loads that with `AssetBundle.LoadFromFile`
(LZ4 is read chunk by chunk on demand): 31 loads in about 120 ms.

- Hooked: `AssetBundle.LoadFromStreamInternal` / `LoadFromStreamAsyncInternal` (every public `LoadFromStream*`
  overload ends there). File and memory loads are not touched: the vanilla SoftRef file bundles are already LZ4
  and no installed mod loads bundles from files or byte arrays.
- Key: the stream is Mono's resource stream (`RuntimeAssembly+UnmanagedMemoryStreamForModule`), so the owning
  module and resource name are known. Key = module MVID + resource name. The MVID changes with every build of the
  DLL and the resource bytes are part of that build, so nothing is hashed at load time. Streams that are not
  embedded resources, loads with a CRC, and streams not at position 0 load uncached.
- Only bundles with LZMA data blocks are cached (UnityFS header + LZ4 block table parsed); LZ4/uncompressed ones
  load as they are.
- First launch: originals load normally. After the main menu the queued bundles are extracted on a worker
  thread and recompressed one at a time with `AssetBundle.RecompressAssetBundleAsync(..., LZ4Runtime, ...,
  ThreadPriority.Low)` (about 20 s here, in the background).
- Storage: `BepInEx\FastStartup\cache\bundles\v1-<Unity version>\<mvid>-<name hash>.bundle`. The file name is
  the key; copies are written to `<file>.<pid>.<kind>.tmp` and renamed into place, so a crash never leaves a
  broken copy. `index.tsv` only holds last-use times for LRU; losing it never deletes a valid copy.
- Maintenance after the main menu (worker thread): deletes temps of dead processes, caches of other Unity
  versions, copies whose DLL (MVID) is no longer loaded (mod updated or removed), then the least recently used
  copies above `MaxCacheSizeMB`. Copies used in the current session are kept.
- Any error in the cache path falls back to the original load and is logged once.

Measured (menu ready, profiler on; SA = StartupAccelerator, LC = LocalizationCache):

| config | cold | warm | bundle loads warm |
| --- | --- | --- | --- |
| FastStartup cache, SA + LC | 43.7 s | 22.1 / 20.8 s | 115 / 120 ms |
| FastStartup cache only | - | 31.5 / 27.6 s | 139 / 124 ms |
| Fast AssetBundle Loader (warm), SA + LC | - | 35.3 s | 1446 ms |
| no bundle cache, no SA/LC | - | 40.9 s | 18722 ms |

With Fast AssetBundle Loader the `start` scene took 18.6 s instead of 7.6 s: it hooks the public
`LoadFromFile(Async)` overloads and MD5-hashes every vanilla SoftRef bundle (GBs) on the main thread on every
launch, which happens while the `start` scene loads.

## Mod patches by owner

`[Profiler] TimeModPatches = true` patches each prefix/postfix/finalizer that another mod put on the profiled game
methods and reports them per owner in `summary.txt`. Hooking forces Mono to compile those methods early; for some
methods (the ItemManager/PieceManager helpers in Warfare, Armory, Wizardry) that native compile crashes the game.
The method being hooked is written to `BepInEx\FastStartup\patch-probe.pending` first, and after a crash the next
launch moves it to `patch-probe.skip` and never hooks it again (5 launches to settle here).

## Profiler output

When the main menu is ready (end of the first `FejdStartup.Start`), the patcher logs one summary line and
overwrites these files:

- `BepInEx\FastStartup\trace.json`: Chrome trace. Open it in `chrome://tracing` or <https://ui.perfetto.dev>.
- `BepInEx\FastStartup\summary.txt`: lifecycle marks with GC counts, self time per category, top time sinks,
  per-plugin init time, Harmony time per owner, AssetBundle loads, game methods split into vanilla body and
  mod patches.

What it measures (monotonic `Stopwatch` spans kept in memory, nothing logged per event):

- Preloader and chainloader phases: `Chainloader.Initialize`, `Chainloader.Start`.
- Each plugin's assembly load, static constructor and `Awake`, keyed by plugin GUID (from the chainloader's
  `Loading [...]` log lines; no Unity method is patched for this).
- Harmony: `PatchAll`, class processors, `PatchProcessor.Patch` per owner ID, and every wrapper build
  (`PatchFunctions.UpdateWrapper`), including builds that another patcher deferred.
- AssetBundle loads from file, memory and stream, sync and async, with path and size.
- Game: `FejdStartup.Awake/Start/SetupGui/SetupObjectDB`, `ObjectDB.Awake/CopyOtherDB/UpdateRegisters`,
  `ZNetScene.Awake`, scene loads.
- Jotunn: item/piece registration into ObjectDB, when Jotunn is installed.

Limits: plugin work in Unity `Start()`/coroutines after `Awake` is not attributed to the plugin. Native Unity
work between hooked calls shows up as the gap between menu-ready time and the hooked total.

## Build

```powershell
dotnet build -c Release
```

References come from `D:\Steam\steamapps\common\Valheim` (`BepInEx\core`, `valheim_Data\Managed`); override
with `-p:ValheimDir=...`.
