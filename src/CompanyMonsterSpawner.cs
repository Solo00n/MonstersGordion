using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MonstersGordion.Compat;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace MonstersGordion;

/// <summary>
/// Host-only singleton that lives for one landing on 71-Gordion.
/// Created by the StartOfRound.OnShipLandedMiscEvents patch, destroyed when
/// the ship leaves (or the scene is torn down), so coroutines never leak.
/// </summary>
internal sealed class CompanyMonsterSpawner : MonoBehaviour
{
    internal static CompanyMonsterSpawner Instance { get; private set; }

    private readonly List<GameObject> _aiNodes = new();
    private readonly List<EnemyAI> _ownedEnemies = new();
    private NavMeshSampler _sampler;
    private Coroutine _loop;
    private bool _shuttingDown;

    // ---------------------------------------------------------------- lifecycle

    internal static bool IsCompanyLevel()
    {
        var level = StartOfRound.Instance != null ? StartOfRound.Instance.currentLevel : null;
        if (level == null)
            return false;
        return level.sceneName == "CompanyBuilding"
            || level.levelID == 3
            || (level.PlanetName != null && level.PlanetName.Contains("Gordion"));
    }

    /// <summary>Entry point, called once per landing by the LandingWatcher.</summary>
    internal static void OnShipLanded()
    {
        var sor = StartOfRound.Instance;
        if (sor == null)
            return;

        var level = sor.currentLevel;
        bool isHost = sor.IsServer;
        bool company = IsCompanyLevel();
        Plugin.Log.LogInfo(
            $"Ship landed: planet='{level?.PlanetName}', scene='{level?.sceneName}', " +
            $"host={isHost}, companyMoon={company}.");

        if (!isHost)
        {
            // Spawning is host authority only; clients receive enemies via netcode.
            Plugin.Log.LogInfo(
                "This game session is not the host — the spawner only runs on the host's machine. " +
                "Make sure the lobby host has MonstersGordion installed.");
            return;
        }
        if (!company)
        {
            Plugin.DebugLog("Not the Company moon — spawner stays idle.");
            return;
        }
        if (Instance != null)
            return;

        // Lazy compat detection: by now the chainloader has finished.
        ToilHeadCompat.Scan();
        StarlancerCompat.Scan();
        BcmeCompat.Scan();

        var go = new GameObject("MonstersGordion_Spawner");
        Instance = go.AddComponent<CompanyMonsterSpawner>();
        Instance.Begin();
    }

    /// <summary>Called from the ShipLeave / StartOfRound.OnDestroy patches.</summary>
    internal static void OnShipLeaving()
    {
        if (Instance != null)
            Instance.Shutdown();
    }

    private void Begin()
    {
        var cfg = Plugin.Cfg;
        EnemyCatalog.Resolve();

        if (cfg.RespawnOnLoad.Value)
            DespawnAllEnemies();

        _sampler = new NavMeshSampler(cfg.RequireIndoorPoints.Value);
        int triangles = _sampler.Build();
        if (triangles == 0)
        {
            Plugin.Log.LogError(
                "No usable navmesh found inside the Company building. " +
                "Make sure NavMeshInCompanyRedux is installed and working " +
                "(or set RequireIndoorPoints=false to loosen the filter). " +
                "Spawning is disabled for this landing.");
            return;
        }

        CreateAINodes(cfg.AINodeCount.Value);
        _loop = StartCoroutine(SpawnLoop());
        Plugin.Log.LogInfo(
            $"Company spawner active: cap={cfg.GlobalCap.Value}, " +
            $"interval=[{cfg.MinSpawnInterval.Value:F0}s..{cfg.MaxSpawnInterval.Value:F0}s], " +
            $"ToilHead={(ToilHeadCompat.Present ? $"{cfg.ToilHeadSpawnChance.Value}%" : "absent")}, " +
            $"StarlancerAIFix={(StarlancerCompat.Present ? "present" : "absent")}, " +
            $"BCME={(BcmeCompat.Present ? "present" : "absent")}.");
    }

