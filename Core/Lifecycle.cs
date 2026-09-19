using System;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace FastStartup.Core
{
    /// <summary>
    /// Startup milestones for modules that do not depend on the profiler. <see cref="ChainloaderInitialized"/> =
    /// end of <c>Chainloader.Initialize</c> (Unity is up, game assemblies resolvable, no plugin loaded yet);
    /// <see cref="MenuReady"/> = end of the first <c>FejdStartup.Start</c> (FejdStartup.cs:427, builds the main menu).
    /// Both are raised on the Unity main thread.
    /// </summary>
    internal static class Lifecycle
    {
        private static Harmony _harmony;
        private static bool _menuReady;

        public static event Action ChainloaderInitialized;

        public static event Action MenuReady;

        /// <summary>Patcher Finish: patches only a BepInEx method, so no Unity/game type initializer runs yet.</summary>
        public static void Install(Harmony harmony)
        {
            _harmony = harmony;
            harmony.Patch(AccessTools.Method(typeof(Chainloader), nameof(Chainloader.Initialize)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(Lifecycle), nameof(InitializePostfix))));
        }

        private static void InitializePostfix()
        {
            Log.Guard("FejdStartup.Start hook", () => _harmony.Patch(AccessTools.DeclaredMethod(typeof(FejdStartup), "Start"),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(Lifecycle), nameof(FejdStartupPostfix)))));
            Log.Guard("ChainloaderInitialized handlers", () => ChainloaderInitialized?.Invoke());
        }

        private static void FejdStartupPostfix()
        {
            if (_menuReady)
            {
                return;
            }
            _menuReady = true;
            Log.Guard("MenuReady handlers", () => MenuReady?.Invoke());
        }
    }
}
