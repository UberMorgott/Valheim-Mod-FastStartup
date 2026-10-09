# FastStartup

BepInEx 5 preloader patcher for Valheim startup speed. Personal build.

Modules: **startup profiler**, **bundle cache** (replaces Fast AssetBundle Loader), **config save batcher**,
**localization cache** and **Harmony batching** (these three replace StartupAccelerator and LocalizationCache),
**mod hotspots** (faster equivalents of slow startup code in other mods' embedded helpers), **world generation**
(parallel biome map on every world load / connect, faster location placement on new worlds, same output).
Design notes: `E:\DEV\Valheim\docs\designs\startup-accel-analysis.md`.

ValheimPlus overlap: none. ValheimPlus has no startup-speed, bundle-cache, profiling or world-generation speed
feature (its cfg has no world/zone/location generation section).

Everything FastStartup writes to disk lives under `Valheim\BepInEx\FastStartup\` (profile, bundle cache, Harmony
state dump); nothing goes to AppData, LocalLow or %TEMP%. Deleting that folder resets it all. The localization
cache is in memory only.

## Results

Menu ready (end of the first `FejdStartup.Start`), 31 plugins, warm = bundle cache filled:

| setup | cold | warm |
| --- | --- | --- |
| no accelerators (2026-09-19 morning) | - | 41 s |
| FastStartup 0.2.0, all modules, profiler off | - | 16.9 / 18.0 / 17.5 s (ShaderReplacer off, interleaved: 19.4 / 19.2 s) |
| FastStartup 0.1.0, all modules, profiler off | - | 18.6 / 18.8 / 19.1 s |
| FastStartup, all modules, profiler on | 38.2 s (cache built after the menu) | 20.0 / 19.2 / 19.4 s |
| FastStartup bundle cache + StartupAccelerator + LocalizationCache (before) | 43.7 s | 21-23 s |

Per module (warm, profiler sub-timings, same session, other load on the machine):

| module | before | after |
| --- | --- | --- |
| BundleCache | 18.7 s of LZMA bundle loads | ~0.12 s |
| HarmonyBatching | wrapper builds 6.6-7.0 s, menu 27-29 s (4 runs) | 3.7-3.9 s, menu 20-21 s (4 runs, interleaved) |
| ConfigSaveBatcher | 1131 `.cfg` writes, 0.8-1.2 s | 33 writes, 0.05-0.09 s |
| LocalizationCache | vanilla `LoadCSV` bodies 0.6-0.8 s | 0.16-0.28 s |
| ModHotspots ShaderReplacer | OreMines `ReplaceShaderPatch` 2.25 s | 0.19 s |
| profiler itself | - | about +0.5 s (hence off by default) |

For comparison, StartupAccelerator's three features and LocalizationCache measured 2 runs each on the same
machine (menu ms; the machine was loaded by other test runs, +-3 s): none 29.3/25.1, Delay Patching 21.6/27.0,
Merge Localization 23.7/29.3, Delay Config Save 31.8/33.1, LocalizationCache 26.4/25.7. Sub-timings: Delay Patching
cut Harmony self time 5.4-6.2 s -> 3.2-3.8 s; Merge Localization / LocalizationCache cut `SetupLanguage`
1.1-1.2 s -> 0.45-0.6 s.

World load (0.4.0 profiler, `summary-world.txt`): the Almanac postfix on `ObjectDB` took 10.7 s of the world-load
freeze; fixed upstream in Almanac 3.8.0.2 (17 ms).

## Changes

- 0.4.0: world-load profiling lane (recording to the first player spawn, world-load wall clock, top sinks named
  per owner), per-owner timing of world-load targets, Jotunn event-handler probe; fix for the Mono crash when the
  patch owner probe hooked bundled ItemManager/PieceManager helper libraries.

## Install

Copy `bin\Release\FastStartup.dll` to `Valheim\BepInEx\patchers\`. It is a patcher, not a plugin, so it does not
go into `plugins`.

Config `BepInEx\config\FastStartup.cfg`:

- `[Profiler] Enabled` (default `false`): record process start -> main menu (and on to the first spawn in a
  world) and write the report. Costs about 0.5 s of startup; turn it on to measure.
- `[Profiler] TimeModPatches` (default `false`): also time each mod's prefix/postfix on the profiled game methods
  (see "Mod patches by owner" below).
- `[BundleCache] Enabled` (default `true`): serve mods' embedded LZMA bundles from LZ4 copies (the modpack's
  prebuilt copies first, see "Modpack cache").
- `[BundleCache] MaxCacheSizeMB` (default `2048`): local cache size cap, least recently used copies not loaded in
  this session are evicted (the modpack cache is not counted or touched).
- `[ConfigSaveBatcher] Enabled` (default `true`): one write per `.cfg` instead of one per `Bind`/value change during
  startup.
- `[LocalizationCache] Enabled` (default `true`): parse each vanilla localization CSV once per language.
- `[HarmonyBatching] Enabled` (default `true`): one wrapper build per patched method per `Harmony.PatchAll(assembly)`.
- `[ModHotspots] ShaderReplacer` (default `true`): run blacks7ar's ShaderReplacer helper (OreMines) with one
  shader lookup instead of one per material.
- `[Diagnostics] DumpHarmonyState` (default `false`): write the Harmony patch registry at the main menu to
  `BepInEx\FastStartup\harmony-state.txt` for diffing two setups.
- `[Diagnostics] DumpModHotspots` (default `false`): write what the ModHotspots replacements produce (every
  ShaderReplacer material and its shader) at the main menu to `BepInEx\FastStartup\modhotspots-state.txt`, for
  diffing a toggle off against on.
- `[Profiler] TimeSpawnWindow` (default `false`): with the profiler on, also time the per-frame world-load methods
  between the main scene request and the first spawn, aggregated (see "Profiler output").
- `[WorldGen] ParallelBiomeData` (default `true`): build the biome/height map of every world load and connect on all
  cores (see "World generation").
- `[WorldGen] EarlyReject` (default `true`): new worlds, skip the terrain-delta samples of location candidates that a
  later cheap check rejects anyway.
- `[WorldGen] LoadingTimeBudget` (default `0.25`): seconds of location generation per frame while a new world is
  generated (vanilla 0.1; 0 = vanilla).
- `[WorldGen] DumpLocations` (default `false`): determinism dump at location generation,
  `BepInEx\FastStartup\diag\locations-<seed>.txt`.

All keys are read once at launch. With any module on, the log gets `menu ready <s> s after process start` at the
end of the first `FejdStartup.Start` (profiler off too).

## World generation

Harmony ID `morgott.faststartup.worldgen`, installed after `Chainloader.Initialize`. Every feature produces vanilla's
output; the log always gets `WorldGen: VerifyBiomeData <ms> (GenerateBiomePoints <ms> parallel|vanilla, GenerateSectors
<ms>)` per world load / connect and, with EarlyReject or the profiler on, a location generation summary.

- **ParallelBiomeData**: `AltBiomeWorldData.VerifyBiomeData` (every server world load, ZNet.cs:464, and every client
  connect, ZNet.cs:1117) rebuilds a 2048x2048 biome + height map, one `WorldGenerator.GetBiome` + `GetBiomeHeight` per
  cell, on the main thread. Those are pure functions of the seed (thread-safe native `Mathf.PerlinNoise`, FastNoise
  reads only its settings, the river lookup is a read-only dictionary plus a one-grid cache under the generator's own
  `ReaderWriterLockSlim`; the HeightmapBuilder thread already calls them concurrently). A replacing prefix runs the
  same loop body row by row with `Parallel.For` (each row writes only its own cells) and stores the result exactly as
  vanilla. `GenerateSectors` (flood fill, alt-biome RNG) stays sequential and untouched. Vanilla path whenever a
  foreign patch is on any method of `WorldGenerator`, `DUtils`, `FastNoise`, `BiomeHelpers` or the `AltBiomeWorldData`
  helpers, a foreign transpiler is on `GenerateBiomePoints`, another prefix skipped it, or a worker throws (logged).
- **EarlyReject** (new worlds): per candidate point `ZoneSystem.GenerateLocationsTimeSliced` (ZoneSystem.cs:1871)
  computes the terrain delta (10 x `Random.insideUnitCircle` + 10 x `GetHeight`, :1995) before the RNG-free checks
  similar / not-similar / vegetation / alt-biome (:2002-2037), any of which ends in the same `continue`. The location
  enumerator is wrapped to know the current location; a prefix on `WorldGenerator.GetTerrainDelta`, only inside it and
  only for the call with the location's exterior radius, runs those checks first and, when one fails, draws
  `insideUnitCircle` exactly 10 times and returns delta = +Inf (same RNG state, same `continue`). Off for a location
  whose `m_maxTerrainDelta` is not finite, and while a foreign patch is on any method involved. The stateful surround
  vegetation check is never evaluated early. Only the debug-build error counters differ.
- **LoadingTimeBudget**: `ZoneSystem.Update` sets `m_timeSlicedGenerationTimeBudget` to 0.1 s per frame while the
  server generates locations (the intro / cinematic frame-rate budget is left alone); a postfix raises that case to the
  configured value. Placement does not depend on where the coroutine yields (the generation RNG state is swapped around
  every yield and every location type reseeds).
- **DumpLocations**: at `LocationsGenerated` writes `BepInEx\FastStartup\diag\locations-<seed>.txt`: SHA-256 of
  `PointBiomes` / `PointHeights`, then every location instance sorted by prefab name and zone, position as float bits.

Measured on the lab dedicated server (Ryzen 7 9800X3D, 16 threads; the pack's 35 server plugins, 197 location types
incl. modded, ~12.8k instances; 3 new worlds x 2 runs each, every run a fresh generation from the same `.fwl`):

| | vanilla (WorldGen off) | WorldGen on |
| --- | --- | --- |
| `GenerateBiomePoints` (every load / connect) | 3.0-3.45 s | 0.59-1.15 s |
| `VerifyBiomeData` (+ sequential `GenerateSectors` 0.44-0.69 s) | 3.43-3.89 s | 1.05-1.79 s |
| location generation (vanilla `Genloc duration`) | 19.3-22.9 s | 14.5-16.0 s |

- EarlyReject skipped 539k-561k of 778k-858k terrain-delta calls; EarlyReject alone 16.2 / 17.8 s,
  LoadingTimeBudget alone 22.6 / 23.1 s (no gain on a dedicated server, whose frames are cheap; on a host with the
  loading screen it saves the frames between slices, not measured yet).
- Determinism: for each of the 3 seeds the 4 dumps (off, on, off, on) are byte-identical (biome map SHA, heights
  SHA, all location instances); on the first seed also the EarlyReject-only, budget-only and profiler-on dumps.

## Mod hotspots

Slow startup code inside other mods, reimplemented with the same result. Many mods embed the same helper
sources, so a replacement matches the helper by shape, not by mod: type/method/field names plus an IL fingerprint
(SHA-256 over every instruction and operand of the method and its compiler-generated lambdas, the helper's
namespace and the mod's anonymous-type numbering stripped). A helper with a different fingerprint is logged and left
alone. Installed at the end of `Chainloader.Start` (after every plugin's Awake, before the menu scene).

- **ShaderReplacer** (Harmony ID `blacks7ar.utilities.ShaderReplacer`, OreMines 1.x): its postfix on
  `FejdStartup.Awake` calls `Resources.FindObjectsOfTypeAll<Shader>()` and compares names with every loaded shader
  once per material of its 17 mine prefabs: 2.25 s. The replacement walks the same objects, renderers and materials in
  the same order and makes the same assignments (every shader of the material's shader name, in
  `FindObjectsOfTypeAll` order, so the last one wins), with the shader list read once and grouped by name: 0.19 s
  (the rest is the walk and the `Material.shader` assignments themselves). SeedBed embeds the later variant of this
  helper, which already uses a name dictionary (0.8 ms); its fingerprint differs and it is not touched.
  Verified: `DumpModHotspots` (36051 lines, one per material slot: renderer path, material, shader name + index among same-named
  shaders, render queue, keywords) byte-identical with the toggle off and on; `DumpHarmonyState` byte-identical
  with every FastStartup module off and with all on (790 methods).

Looked at and not replaced (profiler, `TimeModPatches`, warm):

- Jotunn on `ObjectDB.CopyOtherDB`, 0.53 s: Jotunn's own part is `RegisterCustomDataFejd` (~50 ms); 0.48 s is other
  mods' `PrefabManager.OnVanillaPrefabsAvailable` handlers (ChaosArmor `LoadItems` 197 ms: bundle asset loads +
  `AddItem`; MonsterModifiers `CreateCustomPrefabs` 166 ms; AdventureBackpacks `InitializeBackpacks` 107 ms). The
  biggest shared piece is Jotunn's `PrefabManager.Cache.InitCache(GameObject)` (first `GetPrefab` miss, 150-220 ms
  over 25580 names). A rewrite that skips re-reading the parent of the already mapped object was proven equal
  (in-process comparison against a reverse patch of the original, 0 differences for all 5 types) but saved only
  ~23 ms: the cost is `FindObjectsOfTypeAll` and the name reads. Dropped.
- Jotunn `ModQuery.FejdStartup_Awake_Postfix` (~150 ms): one Harmony patch per other mod's patch method on
  ZNetScene/ObjectDB methods, each a separate wrapper build; nothing to batch.
- PieceManager/CreatureManager `Patch_FejdStartup` (Warfare 190 ms, Wizardry 148 ms): per-piece/creature config
  `Bind` + localization + a `new Regex` per piece; a replacement would reimplement the helper's config generation.
- Seasonality `TextureReplacer` postfix (222 ms): generates seasonal textures; SkillManager (2 ms).

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
- A new local copy is kept only when its UnityFS directory (node paths + sizes) equals the source bundle's.
- Maintenance after the main menu (worker thread): deletes temps of dead processes, caches of other Unity
  versions, copies whose DLL (MVID) is no longer loaded (mod updated or removed), local duplicates of keys the
  modpack cache served this session, then the least recently used copies above `MaxCacheSizeMB`. Copies loaded in
  the current session are never deleted; the modpack cache is never touched.
- Any error in the cache path falls back to the original load and is logged once.

### Modpack cache (prebuilt copies)

The pack can ship the copies so the first launch after an install or mod update skips the LZMA path.

- Location in the game folder (read-only for FastStartup; the launcher may restore it at will):
  `BepInEx\FastStartup\pack\bundles\v1-<Unity version>\manifest.tsv` + `<mvid>-<name hash>.bundle`. In the pack:
  `client\BepInEx\FastStartup\pack\bundles\v1-6000.0.75f1\`. A Unity update changes the folder name, so an old pack
  cache is simply not found.
- Lookup order: pack copy, local copy, original load (+ local rebuild).
- A pack copy is served only when all hold for its key: the `manifest.tsv` row names the same resource and source
  length; the copy has the manifest length, is a complete LZ4 UnityFS file and has the same directory (node paths +
  sizes) as the source bundle; the SHA-256 of the copy and of the source resource equal the manifest. The hashes
  are computed once per file version (copies on the thread pool from `Chainloader.Initialize`, before plugins load
  bundles; the source at its first load) and stamped in `cache\bundles\v1-...\pack-verified.tsv` (copy length +
  last write time, DLL length + last write time, both hashes); later launches compare lengths and times only. A
  rejected copy is logged with the reason and the bundle takes the local path, so a stale or damaged pack copy is
  never loaded and is rebuilt locally.
- Limit: an in-place edit that keeps both the length and the last write time of a copy or DLL is not rehashed.
- Build (on the PC that made the pack, after a launch reached the menu and the background recompress logged
  `cached N bundles`):

  ```powershell
  pwsh -File tools\build-pack-cache.ps1 -PluginDirs Z:\modpacks\Valheim\universal\BepInEx\plugins,Z:\modpacks\Valheim\client\BepInEx\plugins -OutDir <dir>
  # then copy <dir>\* to Z:\modpacks\Valheim\client\BepInEx\FastStartup\pack\bundles\
  ```

  It takes the local copies whose DLL (by MVID) is in the pack, writes them plus a sorted `manifest.tsv` (file,
  resource, source bytes, source SHA-256, copy bytes, copy SHA-256, owner DLL; no times or paths), so the same
  inputs give byte-identical output, and lists pack bundles that have no local copy. Like the local cache it
  trusts the MVID: a local copy is taken as made from the DLL with that MVID (FastStartup checked its directory
  against the source when it stored it). Rebuild it whenever a pack
  DLL with bundles changes: rows of DLLs no longer in the pack are left out, and a row whose DLL changed is
  rejected at load time anyway.
- Timing A/B (menu ready; own `valheim.exe` only, parks and restores the cache folders):
  `pwsh -File tools\startup-ab.ps1 -Setup Pack -PackDir <dir> -Repeat 3`, then `-Setup Local`, then `-Setup None`.

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
methods and reports them per owner in `summary.txt`; FastStartup's own WorldGen prefixes too (owner
`morgott.faststartup.worldgen`: they replace vanilla bodies such as `GenerateBiomePoints`). Hooking forces Mono to compile those methods early; for some
methods (the ItemManager/PieceManager helpers in Warfare, Armory, Wizardry) that native compile crashes the game.
The method being hooked is written to `BepInEx\FastStartup\patch-probe.pending` first, and after a crash the next
launch moves it to `patch-probe.skip` and never hooks it again (5 launches to settle here). This covers the
world-load methods too (`ZNet`, `ZoneSystem`, `Game`, `Minimap`, `ObjectDB`, `ZNetScene`, `DLCMan` ...). The handlers
other mods subscribe to Jotunn's events (`PrefabManager.OnVanillaPrefabsAvailable/OnPrefabsRegistered`,
`ItemManager.OnItemsRegistered(Fejd)`, `PieceManager.OnPiecesRegistered`, the `CreatureManager`, `ZoneManager`,
`DungeonManager`, `GUIManager`, `LocalizationManager`, `MinimapManager` events ...) are timed the same way, owner
`Jotunn event handler [<mod>]`. Patches and subscriptions added after `Chainloader.Start` are picked up again at
the main menu, before the world load.

## Profiler output

When the main menu is ready (end of the first `FejdStartup.Start`), the patcher logs one summary line and
overwrites these files:

- `BepInEx\FastStartup\trace.json`: Chrome trace. Open it in `chrome://tracing` or <https://ui.perfetto.dev>.
- `BepInEx\FastStartup\summary.txt`: lifecycle marks with GC counts, self time per category, top time sinks,
  per-plugin init time, Harmony time per owner, AssetBundle loads, game methods split into vanilla body and
  mod patches.

Recording goes on to the first player spawn (end of the first `Game.SpawnPlayer`); then it stops and writes
`trace-world.json` + `summary-world.txt` (process start -> spawn, same sections, plus a line with menu-ready
time, world load = `FejdStartup.LoadMainScene` start -> spawn, and the time spent in the menu before it). Quitting
before the spawn writes what was recorded as a partial world trace. `summary-world.txt` starts with the world-load
wall clock (click = `FejdStartup.OnWorldStart` -> `LoadMainScene` end -> main scene loaded, i.e. the freeze in which
every main-scene `Awake` runs -> first rendered frame, loading screen visible -> `ZoneSystem.Start` end) and the top
time sinks inside that window by self time, each mod patch / Jotunn handler named by owner.

What it measures (monotonic `Stopwatch` spans kept in memory, nothing logged per event):

- Preloader and chainloader phases: `Chainloader.Initialize`, `Chainloader.Start`.
- Each plugin's assembly load, static constructor and `Awake`, keyed by plugin GUID (from the chainloader's
  `Loading [...]` log lines; no Unity method is patched for this).