    private void Shutdown()
    {
        if (_shuttingDown)
            return;
        _shuttingDown = true;

        if (_loop != null)
        {
            StopCoroutine(_loop);
            _loop = null;
        }

        try
        {
            if (Plugin.Cfg.DespawnOnShipLeave.Value)
                DespawnOwnedEnemies();
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Cleanup despawn failed (scene tearing down?): {e.Message}");
        }

        foreach (var node in _aiNodes)
            if (node != null)
                Destroy(node);
        _aiNodes.Clear();
        _ownedEnemies.Clear();

        Instance = null;
        Destroy(gameObject);
        Plugin.DebugLog("Company spawner shut down.");
    }

    private void OnDestroy()
    {
        // Safety net for scene unloads that bypass ShipLeave (disconnect, crash to menu).
        if (Instance == this && !_shuttingDown)
        {
            _shuttingDown = true;
            StopAllCoroutines();
            Instance = null;
        }
    }

    // ---------------------------------------------------------------- spawn loop

    private IEnumerator SpawnLoop()
    {
        var cfg = Plugin.Cfg;
        while (true)
        {
            float min = Mathf.Max(1f, cfg.MinSpawnInterval.Value);
            float max = Mathf.Max(min, cfg.MaxSpawnInterval.Value);
            float wait = UnityEngine.Random.Range(min, max);
            Plugin.DebugLog($"Next spawn attempt in {wait:F1}s.");
            yield return new WaitForSeconds(wait);

            try
            {
                TrySpawnCycle();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Spawn cycle failed: {e}");
            }
        }
    }

    private void TrySpawnCycle()
    {
        var cfg = Plugin.Cfg;
        var rm = RoundManager.Instance;
        if (rm == null || StartOfRound.Instance == null || StartOfRound.Instance.shipIsLeaving)
            return;

        int aliveTotal = CountAliveEnemies(out Dictionary<string, int> alivePerType);
        if (aliveTotal >= cfg.GlobalCap.Value)
        {
            Plugin.DebugLog($"Global cap reached ({aliveTotal}/{cfg.GlobalCap.Value}) — skipping.");
            return;
        }

        // Build the candidate list: enabled, weighted, below its own max.
        var candidates = new List<(EnemyType type, EnemySpawnSettings s, int alive)>();
        foreach (var type in EnemyCatalog.Enemies)
        {
            var s = Plugin.Cfg.For(type);
            if (!s.Enabled.Value || s.SpawnWeight.Value <= 0 || s.MaxSpawnCount.Value <= 0)
                continue;
            alivePerType.TryGetValue(type.enemyName, out int alive);
            if (alive >= s.MaxSpawnCount.Value)
                continue;
            candidates.Add((type, s, alive));
        }

        if (candidates.Count == 0)
        {
            Plugin.DebugLog("No eligible enemy types (all disabled or at their max).");
            return;
        }

        // Types below their MinSpawnCount get priority: the mod tries to keep
        // the configured minimum population before rolling the general pool.
        var deficit = candidates.Where(c => c.alive < c.s.MinSpawnCount.Value).ToList();
        List<(EnemyType type, EnemySpawnSettings s, int alive)> pool;
        string poolName;
        if (deficit.Count > 0)
        {
            pool = deficit;
            poolName = "min-deficit pool";
        }
        else
        {
            // OutsideEnemyShare: roll between the outdoor pool (dogs, giants,
            // baboon hawks...) and the indoor pool; fall back if one is empty.
            bool wantOutside = UnityEngine.Random.Range(0, 100) < cfg.OutsideEnemyShare.Value;
            var chosen = candidates.Where(c => c.type.isOutsideEnemy == wantOutside).ToList();
            if (chosen.Count == 0)
            {
                chosen = candidates;
                poolName = wantOutside ? "outdoor pool empty → all" : "indoor pool empty → all";
            }
            else
            {
                poolName = wantOutside ? "outdoor pool" : "indoor pool";
            }
            pool = chosen;
        }

        var picked = WeightedPick(pool);
        Plugin.DebugLog(
            $"Picked '{picked.type.enemyName}' (weight {picked.s.SpawnWeight.Value}, " +
            $"alive {picked.alive}/{picked.s.MaxSpawnCount.Value}, {poolName}, " +
            $"total alive {aliveTotal}/{cfg.GlobalCap.Value}).");

        Vector3? point = _sampler.GetRandomPoint(
            cfg.MinDistanceFromPlayers.Value, 12, cfg.UpperFloorSpawnShare.Value);
        if (point == null)
        {
            Plugin.Log.LogWarning(
                $"Could not find a valid interior navmesh point for '{picked.type.enemyName}' — skipping this cycle.");
            return;
        }

        SpawnEnemy(picked.type, point.Value);
    }

