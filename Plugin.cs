#nullable enable
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System.Reflection;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Game;
using Game.Audio;
using Game.Net;
using Game.Prefabs;
using Game.Common;
using Game.Tools;
using Game.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Colossal.Mathematics;
using Colossal.Serialization.Entities;
using Unity.Collections;

namespace OSMImporter
{
    [BepInPlugin("com.omega.osmimporter", "OSM Importer", "1.12.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log = null!;
        private static bool _hasImported;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo("=== OSM Importer v1.12.0 - Surface Snapping ===");
            new Harmony("com.omega.osmimporter").PatchAll(Assembly.GetExecutingAssembly());
        }

        [HarmonyPatch(typeof(AudioManager), "OnGameLoadingComplete")]
        public class AudioManager_OnGameLoadingComplete_Patch
        {
            static void Postfix(AudioManager __instance, GameMode mode)
            {
                if (mode == GameMode.MainMenu || _hasImported) return;

                string desktop = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop);
                string filePath = Path.Combine(desktop, "test.osm");

                if (!File.Exists(filePath)) return;

                try
                {
                    OsmLoader.LoadFromRawString(File.ReadAllText(filePath));
                    if (OsmLoader.Roads.Count > 0)
                    {
                        RoadBuilder.BuildRoads(OsmLoader.Roads, __instance.World);
                        _hasImported = true;
                    }
                }
                catch (System.Exception ex) { Log.LogError($"Build Error: {ex}"); }
            }
        }
    }

    public static class RoadBuilder
    {
        public static void BuildRoads(List<OsmRoad> roads, World world)
        {
            var em = world.EntityManager;
            var prefabSystem = world.GetOrCreateSystemManaged<PrefabSystem>();

            // Get Terrain Data to snap roads to surface
            var terrainSystem = world.GetOrCreateSystemManaged<TerrainSystem>();
            var terrainData = terrainSystem.GetHeightData();

            Entity roadPrefab = Entity.Null;
            var query = em.CreateEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<NetData>());
            using (var entities = query.ToEntityArray(Allocator.TempJob))
            {
                foreach (var e in entities)
                {
                    if (prefabSystem.TryGetPrefab<NetPrefab>(e, out var p))
                    {
                        if (p.name == "Small Road") { roadPrefab = e; break; }
                    }
                }
            }

            if (roadPrefab == Entity.Null) return;

            int count = 0;
            foreach (var road in roads)
            {
                if (road.Points.Count < 2) continue;

                float3 currentStart = road.Points[0];
                float accumulatedDist = 0;

                for (int i = 1; i < road.Points.Count; i++)
                {
                    float3 nextPoint = road.Points[i];
                    float d = math.distance(currentStart, nextPoint);
                    accumulatedDist += d;

                    if (accumulatedDist > 40f || i == road.Points.Count - 1)
                    {
                        if (math.distance(currentStart, nextPoint) > 2.0f)
                        {

                            float3 start = currentStart;
                            float3 end = nextPoint;

                            // SNAP TO TERRAIN: Sample height at these coordinates
                            if (terrainData.isCreated)
                            {
                                start.y = TerrainUtils.SampleHeight(ref terrainData, start);
                                end.y = TerrainUtils.SampleHeight(ref terrainData, end);
                            }
                            else
                            {
                                // FALLBACK: If terrain isn't ready, put them at Y=200 so they stay visible
                                start.y = 200f;
                                end.y = 200f;
                            }

                            CreateSegment(em, roadPrefab, start, end);
                            count++;
                        }
                        currentStart = nextPoint;
                        accumulatedDist = 0;
                    }
                }
            }
            Plugin.Log.LogInfo($"PLACED {count} SEGMENTS. They should be on the surface now!");
        }

        private static void CreateSegment(EntityManager em, Entity prefab, float3 start, float3 end)
        {
            Entity e = em.CreateEntity();
            float3 dir = math.normalize(end - start);
            float dist = math.distance(start, end);

            em.AddComponentData(e, new NetCourse
            {
                m_Curve = new Bezier4x3(start, start + dir * (dist * 0.33f), end - dir * (dist * 0.33f), end),
                m_StartPosition = new() { m_Position = start, m_Rotation = quaternion.LookRotation(dir, math.up()) },
                m_EndPosition = new() { m_Position = end, m_Rotation = quaternion.LookRotation(dir, math.up()) }
            });
            em.AddComponentData(e, new PrefabRef { m_Prefab = prefab });
            em.AddComponentData(e, new CreationDefinition { m_Prefab = prefab, m_Flags = CreationFlags.Permanent });
            em.AddComponent<Updated>(e);
        }
    }

    public class OsmRoad { public List<float3> Points = new(); }

    public static class OsmLoader
    {
        public static List<OsmRoad> Roads = new();

        public static void LoadFromRawString(string content)
        {
            Roads.Clear();
            MatchCollection lats = Regex.Matches(content, "\"lat\":\\s*([\\d.-]+)");
            MatchCollection lons = Regex.Matches(content, "\"lon\":\\s*([\\d.-]+)");
            if (lats.Count == 0) return;

            float sumLat = 0, sumLon = 0;
            for (int i = 0; i < lats.Count; i++)
            {
                sumLat += float.Parse(lats[i].Groups[1].Value);
                sumLon += float.Parse(lons[i].Groups[1].Value);
            }
            float refLat = sumLat / lats.Count;
            float refLon = sumLon / lons.Count;
            float metersPerLat = 111320f;
            float metersPerLon = metersPerLat * math.cos(math.radians(refLat));

            string[] ways = content.Split(new string[] { "\"type\": \"way\"" }, System.StringSplitOptions.None);

            for (int i = 1; i < ways.Length; i++)
            {
                string wayData = ways[i];
                if (wayData.Contains("\"highway\": \"path\"") ||
                    wayData.Contains("\"highway\": \"footway\"") ||
                    wayData.Contains("\"highway\": \"cycleway\"")) continue;

                MatchCollection wLats = Regex.Matches(wayData, "\"lat\":\\s*([\\d.-]+)");
                MatchCollection wLons = Regex.Matches(wayData, "\"lon\":\\s*([\\d.-]+)");

                var road = new OsmRoad();
                for (int j = 0; j < wLats.Count; j++)
                {
                    float x = (float.Parse(wLons[j].Groups[1].Value) - refLon) * metersPerLon;
                    float z = (float.Parse(wLats[j].Groups[1].Value) - refLat) * metersPerLat;
                    road.Points.Add(new float3(x, 0, z));
                }
                if (road.Points.Count > 1) Roads.Add(road);
            }
            Plugin.Log.LogInfo($"PARSED: {Roads.Count} roads.");
        }
    }
}