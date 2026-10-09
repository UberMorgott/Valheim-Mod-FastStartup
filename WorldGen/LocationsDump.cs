using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using FastStartup.Core;

namespace FastStartup.WorldGen
{
    /// <summary>
    /// <c>[WorldGen] DumpLocations</c>: at LocationsGenerated writes <c>BepInEx\FastStartup\diag\locations-&lt;seed&gt;.txt</c>:
    /// the SHA-256 of the biome map (<c>PointBiomes</c> as int32, <c>PointHeights</c> as float bits, both in [x, y] order)
    /// and every <c>ZoneSystem.m_locationInstances</c> entry sorted by prefab name, then zone, with the position as raw
    /// float bits. Nothing in it depends on timing, so two runs of the same seed must give the same file byte for byte.
    /// </summary>
    internal static class LocationsDump
    {
        public static void Write(ZoneSystem zs)
        {
            World world = ZNet.World;
            WorldGenerator generator = WorldGenerator.instance;
            if (world == null || generator == null)
            {
                return;
            }
            var sb = new StringBuilder();
            sb.Append("world ").Append(world.m_name).Append(" seed ").Append(world.m_seedName).Append(' ')
                .Append(generator.GetSeed().ToString(CultureInfo.InvariantCulture)).Append(" worldgen ")
                .Append(world.m_worldGenVersion.ToString(CultureInfo.InvariantCulture)).Append('\n');
            AltBiomeWorldData data = world.m_biomeData;
            if (data?.PointBiomes != null && data.PointHeights != null)
            {
                sb.Append("biomes ").Append(HashBiomes(data.PointBiomes)).Append('\n');
                sb.Append("heights ").Append(HashHeights(data.PointHeights)).Append('\n');
            }
            else
            {
                sb.Append("biome map missing\n");
            }
            var rows = new List<(string Name, short X, short Y, string Line)>();
            foreach (KeyValuePair<Vector2s, ZoneSystem.LocationInstance> entry in zs.m_locationInstances)
            {
                ZoneSystem.LocationInstance instance = entry.Value;
                string name = instance.m_location?.m_prefab.Name ?? "<null>";
                rows.Add((name, entry.Key.x, entry.Key.y, string.Format(CultureInfo.InvariantCulture,
                    "{0}\t{1},{2}\t{3:x8},{4:x8},{5:x8}\t{6}", name, entry.Key.x, entry.Key.y, Bits(instance.m_position.x),
                    Bits(instance.m_position.y), Bits(instance.m_position.z), instance.m_placed ? "placed" : "-")));
            }
            sb.Append("locations ").Append(rows.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (var row in rows.OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.X).ThenBy(r => r.Y))
            {
                sb.Append(row.Line).Append('\n');
            }
            string path = Path.Combine(Paths.BepInExRootPath, "FastStartup", "diag",
                "locations-" + generator.GetSeed().ToString(CultureInfo.InvariantCulture) + ".txt");
            AtomicFile.WriteAllText(path, sb.ToString());
            Log.Info($"WorldGen: {rows.Count} location instances dumped to {path}");
        }

        private static uint Bits(float value) => BitConverter.ToUInt32(BitConverter.GetBytes(value), 0);

        private static string HashBiomes(Heightmap.BiomeIndex[,] map)
        {
            int w = map.GetLength(0), h = map.GetLength(1);
            var bytes = new byte[w * h * 4];
            int k = 0;
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    int v = (int)map[x, y];
                    bytes[k++] = (byte)v;
                    bytes[k++] = (byte)(v >> 8);
                    bytes[k++] = (byte)(v >> 16);
                    bytes[k++] = (byte)(v >> 24);
                }
            }
            return Sha(bytes);
        }

        private static string HashHeights(float[,] map)
        {
            var bytes = new byte[map.Length * 4];
            Buffer.BlockCopy(map, 0, bytes, 0, bytes.Length);
            return Sha(bytes);
        }

        private static string Sha(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
