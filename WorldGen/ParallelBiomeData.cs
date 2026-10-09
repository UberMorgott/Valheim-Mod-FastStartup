using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FastStartup.Core;
using HarmonyLib;
using UnityEngine;

namespace FastStartup.WorldGen
{
    /// <summary>
    /// <c>AltBiomeWorldData.GenerateBiomePoints</c> (ValheimDecompiled-1.0.16 AltBiomeWorldData.cs:99-130) fills a 2048x2048
    /// biome + height map cell by cell on the main thread, on every server world load (ZNet.cs:464) and every client
    /// connect (ZNet.cs:1117), through <c>VerifyBiomeData</c>. Each cell is <c>WorldGenerator.GetBiome(x, y)</c> +
    /// <c>GetBiomeHeight(biome, x, y)</c>: pure functions of the world seed (Perlin noise via the thread-safe native
    /// <c>Mathf.PerlinNoise</c>, FastNoise reading only its settings, river lookup through a read-only dictionary plus a
    /// one-grid cache under <c>m_riverCacheLock</c>, WorldGenerator.cs:620; the HeightmapBuilder thread already calls
    /// them concurrently with the main thread, HeightmapBuilder.cs:159-199). <c>GetBiomeSector</c> inside GetBiomeHeight
    /// only reads the previous map, which is replaced at the end as in vanilla.
    /// The replacement runs the same loop body per row on all cores (each row writes only its own cells) and assigns the
    /// result exactly like vanilla. Falls back to vanilla while a foreign patch sits on any method of the generator path
    /// (WorldGenerator, DUtils, FastNoise, BiomeHelpers, the AltBiomeWorldData helpers), a foreign transpiler on
    /// GenerateBiomePoints, or when another prefix already skipped it; any exception in a worker reruns vanilla.
    /// </summary>
    internal static class ParallelBiomeData
    {
        private static AccessTools.FieldRef<AltBiomeWorldData, World> _world;
        private static MethodInfo _target;
        private static List<MethodBase> _generatorPath;

        public static long LastMs = -1;
        public static bool LastParallel;

        public static void Install(Harmony harmony)
        {
            _target = AccessTools.DeclaredMethod(typeof(AltBiomeWorldData), nameof(AltBiomeWorldData.GenerateBiomePoints), new[] { typeof(World) });
            MethodInfo getBiome = AccessTools.DeclaredMethod(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome),
                new[] { typeof(float), typeof(float), typeof(float), typeof(bool) });
            MethodInfo getBiomeHeight = AccessTools.DeclaredMethod(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeHeight));
            if (_target == null || getBiome == null || getBiomeHeight == null || AccessTools.DeclaredField(typeof(AltBiomeWorldData), "m_world") == null)
            {
                Log.Warning("ParallelBiomeData: AltBiomeWorldData.GenerateBiomePoints / WorldGenerator.GetBiome / GetBiomeHeight / m_world not found (game update?), module off");
                return;
            }
            _world = AccessTools.FieldRefAccess<AltBiomeWorldData, World>("m_world");
            _generatorPath = new List<MethodBase>();
            foreach (Type type in new[] { typeof(WorldGenerator), typeof(DUtils), typeof(FastNoise), typeof(BiomeHelpers) })
            {
                _generatorPath.AddRange(AccessTools.GetDeclaredMethods(type).Where(m => !m.IsAbstract && !m.ContainsGenericParameters));
                _generatorPath.AddRange(AccessTools.GetDeclaredConstructors(type));
            }
            _generatorPath.AddRange(AccessTools.GetDeclaredMethods(typeof(AltBiomeWorldData))
                .Where(m => m.Name == nameof(AltBiomeWorldData.MapSpaceToWorldSpace) || m.Name == nameof(AltBiomeWorldData.dbStep)));
            _generatorPath.AddRange(AccessTools.GetDeclaredConstructors(typeof(AltBiomeWorldData)));
            harmony.Patch(_target, prefix: new HarmonyMethod(AccessTools.Method(typeof(ParallelBiomeData), nameof(Prefix))));
        }

        /// <summary>Replacing prefix; postfixes of other mods still run after it.</summary>
        private static bool Prefix(World world, bool __runOriginal)
        {
            LastParallel = false;
            WorldGenerator generator = WorldGenerator.instance;
            if (!__runOriginal || world == null || generator == null)
            {
                return true;
            }
            string foreign = PatchGuard.Foreign(_target, bodyOnly: true) ?? PatchGuard.Foreign(_generatorPath);
            if (foreign != null)
            {
                Log.WarningOnce("parallel-biome-foreign:" + foreign, $"ParallelBiomeData: {foreign}, vanilla biome map build used");
                return true;
            }
            Stopwatch watch = Stopwatch.StartNew();
            const int size = AltBiomeWorldData.c_textureSize;
            var data = new AltBiomeWorldData(size) { dt = DateTime.Now };
            try
            {
                Parallel.For(0, size, i => Row(generator, data, i));
            }
            catch (Exception e)
            {
                Log.Warning($"ParallelBiomeData: parallel build failed, vanilla rerun: {(e as AggregateException)?.InnerException ?? e}");
                return true;
            }
            data.PointsGenerated = true;
            world.m_biomeData = data;
            _world(data) = world;
            LastMs = watch.ElapsedMilliseconds;
            LastParallel = true;
            return false;
        }

        /// <summary>One vanilla loop row (AltBiomeWorldData.cs:106-124), same calls with the same default arguments.</summary>
        private static void Row(WorldGenerator generator, AltBiomeWorldData data, int i)
        {
            Heightmap.BiomeIndex ocean = Heightmap.Biome.Ocean.ToBiomeIndex();
            for (int j = 0; j < AltBiomeWorldData.c_textureSize; j++)
            {
                Vector2 vector = new Vector2
                {
                    x = AltBiomeWorldData.MapSpaceToWorldSpace(j),
                    y = AltBiomeWorldData.MapSpaceToWorldSpace(i),
                };
                if (vector.sqrMagnitude > 110250000f)
                {
                    data.PointBiomes[j, i] = ocean;
                    data.PointHeights[j, i] = -1000f;
                }
                else
                {
                    Heightmap.Biome biome = generator.GetBiome(vector.x, vector.y);
                    data.PointBiomes[j, i] = biome.ToBiomeIndex();
                    data.PointHeights[j, i] = generator.GetBiomeHeight(biome, vector.x, vector.y, out Color _);
                }
            }
        }
    }
}