- Harmony: `PatchAll`, class processors, `PatchProcessor.Patch` per owner ID, and every wrapper build
  (`PatchFunctions.UpdateWrapper`), including builds that another patcher deferred.
- AssetBundle loads from file, memory and stream, sync and async, with path and size.
- Game: `FejdStartup.Awake/Start/SetupGui/SetupObjectDB`, `ObjectDB.Awake/CopyOtherDB/UpdateRegisters`,
  `ZNetScene.Awake`, scene loads.
- World load: `FejdStartup.OnWorldStart/LoadMainScene`, `ZNet.Awake/Start/LoadWorld`, `ZoneSystem.Awake/Start/
  GenerateLocationsIfNeeded`, `Minimap.Awake/Start`, `EnvMan.Awake`, `DLCMan.Awake`, `Game.Awake/Start/SpawnPlayer/
  CollectResources`, the first rendered frame after `LoadMainScene` and after the main scene load, the main scene
  load (request -> `sceneLoaded`, includes the menu scene's unload). Coroutine work after these calls (location
  placement, the scene unload itself) shows up as gaps.
- Jotunn, when installed: `PrefabManager` / `ItemManager` / `PieceManager` / `CreatureManager` registration steps
  and event invocations, `MockManager.FixReferences` (outermost call) and `FixQueuedMaterials`.

