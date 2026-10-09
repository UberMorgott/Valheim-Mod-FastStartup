# FastStartup

BepInEx 5 preloader patcher for Valheim startup speed. Personal build.

Modules: **startup profiler**, **bundle cache** (replaces Fast AssetBundle Loader), **config save batcher**,
**localization cache** and **Harmony batching** (these three replace StartupAccelerator and LocalizationCache),
**mod hotspots** (faster equivalents of slow startup code in other mods' embedded helpers), **world generation**
(faster world load / connect: exact lake merge, cached river placement, biome map and sectors built while the main
scene loads; faster location placement on new worlds; same output).
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

World load, click -> first spawn, existing world `AutotestWorld` (single player = server), profiler off, the
`world load` log line; autotest `-Mod Almanac -Shots 20-hud`, interleaved rounds under one game lock, same mod set
(2026-10-09, Ryzen 7 9800X3D; "off" = the four new WorldGen keys off, ParallelBiomeData on as in 0.5.0):

| config | world load (s) | freeze (s) | VerifyBiomeData (ms) |
| --- | --- | --- | --- |
| off (0.5.0) | 18.98 / 17.53 (third run: Steam init failed) | 5.49 / 4.65 | 1105 / 1052 |
| + FastLakes | 18.50 / 15.79 / 15.96 | 4.74 / 3.18 / 3.37 | ~1000 |
| + PregenCache (warm) | 15.39 / 16.75 / 16.20 | 2.66 / 3.33 / 3.23 | ~980 |
| + PrefetchBiomeData | 14.43 / 14.90 / 15.98 | 2.53 / 2.57 / 3.42 | 388-506 |
| + PrefetchSectors (all on) | 14.16 / 13.74 / 13.94 | 2.70 / 2.48 / 2.60 | 3 |

Mean 18.26 -> 13.95 s (-4.3 s), freeze 5.07 -> 2.59 s. An earlier batch (other MorgottTweaks build, 2 rounds): off
19.26 / 18.50, all on 15.35 / 16.40. Inside: `FindLakes` 1.5 s -> 60 ms, pregeneration ~0.66 s -> 0.2 s (cache hit),
`VerifyBiomeData` 1.1 s (3.5-4 s with ParallelBiomeData off) -> 3 ms on the main thread. Determinism: the
`WorldGen: state` hashes (sectors, biome map, heights, lakes, rivers, streams, all river points) are equal for true
vanilla (every WorldGen key off), all on with a cache miss and all on with a cache hit; the state Pregenerate leaves
(river cache included) is equal on miss and hit; `FastLakes verify: identical`.

Where the rest of the world load goes (profiler, frame probe + Awake/Start probe, same world):

- About 8 s from the first rendered frame to the spawn: vanilla `Game.FindSpawnPoint` (Game.cs:533-561) spawns at a
  logout point only once `m_respawnWait > m_respawnLoadDuration` (8 s of game time; `Spawned after 8.02` in every
  run) and the area is ready. The area is ready after ~3-4 s here; the rest of those frames wait for the frame cap
  (TimeUpdate). Not changed: a vanilla behaviour, not work.
- Freeze: native scene load + old scene unload ~1.7 s unhooked; `ZNetScene.Awake` / `ObjectDB.Awake` mod postfixes
  ~1.9 s (Jotunn and its event handlers, ItemDrawers icon atlas 0.4 s once per session, MorgottTweaks AuraVisuals
  0.15-0.17 s, Warfare collider fix 0.1 s), ConditionalConfigSync 0.16 s on ZNet.Awake.
- Spawn window: zone and object streaming (`ZNetScene.CreateObjects` under Skidbladnir's Streaming patch,
  `ZoneSystem.CreateLocalZones` / `SpawnZone`), per-object Awake (StaticPhysics, ZNetView, SlowUpdate, LodFadeInOut).

## Changes

- 0.6.0: world-load module work (FastLakes, PregenCache, PrefetchBiomeData, PrefetchSectors, river cache
  refresh before parallel builds), `world load` log line with profiler off, profiler frame probe (player-loop phases
  per frame, click -> spawn), Awake/Start probe (`TimeUnityMessages`), spans inside `ZNet.Awake` / `ZNet.LoadWorld`.
  Measured: world load 18.26 -> 13.95 s, freeze 5.07 -> 2.59 s.
- 0.5.0: WorldGen module (parallel biome map, early location rejects, loading time budget); spawn-window
  profiling (world generation spans, aggregated spawn-window timing); fix: WorldGen prefix time is attributed to its
  owner instead of untimed mod patches.
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
  `BepInEx\FastStartup\diag\locations-<seed>.txt`; also logs `WorldGen: state ...` hashes at every world load and the
  FastLakes / PregenCache self-checks (costs ~2 s per load).
- `[WorldGen] FastLakes` (default `true`): exact grid-based lake merge on every world load / connect.
- `[WorldGen] PregenCache` (default `true`): river / stream placement cache, `BepInEx\FastStartup\cache\worldgen`.
- `[WorldGen] PrefetchBiomeData` (default `true`, needs ParallelBiomeData): biome map built while the main scene loads.
- `[WorldGen] PrefetchSectors` (default `true`, needs PrefetchBiomeData): biome sectors built on the same worker.
- `[Profiler] TimeUnityMessages` (default `false`): Awake/Start of every game MonoBehaviour per world-load phase.

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
  `PointBiomes` / `PointHeights`, lakes, rivers, streams, then every location instance sorted by prefab name and zone,
  position as float bits. At every `VerifyBiomeData` end it logs `WorldGen: state ...`: hashes of the sector graph
  (every sector's fields, neighbours as indices, each biome's point lists, the per-cell sector index), biome map,
  heights, lakes, rivers, streams and all `m_riverPoints` entries in enumeration order.
- **FastLakes**: `WorldGenerator.FindLakes` (WorldGenerator.cs:275, every load / connect, inside `ZNet.Awake` on a
  server) merges ~8k under-water grid points into lakes with `MergePoints` (:293), which scans the whole remaining
  list per merge step (`FindClosest`, :316): O(n^2), 1.5 s. The replacement does the same list operations (take index
  0, swap-remove the merged point) on an array window and finds the same closest point through a grid (cell = range,
  5x5 cells searched, so nothing outside can be within range even with rounding), same `Vector2 ==` skip, same
  `Vector2.Distance`, same tie rule (lowest list index wins). Vanilla while another mod patches MergePoints /
  FindClosest / FindLakes. With DumpLocations the result is compared with a reverse-patched copy of the original on
  the same input and on 8 synthetic inputs (`FastLakes verify: identical`).
- **PregenCache**: the rest of `Pregenerate` (WorldGenerator.cs:252: PlaceRivers, PlaceStreams x2, ~0.5 s of random
  start/end searches) depends only on the seed-derived generator fields. After a vanilla run the lakes, the three
  river lists, the `Random.state` at each of the three `RenderRivers` calls and the final one-grid river cache are
  written to `BepInEx\FastStartup\cache\worldgen\pregen-<key>.bin` (~180 KB). A hit assigns the lists and calls the
  original `RenderRivers` with the recorded states (so `m_riverPoints` is built by vanilla code in vanilla order) and
  leaves `Random.state` as vanilla does. Key = Unity version + assembly_valheim MVID + every int/float field of the
  generator at entry, stored in full and compared; payload SHA-256 checked. Off while a foreign patch is on the
  generator path; 32 files kept (LRU).
- **PrefetchBiomeData**: on a server the map is first needed in `ZNet.Start` (VerifyBiomeData, ZNet.cs:464), one frame
  after `WorldGenerator.Initialize` in `ZNet.Awake` (:381). A postfix on Initialize starts the same parallel build on
  worker threads; VerifyBiomeData takes the result (waiting if needed), so it runs during the rest of the main
  scene's Awake calls and the native scene load. `world.m_biomeData` is assigned at the vanilla moment. A running
  prefetch is always finished before the next Initialize (vanilla clears the old generator's river data there
  without a lock) and before a vanilla fallback. Before any parallel build the generator's one-grid river cache is
  pointed at the grid's current array: Pregenerate can leave it holding an array a later `RenderRivers` replaced
  (:344-347 then :559-568), which a parallel worker could hit where the sequential build would have evicted it.
- **PrefetchSectors**: the pure part of `GenerateSectors` (AltBiomeWorldData.cs:150-256: flood fill, edges,
  neighbours, bounds, zones, heights) runs on the prefetch worker on the unpublished data; the rest (:257-296:
  `SectorsCalculated`, discovered flags from `ZoneSystem.IsZoneLoaded`, distances, `GenerateAltBiomes` with
  `UnityEngine.Random`) stays on the main thread at the vanilla moment through a prefix that applies only to that
  object. Off while a foreign patch is on VerifyBiomeData, GenerateBiomePoints, GenerateSectors, tryFill, the
  BiomeSector / BiomeTypeInfo constructors or ZoneSystem.GetZone. The generator-path guard of every WorldGen feature
  also covers `Utils.LerpStep` / `Utils.FloorToInt`.

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

- With `TimeSpawnWindow`, frame probe: marker systems at the start of every top-level Unity player-loop phase
  (Initialization, TimeUpdate, EarlyUpdate, FixedUpdate, PreUpdate, Update, PreLateUpdate, PostLateUpdate) and at the
  end of PostLateUpdate split every frame from the world-start click to the first spawn. "frames" lane (one span per
  frame, detail = ms per phase + gen0 GC count) and a "Frames" summary section: per world-load phase (before the
  scene request / freeze / scene loaded -> first frame / first frame -> spawn) frames, ms, hooked vs unhooked ms and ms
  per player-loop phase; the 25 longest frames with the hooked top-level spans inside them. TimeUpdate is where Unity
  waits for the frame cap / vsync, EarlyUpdate is where a sync scene load runs.
- `[Profiler] TimeUnityMessages`: every Awake/Start of assembly_valheim's MonoBehaviours (~330 methods), calls / self /
  inclusive ms per method and world-load phase, as a summary section. It slows every object created before the spawn
  (ZNetView, StaticPhysics, Piece ... thousands of Awake calls), so the spawn window is inflated while it is on.
- Spans inside `ZNet.Awake` (SteamManager / ZSteamMatchmaking / ZPlayFabMatchmaking.Initialize, WorldGenerator.Initialize,
  FindLakes, PlaceRivers, PlaceStreams, RenderRivers) and `ZNet.LoadWorld` (`ZDOMan.LoadChunks`).
- Profiler off too (any module on): the log gets `world load <s> s (click -> first spawn): click -> LoadMainScene end,
  freeze (-> main scene loaded), -> spawn` once per session.

Limits: plugin work in Unity `Start()`/coroutines after `Awake` is not attributed to the plugin. Native Unity
work between hooked calls shows up as the gap between menu-ready time and the hooked total.

## Spike probe (in-play hitches)

`[Profiler] SpikeProbe = true` (independent of `Enabled`; `SpikeThresholdMs`, default 33) records every play frame
(local player exists) and, for each frame at or over the threshold, what ran in it. Release-player signals only:

- Frame split: at the main menu a marker system goes before every top-level player-loop entry and every subsystem
  (149 markers in 1.0.17), so a spike frame names its slowest subsystems (`Update.ScriptRunBehaviourUpdate`,
  `PostLateUpdate.FinishFrameRendering`, `FixedUpdate.PhysicsFixedUpdate`, `TimeUpdate.WaitForLastPresentation...`).
- Hooks (prefix First + finalizer Last, observer, self time = nested hooked calls subtracted): `ZNetScene.CreateObject`,
  `ZoneSystem.SpawnZone/SpawnLocation`, `DungeonGenerator.PlaceRoom`, `Heightmap.Regenerate/RebuildRenderMesh/
  RebuildCollisionMesh/ForceGenerateAll`, AssetBundle sync loads, `Texture2D.Apply`; count, ms and the slowest call's
  prefab / zone / location / room name.
- First seen: the first instance of each net prefab, location and room is scanned once (each material once) for shaders
  and (shader, keywords, instancing, renderer kind) combinations new to the session: shader-compile candidates.
- ProfilerRecorder counters marked "available in release players" in the Unity 6000.0 profiler counters reference:
  GC Used / GC Reserved / Total Used / System Used Memory, SetPass Calls, Draw Calls, Batches, Vertex/Index Buffer Upload
  In Frame Bytes, Render Textures Changes, Shadow Casters, Visible Skinned Meshes. `GC.CollectionCount` per generation.
  Profiler markers (`GC.Collect`, `Shader.CreateGPUProgram`, `Loading.ReadObject` ...) do not exist in a release player
  (the summary lists them valid/nonzero; `profiler-available.txt` = all 44 metrics `ProfilerRecorderHandle.GetAvailable`
  returns), and Frame Timing Stats (CPU/GPU frame time) is off in Valheim's build.
- Player state on spike frames: segment label (`SpikeProbe.Segment`, set by a test driver), teleporting, interior,
  zone changed in the last second, speed, creatures within 30 m, combat timer.

Output `BepInEx\FastStartup\spikes.tsv` (one row per spike) and `spike-summary.txt` (frame time per segment, causes
ranked by total ms with owner, hook totals, first-seen correlation, session time per subsystem, top 40 spikes), every 30 s
in play (worker thread) and at quit. Cause = the largest of: a hook's self time, script time not in a hook (`scripts`,
`scripts+gc` with a collection), render (PostLateUpdate), physics, async load integration, present wait; `+newVariants`
when the frame or the one before had first-seen shader combinations. Overhead: 5.6 us per frame mean.

