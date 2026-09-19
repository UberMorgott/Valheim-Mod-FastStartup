using System;
using System.Linq;
using System.Reflection;
using FastStartup.Core;
using HarmonyLib;

namespace FastStartup.Profiling
{
    /// <summary>
    /// Optional Jotunn phases: item/piece registration into ObjectDB (the menu ObjectDB is filled through
    /// <c>ObjectDB.CopyOtherDB</c>). Installed after Chainloader.Start, when Jotunn's managers are initialized:
    /// patching them earlier forces their static constructors before Jotunn is ready (observed:
    /// TypeInitializationException in <c>AssetManager</c>, which breaks Jotunn for the session). Members verified in
    /// the installed Jotunn 2.30.1 (ilspycmd): <c>ItemManager.RegisterCustomDataFejd(ObjectDB, ObjectDB)</c>,
    /// <c>ItemManager.RegisterCustomData(ObjectDB)</c>, <c>PieceManager.RegisterCustomData(ObjectDB)</c>.
    /// </summary>
    internal static class JotunnProbe
    {
        private static readonly SpanStack Spans = new SpanStack();

        public static void Install(Harmony harmony)
        {
            Assembly jotunn = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Jotunn");
            if (jotunn == null)
            {
                return;
            }
            int hooked = 0;
            hooked += Hook(harmony, jotunn, "Jotunn.Managers.ItemManager", "RegisterCustomDataFejd");
            hooked += Hook(harmony, jotunn, "Jotunn.Managers.ItemManager", "RegisterCustomData");
            hooked += Hook(harmony, jotunn, "Jotunn.Managers.PieceManager", "RegisterCustomData");
            Log.Info($"Jotunn probe: {hooked} Jotunn methods hooked");
        }

        private static int Hook(Harmony harmony, Assembly assembly, string type, string method)
        {
            // Instance method only: ItemManager/PieceManager also declare static Harmony forwarders of the same name.
            MethodInfo target = assembly.GetType(type)?
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .FirstOrDefault(m => m.Name == method);
            if (target == null)
            {
                Log.Warning($"Jotunn probe: {type}.{method} not found, skipped");
                return 0;
            }
            harmony.Patch(target,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(JotunnProbe), nameof(Prefix)), Priority.First),
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(JotunnProbe), nameof(Finalizer)), Priority.Last));
            return 1;
        }

        private static void Prefix() => Spans.Push();

        private static void Finalizer(MethodBase __originalMethod)
        {
            long start = Spans.Pop();
            if (start != 0)
            {
                StartupTrace.Complete("jotunn", __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name,
                    start, StartupTrace.Now());
            }
        }
    }
}