- World generation: `AltBiomeWorldData.VerifyBiomeData/GenerateBiomePoints/GenerateSectors`, `ZoneSystem.SpawnZone/
  PlaceLocations/SpawnLocation`, `DungeonGenerator.Generate(int, SpawnMode)`; on a new world one span per location type
  (lane "location generation": wall clock, busy ms, terrain-delta calls, delta rejects, EarlyReject skips) and a
  "Location generation" summary section.
- `[Profiler] TimeSpawnWindow`: `ZoneSystem.CreateLocalZones/PokeLocalZone/IsActiveAreaLoaded`, `ZNetScene.
  CreateDestroyObjects/CreateObjects/IsAreaReady`, `HeightmapBuilder.IsTerrainReady/RequestTerrainSync/Build` (the
  builder thread), `Game.FindSpawnPoint`, `Player.UpdateTeleport`, `DungeonGenerator.Load`, `SnapToGround.SnappAll`,
  `Heightmap.ForceGenerateAll`, between `FejdStartup.LoadMainScene` end and the end of the first `Game.SpawnPlayer`.
  First prefix / Last finalizer (other mods' patches, e.g. Skidbladnir's, are inside); calls / total / max per method
  (with `TimeModPatches` also per mod patch method), written as one span per method on the "spawn window totals" lane
  and in the "Spawn window" summary section; the three one-shot calls also get a span each. After the first spawn
  the hooks only push/pop a stack entry.

Limits: plugin work in Unity `Start()`/coroutines after `Awake` is not attributed to the plugin. Native Unity
work between hooked calls shows up as the gap between menu-ready time and the hooked total.

## Build

```powershell
dotnet build -c Release
```

References come from `D:\Steam\steamapps\common\Valheim` (`BepInEx\core`, `valheim_Data\Managed`); override
with `-p:ValheimDir=...`.

## Licence

CC BY-NC 4.0, see [LICENSE](LICENSE). Copyright (c) 2026 Morgott.