    private static (EnemyType type, EnemySpawnSettings s, int alive) WeightedPick(
        List<(EnemyType type, EnemySpawnSettings s, int alive)> pool)
    {
        int totalWeight = 0;
        foreach (var c in pool)
            totalWeight += c.s.SpawnWeight.Value;

        int roll = UnityEngine.Random.Range(0, totalWeight);
        foreach (var c in pool)
        {
            roll -= c.s.SpawnWeight.Value;
            if (roll < 0)
                return c;
        }
        return pool[pool.Count - 1];
    }

    // ---------------------------------------------------------------- counting

    /// <summary>
    /// Counts alive enemies, either everything on the moon (CountForeignEnemies=true,
    /// the default — shares the budget with BCME etc.) or only our own spawns.
    /// </summary>
    private int CountAliveEnemies(out Dictionary<string, int> perType)
    {
        perType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int total = 0;

        _ownedEnemies.RemoveAll(e => e == null || e.isEnemyDead);

        IEnumerable<EnemyAI> source = Plugin.Cfg.CountForeignEnemies.Value
            ? RoundManager.Instance.SpawnedEnemies
            : _ownedEnemies;

        foreach (var enemy in source)
        {
            if (enemy == null || enemy.isEnemyDead || enemy.enemyType == null)
                continue;
            total++;
            string name = enemy.enemyType.enemyName;
            perType.TryGetValue(name, out int n);
            perType[name] = n + 1;
        }
        return total;
    }

    internal static void NotifyEnemyKilled(EnemyAI enemy)
    {
        if (Instance == null || enemy == null || enemy.enemyType == null)
            return;
        Plugin.DebugLog($"Enemy died: {enemy.enemyType.enemyName}.");
    }

    // ---------------------------------------------------------------- spawning

    private void SpawnEnemy(EnemyType type, Vector3 point)
    {
        var cfg = Plugin.Cfg;
        try
        {
            Vector3 spawnPosition = point + Vector3.up * cfg.SpawnYOffset.Value;
            float yRotation = UnityEngine.Random.Range(0f, 360f);

            NetworkObjectReference reference =
                RoundManager.Instance.SpawnEnemyGameObject(spawnPosition, yRotation, -1, type);

            if (!reference.TryGet(out NetworkObject netObj) || netObj == null)
            {
                Plugin.Log.LogWarning($"Spawn of '{type.enemyName}' returned no NetworkObject.");
                return;
            }

            EnemyAI ai = netObj.GetComponent<EnemyAI>();
            if (ai == null)
                ai = netObj.GetComponentInChildren<EnemyAI>();
            if (ai == null)
            {
                Plugin.Log.LogWarning($"Spawned '{type.enemyName}' has no EnemyAI component.");
                return;
            }

            _ownedEnemies.Add(ai);
            Plugin.DebugLog($"Spawned '{type.enemyName}' at {spawnPosition}.");
            StartCoroutine(PostSpawnSetup(ai));
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Failed to spawn '{type.enemyName}': {e}");
        }
    }

    /// <summary>
    /// Runs two frames after the spawn so EnemyAI.Start (and StarlancerAIFix's
    /// postfix on it) has executed, then forces interior AI state on top.
    /// </summary>
    private IEnumerator PostSpawnSetup(EnemyAI ai)
    {
        yield return null;
        yield return null;
        if (ai == null || ai.isEnemyDead)
            yield break;

        ApplyInteriorAI(ai);

        // ToilHead: chance to put a turret on Coil-Heads and Manticoils.
        string name = ai.enemyType != null ? ai.enemyType.enemyName : string.Empty;
        if (ToilHeadCompat.Present && ToilHeadCompat.IsEligible(name))
        {
            int roll = UnityEngine.Random.Range(0, 100);
            if (roll < Plugin.Cfg.ToilHeadSpawnChance.Value)
            {
                bool slayer = UnityEngine.Random.Range(0, 100) < Plugin.Cfg.ToilSlayerChance.Value;
                bool ok = ToilHeadCompat.TryApply(ai, slayer);
                Plugin.DebugLog($"ToilHead roll {roll} < {Plugin.Cfg.ToilHeadSpawnChance.Value} " +
                                $"for '{name}' (slayer={slayer}): " +
                                $"{(ok ? "turret attached" : "attach FAILED — see warnings above")}.");
            }
            else
            {
                Plugin.DebugLog($"ToilHead roll {roll} >= {Plugin.Cfg.ToilHeadSpawnChance.Value} for '{name}' — plain spawn.");
            }
        }
    }

