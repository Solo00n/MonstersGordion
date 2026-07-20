using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace MonstersGordion;

/// <summary>
/// Samples random points on the navmesh baked by NavMeshInCompanyRedux.
///
/// Defences against unreachable spawns:
///  1. Build() welds triangle vertices and unions triangles into connected
///     regions; the largest region's centre becomes the reachability anchor.
///  2. Every triangle is pre-filtered at build time by NavMesh.CalculatePath
///     to the anchor (complete path required), which removes roof patches,
///     pits and closed rooms — including one-way drops.
///
/// Vertical balance: kept triangles are classified into an "upper" tier
/// (at/above the ship landing level) and a "lower" tier (basement). The
/// caller passes the desired percentage of upper-tier spawns, so the huge
/// basement floor does not dominate the area-weighted pick.
/// </summary>
internal sealed class NavMeshSampler
{
    private sealed class Tier
    {
        public readonly List<(int a, int b, int c)> Triangles = new();
        public readonly List<float> CumulativeAreas = new();
        public float TotalArea;

        public void Add((int a, int b, int c) triangle, float area)
        {
            Triangles.Add(triangle);
            TotalArea += area;
            CumulativeAreas.Add(TotalArea);
        }
    }

    private readonly bool _requireIndoor;
    private readonly int _roofMask;
    private readonly NavMeshPath _pathCache = new NavMeshPath();

    private Vector3[] _vertices;
    private readonly Tier _upper = new();
    private readonly Tier _lower = new();
    private Vector3? _anchor;

    public Vector3? Anchor => _anchor;
    public bool HasUpperTier => _upper.Triangles.Count > 0;
    public bool HasLowerTier => _lower.Triangles.Count > 0;

    public NavMeshSampler(bool requireIndoor)
    {
        _requireIndoor = requireIndoor;
        _roofMask = BuildRoofMask();
    }

    private static int BuildRoofMask()
    {
        int mask = 0;
        foreach (string layerName in new[] { "Room", "Colliders", "Default", "MapHazards", "Terrain" })
        {
            int layer = LayerMask.NameToLayer(layerName);
            if (layer >= 0)
                mask |= 1 << layer;
        }
        return mask == 0 ? Physics.DefaultRaycastLayers : mask;
    }

    /// <summary>Rebuilds the triangle cache. Returns the number of usable triangles.</summary>
    public int Build()
    {
        _upper.Triangles.Clear(); _upper.CumulativeAreas.Clear(); _upper.TotalArea = 0f;
        _lower.Triangles.Clear(); _lower.CumulativeAreas.Clear(); _lower.TotalArea = 0f;
        _anchor = null;

        NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
        _vertices = tri.vertices;

        // Pass 1: collect candidate triangles (indoor filter, outside the ship).
        var candidates = new List<(int a, int b, int c, float area, Vector3 centroid)>();
        float candidateArea = 0f;
        for (int i = 0; i + 2 < tri.indices.Length; i += 3)
        {
            int a = tri.indices[i], b = tri.indices[i + 1], c = tri.indices[i + 2];
            Vector3 va = _vertices[a], vb = _vertices[b], vc = _vertices[c];

            float area = Vector3.Cross(vb - va, vc - va).magnitude * 0.5f;
            if (area < 0.05f)
                continue;

            Vector3 centroid = (va + vb + vc) / 3f;
            if (IsInsideShip(centroid))
                continue;
            if (_requireIndoor && !HasCeilingAbove(centroid))
                continue;

            candidates.Add((a, b, c, area, centroid));
            candidateArea += area;
        }

        if (candidates.Count == 0)
            return 0;

        // Pass 2: weld vertices on a coarse grid, union triangles into regions,
        // and place the anchor in the largest region.
        var parent = new int[candidates.Count];
        for (int i = 0; i < parent.Length; i++)
            parent[i] = i;

        var weldToTriangle = new Dictionary<long, int>();
        for (int i = 0; i < candidates.Count; i++)
        {
            foreach (int vertexIndex in new[] { candidates[i].a, candidates[i].b, candidates[i].c })
            {
                long key = WeldKey(_vertices[vertexIndex]);
                if (weldToTriangle.TryGetValue(key, out int other))
                    Union(parent, i, other);
                else
                    weldToTriangle[key] = i;
            }
        }

        var regionAreas = new Dictionary<int, float>();
        for (int i = 0; i < candidates.Count; i++)
        {
            int root = Find(parent, i);
            regionAreas.TryGetValue(root, out float sum);
            regionAreas[root] = sum + candidates[i].area;
        }

        int mainRegion = -1;
        float mainArea = 0f;
        foreach (var kv in regionAreas)
        {
            if (kv.Value > mainArea)
            {
                mainArea = kv.Value;
                mainRegion = kv.Key;
            }
        }

        float bestTriangleArea = 0f;
        Vector3 anchorCandidate = Vector3.zero;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (Find(parent, i) != mainRegion)
                continue;
            if (candidates[i].area > bestTriangleArea)
            {
                bestTriangleArea = candidates[i].area;
                anchorCandidate = candidates[i].centroid;
            }
        }
        if (NavMesh.SamplePosition(anchorCandidate, out NavMeshHit anchorHit, 5f, NavMesh.AllAreas))
            _anchor = anchorHit.position;

        // Pass 3: keep triangles from ANY region that can actually path to the
        // anchor (regions connected via ramps/links are legitimate), classify
        // them into upper/lower tiers around the ship landing level.
        float splitY = DetermineSplitY(candidates);
        int rejected = 0;
        foreach (var t in candidates)
        {
            if (!NavMesh.SamplePosition(t.centroid, out NavMeshHit hit, 3f, NavMesh.AllAreas)
                || !IsReachable(hit.position))
            {
                rejected++;
                continue;
            }
            (t.centroid.y >= splitY ? _upper : _lower).Add((t.a, t.b, t.c), t.area);
        }