Measured 2026-10-09 with the autotest route `99s-spike-tour` (tools repo a950ff9: idle, 90 s run into new zones, portal
home, the same path again, Crypt3 dungeon, 6 Greydwarfs, 30 pieces, 150 s idle), full modpack, RTX 5070 Ti, 2 runs per
profile; 4 / 2 cores = `autotest.ps1 -Affinity N` (Unity and Mono still report 16 cores, so Unity keeps its 16-core
job-worker pool: the 2-core profile is harsher than a real 2-core PC). Per run, spikes >= 33 ms:

| profile | spikes | spike ms | run-new | run-repeat | idle (150 s) | top causes (ms per run) |
|---|---|---|---|---|---|---|
| 16 cores | 150 | 11 308 | 69 / 3 742 | 13 / 644 | 0 | create 2 837, scripts 2 517, spawn-frame GC 2 353, zone 1 651 |
| 4 cores | 164 | 11 769 | 27 / 1 371 | 13 / 631 | 22 / 1 096 | create 3 730, scripts 2 976, spawn-frame GC 1 885, render 1 870 |
| 2 cores | 1 438 | 105 995 | 243 / 22 926 | 214 / 14 731 | 307 / 16 155 | render 55 154, scripts 21 328, physics 8 390, create 4 949 |

- Shader compiles are not the hitch source: frames with first-seen shader combinations (in the frame or the one
  before) are 13 of 301 spikes / 4 % of spike ms on 16 cores, 3 % on 4, 1 % on 2, and none of them is render- or
  present-bound; the run-new -> run-repeat drop (3 742 -> 644 ms) is zone / location / room generation, not render.
- The first play frame after every spawn costs 1.6-4.0 s (2 full GCs, all `Start()` calls).
- On 2 cores `PostLateUpdate.FinishFrameRendering` (render thread / job workers starved) and physics dominate even idle.
- WorldGen on 2 cores (2 runs each): world load on 20.0 / 21.4 s, off 31.6 / 27.2 s; freeze on 7.3 / 8.7 s, off 13.2 /
  8.3 s. The worker threads do not starve the main thread, so no core-count cap.

## Build

```powershell
dotnet build -c Release
```

References come from `D:\Steam\steamapps\common\Valheim` (`BepInEx\core`, `valheim_Data\Managed`); override
with `-p:ValheimDir=...`.

## Licence

CC BY-NC 4.0, see [LICENSE](LICENSE). Copyright (c) 2026 Morgott.
