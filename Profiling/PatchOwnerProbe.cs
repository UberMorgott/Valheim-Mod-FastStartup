using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using FastStartup.Core;
using HarmonyLib;

namespace FastStartup.Profiling
{
    /// <summary>
    /// Opt-in (<c>[Profiler] TimeModPatches</c>): splits the "mods' patches" part of the hooked game methods by
    /// owner. After Chainloader.Start every prefix/postfix/finalizer that another Harmony ID put on a
    /// <see cref="GameLifecycleProbe"/> target is itself patched with a First prefix / Last finalizer timing pair.
    /// Category <c>game.patch</c>, name = owner Harmony ID + [assembly] (helper IDs such as
    /// org.bepinex.helpers.ItemManager are shared by many mods), detail = patch method, detail2 = patched game method.
    /// Crash guard: detouring forces Mono to compile the patch method early, and for one method here
    /// (Warfare's ItemManager.Item.Patch_FejdStartup) that native compile crashed the process in
    /// <c>RuntimeMethodHandle.GetFunctionPointer</c>, which no managed catch can stop. The method being detoured is
    /// written to <c>patch-probe.pending</c> first; if the process dies, the next launch moves it to
    /// <c>patch-probe.skip</c> and never touches it again.
    /// Limits: transpiler cost is inside the game body; a tiny patch method the JIT inlined into an already built
    /// wrapper is not seen (its cost is negligible by definition).
    /// </summary>
    internal static class PatchOwnerProbe
    {
        private static readonly Dictionary<MethodBase, (string Owner, string Target)> Owners =
            new Dictionary<MethodBase, (string, string)>();

        [ThreadStatic] private static SpanStack _spans;

        private static string Dir => Path.Combine(Paths.BepInExRootPath, "FastStartup");

        public static void Install(Harmony harmony)
        {
            string pendingPath = Path.Combine(Dir, "patch-probe.pending");
            string skipPath = Path.Combine(Dir, "patch-probe.skip");
            var skip = new HashSet<string>(File.Exists(skipPath) ? File.ReadAllLines(skipPath) : new string[0]);
            if (File.Exists(pendingPath))
            {
                string crashed = File.ReadAllText(pendingPath).Trim();
                if (crashed.Length > 0 && skip.Add(crashed))
                {
                    File.AppendAllText(skipPath, crashed + Environment.NewLine);
                    Log.Warning($"Patch owner probe: the last launch died while hooking {crashed}; it is skipped from now on");
                }
                File.Delete(pendingPath);
            }

            int wrapped = 0;
            foreach (MethodInfo target in GameLifecycleProbe.Targets().Where(m => m != null))
            {
                Patches info = Harmony.GetPatchInfo(target);
                if (info == null)
                {
                    continue;
                }
                foreach (Patch patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers))
                {
                    MethodInfo method = patch.PatchMethod;
                    string id = Identity(method);
                    if (patch.owner.StartsWith("morgott.faststartup", StringComparison.Ordinal) || Owners.ContainsKey(method) || skip.Contains(id))
                    {
                        continue;
                    }
                    try
                    {
                        File.WriteAllText(pendingPath, id);
                        harmony.Patch(method,
                            prefix: new HarmonyMethod(AccessTools.Method(typeof(PatchOwnerProbe), nameof(Prefix)), Priority.First),
                            finalizer: new HarmonyMethod(AccessTools.Method(typeof(PatchOwnerProbe), nameof(Finalizer)), Priority.Last));
                        Owners[method] = (patch.owner + " [" + method.Module.Assembly.GetName().Name + "]", target.DeclaringType?.Name + "." + target.Name);
                        wrapped++;
                    }
                    catch (Exception e)
                    {
                        Log.Warning($"Patch owner probe: cannot time {id} ({patch.owner}): {e.Message}");
                    }
                }
            }
            File.Delete(pendingPath);
            Log.Info($"Patch owner probe: {wrapped} mod patch methods on game startup methods timed, {skip.Count} skipped");
        }

        /// <summary>Stable across launches of the same build: assembly name + MVID + metadata token + readable name.</summary>
        private static string Identity(MethodInfo method) =>
            method.Module.Assembly.GetName().Name + "|" + method.Module.ModuleVersionId.ToString("N") + "|" +
            method.MetadataToken.ToString("x8") + "|" + HarmonyProfiler.Describe(method);

        private static void Prefix()
        {
            (_spans ?? (_spans = new SpanStack())).Push();
        }

        private static void Finalizer(MethodBase __originalMethod)
        {
            long start = _spans?.Pop() ?? 0;
            if (start == 0 || !Owners.TryGetValue(__originalMethod, out (string Owner, string Target) info))
            {
                return;
            }
            StartupTrace.Complete("game.patch", info.Owner, start, StartupTrace.Now(), HarmonyProfiler.Describe(__originalMethod), info.Target);
        }
    }
}
