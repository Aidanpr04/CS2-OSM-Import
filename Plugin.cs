#nullable enable
using BepInEx;
using BepInEx.Logging;
using Colossal.Mathematics;
using Game;
using Game.Audio;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using HarmonyLib;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace OSMImporter
{
    public enum HighwayType
    {
        Motorway, Trunk, Primary, Secondary, Tertiary, Residential, Service, Unknown
    }

    public class OsmRoad
    {
        public List<float3> Points = new();
        public List<float3> RawPoints = new();
        public HighwayType Highway = HighwayType.Unknown;
    }

    // Represents a single processed segment ready for building
    public struct Segment
    {
        public float3 Start, End;
        public float2 Dir;        // normalised XZ direction
        public float2 Mid;        // XZ midpoint
        public HighwayType Highway;
    }

    [BepInPlugin("com.omega.osmimporter", "OSM Importer", "1.24.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log = null!;
        private static readonly HashSet<int> _importedWorlds = new();

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo("=== OSM Importer v1.24.0 ===");
            try
            {
                new Harmony("com.omega.osmimporter").PatchAll(Assembly.GetExecutingAssembly());
                Log.LogInfo("Harmony patched OK.");
            }
            catch (System.Exception ex)
            {
                Log.LogError($"Harmony patch failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }

        [HarmonyPatch(typeof(AudioManager), "OnGameLoadingComplete")]
        public class Patch
        {
            static void Postfix(AudioManager __instance, GameMode mode)
            {
                Plugin.Log.LogInfo($"OnGameLoadingComplete fired. Mode={mode}");
                if (mode != GameMode.Editor) return;

                int worldId = __instance.World.GetHashCode();
                if (_importedWorlds.Contains(worldId))
                {
                    Plugin.Log.LogInfo("Already imported for this world — skipping.");
                    return;
                }

                string filePath = Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop),
                    "test.osm");

                if (!File.Exists(filePath))
                {
                    Plugin.Log.LogWarning("test.osm not found on Desktop.");
                    return;
                }

                try
                {
                    OsmLoader.LoadFromRawString(File.ReadAllText(filePath));
                    Plugin.Log.LogInfo($"Parsed {OsmLoader.Roads.Count} roads.");

                    if (OsmLoader.Roads.Count > 0)
                    {
                        RoadBuilder.BuildRoads(OsmLoader.Roads, __instance.World);
                        _importedWorlds.Add(worldId);
                        Plugin.Log.LogInfo("Import complete.");
                    }
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogError($"FATAL: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }
    }

    public static class RoadBuilder
    {
        private const float MIN_LENGTH_M = 8f;
        private const float MAX_LENGTH_M = 500f;
        private const float NODE_SNAP_M = 4f;

        // Per-type overlap radius (metres) based on CS2 road width.
        // A segment whose midpoint is within this distance laterally of an
        // already-placed segment of the same type is suppressed as an overlap.
        //   Highway    ~15m wide  -> 16m
        //   Large Road ~12m wide  -> 13m
        //   Medium Road ~9m wide  -> 10m
        //   Small Road  ~6m wide  ->  7m
        private static readonly Dictionary<HighwayType, float> OverlapRadius = new()
        {
            { HighwayType.Motorway,    16f },
            { HighwayType.Trunk,       13f },
            { HighwayType.Primary,     13f },
            { HighwayType.Secondary,   10f },
            { HighwayType.Tertiary,     7f },
            { HighwayType.Residential,  7f },
            { HighwayType.Service,      5f },
            { HighwayType.Unknown,      7f },
        };

        // Dual carriageway detection thresholds
        // Two segments are considered the same road (dual carriageway) when:
        //   - Their midpoints are within DUAL_LATERAL_M of each other laterally
        //   - They are parallel but opposite direction (dot product < DUAL_DIR_DOT)
        //   - Their lengths are within DUAL_LENGTH_DIFF_M of each other
        private const float DUAL_LATERAL_M = 20f;  // max side separation in metres
        private const float DUAL_DIR_DOT = -0.8f; // anti-parallel threshold
        private const float DUAL_LENGTH_DIFF = 0.5f;  // max length ratio difference

        // Spatial grid cell size for the segment lookup (metres)
        // Should be >= DUAL_LATERAL_M so nearby segments are always in adjacent cells
        private const float GRID_CELL_M = 25f;

        private static readonly Dictionary<HighwayType, string> PrefabNames = new()
        {
            { HighwayType.Motorway,    "Highway"     },
            { HighwayType.Trunk,       "Large Road"  },
            { HighwayType.Primary,     "Large Road"  },
            { HighwayType.Secondary,   "Medium Road" },
            { HighwayType.Tertiary,    "Small Road"  },
            { HighwayType.Residential, "Small Road"  },
            { HighwayType.Service,     "Small Road"  },
            { HighwayType.Unknown,     "Small Road"  },
        };

        public static void BuildRoads(List<OsmRoad> roads, World world)
        {
            var em = world.EntityManager;
            var prefabSystem = world.GetOrCreateSystemManaged<PrefabSystem>();

            // ---- Discover prefabs -------------------------------------------
            var prefabLookup = new Dictionary<string, Entity>();
            var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<PrefabData>(),
                ComponentType.ReadOnly<NetData>());

            using (var entities = query.ToEntityArray(Allocator.TempJob))
                foreach (var e in entities)
                    if (prefabSystem.TryGetPrefab<NetPrefab>(e, out var p))
                        prefabLookup[p.name] = e;
            query.Dispose();

            var prefabForType = new Dictionary<HighwayType, Entity>();
            Entity fallback = Entity.Null;

            foreach (var kv in PrefabNames)
            {
                if (prefabLookup.TryGetValue(kv.Value, out Entity e))
                {
                    prefabForType[kv.Key] = e;
                    if (kv.Key == HighwayType.Residential) fallback = e;
                }
                else
                    Plugin.Log.LogWarning($"Prefab '{kv.Value}' not found for {kv.Key}.");
            }

            if (fallback == Entity.Null && prefabLookup.TryGetValue("Small Road", out Entity sr))
                fallback = sr;
            if (fallback == Entity.Null) { Plugin.Log.LogError("No usable road prefab — aborting."); return; }

            // ---- Terrain ----------------------------------------------------
            var terrainSystem = world.GetOrCreateSystemManaged<TerrainSystem>();
            var terrainData = terrainSystem.GetHeightData();
            bool hasTerrain = terrainData.isCreated;

            // ---- Step 1: Flatten all roads into candidate segments ----------
            var candidates = new List<Segment>();

            foreach (var road in roads)
            {
                for (int i = 0; i < road.Points.Count - 1; i++)
                {
                    float3 s = road.Points[i];
                    float3 e = road.Points[i + 1];

                    float dist = math.distance(new float2(s.x, s.z), new float2(e.x, e.z));
                    if (dist < MIN_LENGTH_M || dist > MAX_LENGTH_M) continue;

                    float2 dir = math.normalize(new float2(e.x - s.x, e.z - s.z));
                    float2 mid = new float2((s.x + e.x) * 0.5f, (s.z + e.z) * 0.5f);

                    candidates.Add(new Segment
                    {
                        Start = s,
                        End = e,
                        Dir = dir,
                        Mid = mid,
                        Highway = road.Highway
                    });
                }
            }

            Plugin.Log.LogInfo($"Candidate segments before dual-carriageway filter: {candidates.Count}");

            // ---- Step 2: Build spatial grid of midpoints --------------------
            // Grid maps cell -> list of segment indices in that cell
            float gridScale = 1f / GRID_CELL_M;
            var grid = new Dictionary<(int, int), List<int>>();

            for (int i = 0; i < candidates.Count; i++)
            {
                var seg = candidates[i];
                var cell = GridCell(seg.Mid, gridScale);
                if (!grid.TryGetValue(cell, out var list))
                    grid[cell] = list = new List<int>();
                list.Add(i);
            }

            // ---- Step 3: For each segment, check for a parallel opposite ----
            // If found, replace both with a single centreline segment
            var suppressed = new HashSet<int>(); // indices to skip
            var centrelines = new List<Segment>(); // new merged segments

            for (int i = 0; i < candidates.Count; i++)
            {
                if (suppressed.Contains(i)) continue;

                var a = candidates[i];
                int best = -1;
                float bestScore = float.MaxValue;

                // Check a 3x3 neighbourhood of grid cells
                var cellA = GridCell(a.Mid, gridScale);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        var neighbourCell = (cellA.Item1 + dx, cellA.Item2 + dz);
                        if (!grid.TryGetValue(neighbourCell, out var neighbours)) continue;

                        foreach (int j in neighbours)
                        {
                            if (j <= i || suppressed.Contains(j)) continue;

                            var b = candidates[j];

                            // Must be same road class
                            if (b.Highway != a.Highway) continue;

                            // Must be anti-parallel
                            float dot = a.Dir.x * b.Dir.x + a.Dir.y * b.Dir.y;
                            if (dot > DUAL_DIR_DOT) continue;

                            // Lateral separation: project midpoint offset onto
                            // the perpendicular of A's direction
                            float2 perp = new float2(-a.Dir.y, a.Dir.x);
                            float2 midDiff = b.Mid - a.Mid;
                            float lateral = math.abs(math.dot(midDiff, perp));
                            if (lateral > DUAL_LATERAL_M) continue;

                            // Along-track offset: should be small (segments run alongside)
                            float along = math.abs(math.dot(midDiff, a.Dir));
                            if (along > DUAL_LATERAL_M * 2f) continue;

                            // Similar length
                            float lenA = math.distance(new float2(a.Start.x, a.Start.z),
                                                           new float2(a.End.x, a.End.z));
                            float lenB = math.distance(new float2(b.Start.x, b.Start.z),
                                                           new float2(b.End.x, b.End.z));
                            float lenDiff = math.abs(lenA - lenB) / math.max(math.max(lenA, lenB), 1f);
                            if (lenDiff > DUAL_LENGTH_DIFF) continue;

                            // Pick the closest match
                            float score = lateral + along;
                            if (score < bestScore) { bestScore = score; best = j; }
                        }
                    }

                if (best >= 0)
                {
                    // Suppress the secondary carriageway
                    suppressed.Add(best);

                    var b = candidates[best];

                    // Build centreline: reverse B so directions match, then average
                    float2 bDirAligned = b.Dir;
                    float3 bStart = b.Start, bEnd = b.End;
                    if (a.Dir.x * b.Dir.x + a.Dir.y * b.Dir.y < 0f)
                    {
                        // B is reversed relative to A — swap its endpoints
                        bStart = b.End;
                        bEnd = b.Start;
                    }

                    float3 cStart = (a.Start + bStart) * 0.5f;
                    float3 cEnd = (a.End + bEnd) * 0.5f;
                    float2 cDir = math.normalize(new float2(cEnd.x - cStart.x, cEnd.z - cStart.z));
                    float2 cMid = (a.Mid + b.Mid) * 0.5f;

                    // Replace segment A with the centreline
                    candidates[i] = new Segment
                    {
                        Start = cStart,
                        End = cEnd,
                        Dir = cDir,
                        Mid = cMid,
                        Highway = a.Highway
                    };
                }
            }

            int dualMerged = suppressed.Count;
            Plugin.Log.LogInfo($"Dual carriageway pairs merged: {dualMerged}. " +
                               $"Segments after merge: {candidates.Count - dualMerged}");

            // ---- Step 4: Emit ECS entities ----------------------------------
            float snapScale = 1f / NODE_SNAP_M;
            var endpointCount = new Dictionary<(int, int), int>();
            var typeCounts = new Dictionary<HighwayType, int>();

            // Overlap dedup: tracks placed segment midpoints per highway type.
            // Uses the road width as the grid cell size so any two parallel
            // segments of the same type closer than their road width are collapsed.
            var placedMidpoints = new Dictionary<HighwayType, HashSet<(int, int)>>();
            foreach (HighwayType ht in System.Enum.GetValues(typeof(HighwayType)))
                placedMidpoints[ht] = new HashSet<(int, int)>();

            int segCount = 0, skipCount = 0, errCount = 0;

            for (int i = 0; i < candidates.Count; i++)
            {
                if (suppressed.Contains(i)) continue;

                try
                {
                    var seg = candidates[i];

                    // Overlap check: use road width as the grid cell size.
                    // Two segments of the same type whose midpoints land in
                    // the same cell are physically overlapping in-game.
                    float overlapRadius = OverlapRadius.TryGetValue(seg.Highway, out float or) ? or : 7f;
                    float overlapScale = 1f / overlapRadius;
                    var overlapKey = ((int)math.round(seg.Mid.x * overlapScale),
                                      (int)math.round(seg.Mid.y * overlapScale));
                    if (!placedMidpoints[seg.Highway].Add(overlapKey))
                    { skipCount++; continue; }

                    float3 start = seg.Start;
                    float3 end = seg.End;

                    // Endpoint cluster check
                    var startKey = ((int)math.round(start.x * snapScale),
                                    (int)math.round(start.z * snapScale));
                    var endKey = ((int)math.round(end.x * snapScale),
                                    (int)math.round(end.z * snapScale));

                    endpointCount.TryGetValue(startKey, out int sCount);
                    endpointCount.TryGetValue(endKey, out int eCount);
                    if (sCount >= 2 && eCount >= 2) { skipCount++; continue; }

                    endpointCount[startKey] = sCount + 1;
                    endpointCount[endKey] = eCount + 1;

                    // Terrain sampling
                    if (hasTerrain)
                    {
                        start.y = TerrainUtils.SampleHeight(ref terrainData, start) + 0.2f;
                        end.y = TerrainUtils.SampleHeight(ref terrainData, end) + 0.2f;
                    }

                    float dist = math.distance(new float2(start.x, start.z),
                                               new float2(end.x, end.z));
                    float3 dir = math.normalize(end - start);

                    if (!prefabForType.TryGetValue(seg.Highway, out Entity roadPrefab))
                        roadPrefab = fallback;

                    Entity def = em.CreateEntity();

                    em.AddComponentData(def, new NetCourse
                    {
                        m_Curve = new Bezier4x3(
                            start,
                            start + dir * (dist * 0.33f),
                            end - dir * (dist * 0.33f),
                            end),
                        m_StartPosition = new CoursePos
                        {
                            m_Position = start,
                            m_Rotation = quaternion.LookRotation(dir, math.up()),
                            m_CourseDelta = 0f,
                            m_ParentMesh = -1
                        },
                        m_EndPosition = new CoursePos
                        {
                            m_Position = end,
                            m_Rotation = quaternion.LookRotation(dir, math.up()),
                            m_CourseDelta = 1f,
                            m_ParentMesh = -1
                        },
                        m_Length = dist,
                        m_FixedIndex = -1
                    });

                    em.AddComponentData(def, new CreationDefinition
                    {
                        m_Prefab = roadPrefab,
                        m_Flags = CreationFlags.Permanent
                    });

                    em.AddComponent<Updated>(def);

                    segCount++;
                    typeCounts.TryGetValue(seg.Highway, out int tc);
                    typeCounts[seg.Highway] = tc + 1;
                }
                catch (System.Exception ex)
                {
                    errCount++;
                    if (errCount <= 5)
                        Plugin.Log.LogError($"Segment {i} error: {ex.GetType().Name}: {ex.Message}");
                }
            }

            Plugin.Log.LogInfo($"Created {segCount} segments. Skipped={skipCount} Errors={errCount}");
            foreach (var kv in typeCounts)
                Plugin.Log.LogInfo($"  {kv.Key}: {kv.Value}");
        }

        private static (int, int) GridCell(float2 pos, float scale) =>
            ((int)math.floor(pos.x * scale), (int)math.floor(pos.y * scale));
    }

    public static class OsmLoader
    {
        public static List<OsmRoad> Roads = new();

        private static HighwayType ClassifyHighway(string tag) => tag switch
        {
            "motorway" or "motorway_link" => HighwayType.Motorway,
            "trunk" or "trunk_link" => HighwayType.Trunk,
            "primary" or "primary_link" => HighwayType.Primary,
            "secondary" or "secondary_link" => HighwayType.Secondary,
            "tertiary" or "tertiary_link" => HighwayType.Tertiary,
            "residential" or
            "living_street" or
            "unclassified" => HighwayType.Residential,
            "service" => HighwayType.Service,
            _ => HighwayType.Unknown
        };

        private static readonly HashSet<string> SkipTags = new()
        {
            "footway", "path", "steps", "cycleway", "pedestrian", "track"
        };

        public static void LoadFromRawString(string content)
        {
            Roads.Clear();

            MatchCollection lats = Regex.Matches(content, "\"lat\":\\s*([\\d.-]+)");
            MatchCollection lons = Regex.Matches(content, "\"lon\":\\s*([\\d.-]+)");
            if (lats.Count == 0) { Plugin.Log.LogError("No lat/lon found."); return; }

            float sumLat = 0, sumLon = 0;
            for (int i = 0; i < lats.Count; i++)
            {
                sumLat += float.Parse(lats[i].Groups[1].Value);
                sumLon += float.Parse(lons[i].Groups[1].Value);
            }
            float refLat = sumLat / lats.Count;
            float refLon = sumLon / lats.Count;

            float metersPerLat = 111320f;
            float metersPerLon = metersPerLat * math.cos(math.radians(refLat));
            Plugin.Log.LogInfo($"Centre: lat={refLat:F6} lon={refLon:F6}");

            string[] wayBlocks = content.Split(
                new string[] { "\"type\": \"way\"" },
                System.StringSplitOptions.None);

            var typeSummary = new Dictionary<string, int>();

            for (int i = 1; i < wayBlocks.Length; i++)
            {
                string wayData = wayBlocks[i];

                var hwMatch = Regex.Match(wayData, "\"highway\":\\s*\"([^\"]+)\"");
                if (!hwMatch.Success) continue;
                string hwTag = hwMatch.Groups[1].Value;

                typeSummary.TryGetValue(hwTag, out int tc);
                typeSummary[hwTag] = tc + 1;

                if (SkipTags.Contains(hwTag)) continue;

                MatchCollection wLats = Regex.Matches(wayData, "\"lat\":\\s*([\\d.-]+)");
                MatchCollection wLons = Regex.Matches(wayData, "\"lon\":\\s*([\\d.-]+)");

                var road = new OsmRoad { Highway = ClassifyHighway(hwTag) };

                for (int j = 0; j < wLats.Count; j++)
                {
                    float lat = float.Parse(wLats[j].Groups[1].Value);
                    float lon = float.Parse(wLons[j].Groups[1].Value);
                    float x = (lon - refLon) * metersPerLon;
                    float z = (lat - refLat) * metersPerLat;
                    road.Points.Add(new float3(x, 0, z));
                    road.RawPoints.Add(new float3(x, 0, z));
                }

                if (road.Points.Count > 1) Roads.Add(road);
            }

            Plugin.Log.LogInfo($"Parsed {Roads.Count} road ways. Tags:");
            foreach (var kv in typeSummary)
                Plugin.Log.LogInfo($"  highway={kv.Key}: {kv.Value}");
        }
    }
}