    private void ApplyInteriorAI(EnemyAI ai)
    {
        try
        {
            GameObject[] nodes = _aiNodes.Where(n => n != null).ToArray();

            // Mark the enemy as an interior one and point it at our patrol nodes.
            // SetEnemyOutside(false) re-resolves nodes from the "AINode" tag (ours),
            // the explicit assignment afterwards is a belt-and-braces fallback in
            // case another mod's Start postfix replaced the array meanwhile.
            ai.isOutside = false;
            try { ai.SetEnemyOutside(false); }
            catch (Exception e) { Plugin.DebugLog($"SetEnemyOutside threw: {e.Message}"); }
            if (nodes.Length > 0)
                ai.allAINodes = nodes;

            // Make sure the agent actually sits on the navmesh.
            if (ai.agent != null && ai.agent.isActiveAndEnabled
                && NavMesh.SamplePosition(ai.transform.position, out NavMeshHit hit, 4f, NavMesh.AllAreas))
            {
                ai.agent.Warp(hit.position);
            }

            // StarlancerAIFix note: its EnemyAI.Start postfix already ran for this
            // enemy (we are 2 frames late on purpose); running last means OUR node
            // assignment wins over its surface-level "outdoors" classification.
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Interior AI fix-up failed for '{ai.enemyType?.enemyName}': {e.Message}");
        }
    }

    // ---------------------------------------------------------------- AI nodes

    /// <summary>
    /// The Company building has no vanilla interior AI nodes, so roam/patrol AI
    /// would have nothing to walk between. Generate our own on the navmesh.
    /// </summary>
    private void CreateAINodes(int count)
    {
        for (int i = 0; i < count; i++)
        {
            // 50/50 tier split for patrol nodes so both floors are covered
            // regardless of the spawn-share setting.
            Vector3? point = _sampler.GetRandomPoint(0f, 15, 50);
            if (point == null)
                continue;

            var node = new GameObject($"MG_AINode_{i}");
            node.transform.SetParent(transform, worldPositionStays: false);
            node.transform.position = point.Value;
            try { node.tag = "AINode"; }
            catch (Exception e) { Plugin.DebugLog($"Could not tag AI node: {e.Message}"); }
            _aiNodes.Add(node);
        }
        Plugin.Log.LogInfo($"Created {_aiNodes.Count} interior AI patrol nodes.");
    }

    // ---------------------------------------------------------------- despawning

    /// <summary>RespawnOnLoad=true: wipe every enemy currently on the moon.</summary>
    private void DespawnAllEnemies()
    {
        var rm = RoundManager.Instance;
        if (rm == null)
            return;

        var snapshot = rm.SpawnedEnemies.Where(e => e != null).ToList();
        int despawned = 0;
        foreach (var enemy in snapshot)
        {
            if (TryDespawn(rm, enemy))
                despawned++;
        }
        rm.SpawnedEnemies.RemoveAll(e => e == null);
        if (despawned > 0)
            Plugin.Log.LogInfo($"RespawnOnLoad: despawned {despawned} pre-existing enemies.");
    }

    private void DespawnOwnedEnemies()
    {
        var rm = RoundManager.Instance;
        if (rm == null)
            return;
        int despawned = 0;
        foreach (var enemy in _ownedEnemies.Where(e => e != null).ToList())
        {
            if (TryDespawn(rm, enemy))
                despawned++;
        }
        if (despawned > 0)
            Plugin.DebugLog($"Ship leaving: despawned {despawned} of our enemies.");
    }

    private static bool TryDespawn(RoundManager rm, EnemyAI enemy)
    {
        try
        {
            NetworkObject netObj = enemy.thisNetworkObject;
            if (netObj == null)
                netObj = enemy.GetComponent<NetworkObject>();
            if (netObj != null && netObj.IsSpawned)
            {
                rm.DespawnEnemyOnServer(netObj);
                return true;
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Failed to despawn '{enemy.enemyType?.enemyName}': {e.Message}");
        }
        return false;
    }
}
