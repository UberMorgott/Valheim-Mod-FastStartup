using System;
using System.Collections.Generic;
using System.Reflection;
using FastStartup.Core;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace FastStartup.Profiling
{
    /// <summary>
    /// Valheim startup methods, scene loads and the main-menu-ready trigger. Game types are only touched here,
    /// after Chainloader.Initialize, so the preloader never loads assembly_valheim. Every method gets two hook
    /// pairs: an outer one (First prefix / Last finalizer, category <c>game</c>) around all mods' patches, and an
    /// inner one (Last prefix / First postfix, category <c>game.body</c>) around the original body only (Harmony
    /// runs every postfix before any finalizer, so the inner end must be a postfix); outer minus body = mods'
    /// patch cost. The outer finalizer always runs and rebalances the inner stack when an exception skipped the
    /// postfix. All hooked methods run on the Unity main thread.
    /// </summary>
    internal static class GameLifecycleProbe
    {
        private static readonly SpanStack OuterSpans = new SpanStack();
        private static readonly SpanStack BodySpans = new SpanStack();
        private static readonly List<int> BodyDepths = new List<int>();
        private static long _sceneRequest;
        private static bool _menuReady;

        /// <summary>Raised once, on the main thread, at the end of the first FejdStartup.Start.</summary>
        public static event Action MenuReady;

        // Citations: ValheimDecompiled-1.0.15\assembly_valheim\<File>.cs:<line>.
        internal static MethodInfo[] Targets() => new[]
        {
            AccessTools.DeclaredMethod(typeof(FejdStartup), "Awake"),         // FejdStartup.cs:307
            AccessTools.DeclaredMethod(typeof(FejdStartup), "Start"),         // FejdStartup.cs:427 (end = main menu ready)
            AccessTools.DeclaredMethod(typeof(FejdStartup), "SetupGui"),      // FejdStartup.cs:505
            AccessTools.DeclaredMethod(typeof(FejdStartup), "SetupObjectDB"), // FejdStartup.cs:855
            AccessTools.DeclaredMethod(typeof(ObjectDB), "Awake"),            // ObjectDB.cs:35
            AccessTools.DeclaredMethod(typeof(ObjectDB), "CopyOtherDB"),      // ObjectDB.cs:41
            AccessTools.DeclaredMethod(typeof(ObjectDB), "UpdateRegisters"),  // ObjectDB.cs:50
            AccessTools.DeclaredMethod(typeof(ZNetScene), "Awake"),           // ZNetScene.cs:33
        };

        public static void Install(Harmony harmony)
        {
            int hooked = 0;
            foreach (MethodInfo method in Targets())
            {
                if (method == null)
                {
                    Log.Warning("Game probe: a target method was not found (game update?), skipped");
                    continue;
                }
                harmony.Patch(method,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(GameLifecycleProbe), nameof(OuterPrefix)), Priority.First),
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(GameLifecycleProbe), nameof(OuterFinalizer)), Priority.Last));
                harmony.Patch(method,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(GameLifecycleProbe), nameof(BodyPrefix)), Priority.Last),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(GameLifecycleProbe), nameof(BodyPostfix)), Priority.First));
                hooked++;
            }

            // SceneLoader.Start -> StartLoading -> LoadSceneAsync (SceneLoader.cs:74/118/123): the scene request.
            MethodInfo sceneLoaderStart = AccessTools.DeclaredMethod(typeof(SceneLoader), "Start");
            if (sceneLoaderStart != null)
            {
                harmony.Patch(sceneLoaderStart,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(GameLifecycleProbe), nameof(SceneLoaderStartPrefix))));
            }

            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.activeSceneChanged += OnActiveSceneChanged;
            Log.Info($"Game probe: {hooked} game methods hooked");
        }

        private static void OuterPrefix()
        {
            BodyDepths.Add(BodySpans.Count);
            OuterSpans.Push();
        }

        private static void BodyPrefix() => BodySpans.Push();

        private static void BodyPostfix(MethodBase __originalMethod) => Finish(__originalMethod, "game.body", BodySpans.Pop());

        private static void OuterFinalizer(MethodBase __originalMethod)
        {
            int last = BodyDepths.Count - 1;
            if (last >= 0)
            {
                BodySpans.TruncateTo(BodyDepths[last]);
                BodyDepths.RemoveAt(last);
            }
            Finish(__originalMethod, "game", OuterSpans.Pop());
        }

        private static void Finish(MethodBase original, string cat, long start)
        {
            if (start == 0)
            {
                return;
            }
            long end = StartupTrace.Now();
            StartupTrace.Complete(cat, original.DeclaringType?.Name + "." + original.Name, start, end);
            if (cat == "game" && !_menuReady && original.DeclaringType == typeof(FejdStartup) && original.Name == "Start")
            {
                _menuReady = true;
                StartupTrace.Mark("main menu ready (FejdStartup.Start end)");
                Log.Guard("MenuReady handlers", () => MenuReady?.Invoke());
            }
        }

        private static void SceneLoaderStartPrefix()
        {
            _sceneRequest = StartupTrace.Now();
            StartupTrace.Mark("SceneLoader.Start (scene requested)");
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!StartupTrace.Recording)
            {
                return;
            }
            StartupTrace.Mark("scene loaded: " + scene.name, mode.ToString());
            if (_sceneRequest != 0)
            {
                StartupTrace.Complete("scene", "load " + scene.name, _sceneRequest, StartupTrace.Now(),
                    "SceneLoader.Start -> sceneLoaded (includes logos, platform init and scene Awake calls)",
                    tid: StartupTrace.LaneScenes);
                _sceneRequest = 0;
            }
        }

        private static void OnActiveSceneChanged(Scene from, Scene to)
        {
            if (StartupTrace.Recording)
            {
                StartupTrace.Mark("active scene: " + to.name);
            }
        }
    }
}
