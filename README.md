# FastStartup

BepInEx 5 preloader patcher for Valheim startup speed. Personal build, not published.

Module 1 (this release): **startup profiler**. Later modules (bundle cache, config save batching) come once
the profile shows where the time goes. Design notes: `E:\DEV\Valheim\docs\designs\startup-accel-analysis.md`.

ValheimPlus overlap: none. ValheimPlus has no startup-speed or profiling feature.

## Install

Copy `bin\Release\FastStartup.dll` to `Valheim\BepInEx\patchers\`. It is a patcher, not a plugin, so it does not
go into `plugins`.

Config `BepInEx\config\FastStartup.cfg`:

- `[Profiler] Enabled` (default `true`): record process start -> main menu and write the report.

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