        int kept = _upper.Triangles.Count + _lower.Triangles.Count;
        Plugin.Log.LogInfo(
            $"NavMeshSampler: kept {kept}/{candidates.Count} triangles " +
            $"({_upper.TotalArea + _lower.TotalArea:F0} of {candidateArea:F0} m², " +
            $"{regionAreas.Count} regions, {rejected} unreachable). " +
            $"Tiers @ splitY={splitY:F1}: upper {_upper.Triangles.Count} tris / {_upper.TotalArea:F0} m², " +
            $"lower {_lower.Triangles.Count} tris / {_lower.TotalArea:F0} m². " +
            $"Anchor={(_anchor.HasValue ? _anchor.Value.ToString("F1") : "NONE")}.");
        return kept;
    }

    /// <summary>
    /// Upper tier = at or above the ship's landing level (minus a margin);
    /// falls back to the median triangle height when the ship is unavailable.
    /// </summary>
    private static float DetermineSplitY(List<(int a, int b, int c, float area, Vector3 centroid)> candidates)
    {
        try
        {
            var sor = StartOfRound.Instance;
            if (sor != null && sor.shipBounds != null)
                return sor.shipBounds.bounds.min.y - 3f;
        }
        catch
        {
            // fall through to median
        }

        var ys = new List<float>(candidates.Count);
        foreach (var t in candidates)
            ys.Add(t.centroid.y);
        ys.Sort();
        return ys[ys.Count / 2];
    }

    /// <summary>
    /// Returns a random valid, anchor-reachable point. upperSharePercent of the
    /// rolls target the upper tier; if the rolled tier is empty, the other one
    /// is used instead.
    /// </summary>
    public Vector3? GetRandomPoint(float minPlayerDistance, int attempts, int upperSharePercent)
    {
        if (_upper.Triangles.Count == 0 && _lower.Triangles.Count == 0)
            return null;

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            bool wantUpper = UnityEngine.Random.Range(0, 100) < upperSharePercent;
            Tier tier = wantUpper ? _upper : _lower;
            if (tier.Triangles.Count == 0)
                tier = wantUpper ? _lower : _upper;

            Vector3 candidate = RandomPointInTriangle(tier);

            if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, 3f, NavMesh.AllAreas))
                continue;
            Vector3 point = hit.position;

            if (IsInsideShip(point))
                continue;
            if (_requireIndoor && !HasCeilingAbove(point))
                continue;
            if (minPlayerDistance > 0f && IsTooCloseToAnyPlayer(point, minPlayerDistance))
                continue;
            if (!IsReachable(point))
            {
                Plugin.DebugLog($"Rejected unreachable point {point:F1}.");
                continue;
            }

            return point;
        }
        return null;
    }

    /// <summary>A complete walkable path must exist from the point to the anchor.</summary>
    public bool IsReachable(Vector3 point)
    {
        if (!_anchor.HasValue)
            return true;
        _pathCache.ClearCorners();
        return NavMesh.CalculatePath(point, _anchor.Value, NavMesh.AllAreas, _pathCache)
            && _pathCache.status == NavMeshPathStatus.PathComplete;
    }

    private Vector3 RandomPointInTriangle(Tier tier)
    {
        float roll = UnityEngine.Random.value * tier.TotalArea;
        int index = LowerBound(tier.CumulativeAreas, roll);
        (int a, int b, int c) = tier.Triangles[index];

        // Uniform barycentric sampling.
        float r1 = Mathf.Sqrt(UnityEngine.Random.value);
        float r2 = UnityEngine.Random.value;
        return _vertices[a] * (1f - r1)
             + _vertices[b] * (r1 * (1f - r2))
             + _vertices[c] * (r1 * r2);
    }

    private static long WeldKey(Vector3 v)
    {
        // 0.1 m grid; navmesh islands are spatially disjoint at that scale.
        long x = (long)Mathf.Round(v.x * 10f);
        long y = (long)Mathf.Round(v.y * 10f);
        long z = (long)Mathf.Round(v.z * 10f);
        return (x & 0x1FFFFF) | ((y & 0x1FFFFF) << 21) | ((z & 0x1FFFFF) << 42);
    }

    private static int Find(int[] parent, int i)
    {
        while (parent[i] != i)
        {
            parent[i] = parent[parent[i]];
            i = parent[i];
        }
        return i;
    }

    private static void Union(int[] parent, int a, int b)
    {
        int ra = Find(parent, a), rb = Find(parent, b);
        if (ra != rb)
            parent[ra] = rb;
    }

    private static int LowerBound(List<float> sorted, float value)
    {
        int lo = 0, hi = sorted.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (sorted[mid] < value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private bool HasCeilingAbove(Vector3 point)
    {
        return Physics.Raycast(point + Vector3.up * 0.3f, Vector3.up, 60f,
            _roofMask, QueryTriggerInteraction.Ignore);
    }

    private static bool IsInsideShip(Vector3 point)
    {
        var sor = StartOfRound.Instance;
        if (sor == null || sor.shipBounds == null)
            return false;
        Bounds bounds = sor.shipBounds.bounds;
        bounds.Expand(4f);
        return bounds.Contains(point);
    }

    private static bool IsTooCloseToAnyPlayer(Vector3 point, float minDistance)
    {
        var sor = StartOfRound.Instance;
        if (sor == null)
            return false;
        float sqrMin = minDistance * minDistance;
        foreach (var player in sor.allPlayerScripts)
        {
            if (player == null || !player.isPlayerControlled || player.isPlayerDead)
                continue;
            if ((player.transform.position - point).sqrMagnitude < sqrMin)
                return true;
        }
        return false;
    }
}
