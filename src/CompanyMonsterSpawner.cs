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
    private Coroutine _maintenance;
    private bool _shuttingDown;
    private bool _hasVainShrouds;
    private readonly HashSet<string> _warnedRequirements = new(StringComparer.OrdinalIgnoreCase);

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

        // Weeds are generated during level load; evaluate once so GetWeeds()
        // (which logs on every call) is not hit each spawn cycle.
        _hasVainShrouds = CheckVainShrouds();

        CreateAINodes(cfg.AINodeCount.Value);
        _loop = StartCoroutine(SpawnLoop());
        _maintenance = StartCoroutine(MaintenanceLoop());
        Plugin.Log.LogInfo(
            $"Company spawner active: cap={cfg.GlobalCap.Value}, " +
            $"interval=[{cfg.MinSpawnInterval.Value:F0}s..{cfg.MaxSpawnInterval.Value:F0}s], " +
            $"outsideAIMode={cfg.TreatEnemiesAsOutside.Value}, foreignEnemies={cfg.ForeignEnemies.Value}, " +
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

        if (_maintenance != null)
        {
            StopCoroutine(_maintenance);
            _maintenance = null;
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
            if (!MeetsMapRequirements(type))
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

    /// <summary>
    /// Blocks enemies whose map requirements this moon does not satisfy, so they
    /// are never spawned into an instant self-despawn. The reason is logged once
    /// per landing, naming the AI class.
    /// </summary>
    private static bool CheckVainShrouds()
    {
        try
        {
            var mold = UnityEngine.Object.FindObjectOfType<MoldSpreadManager>();
            if (mold == null)
            {
                Plugin.DebugLog("No MoldSpreadManager on this moon — no vain shrouds.");
                return false;
            }
            bool weeds = mold.GetWeeds();
            Plugin.Log.LogInfo($"Vain shrouds present: {weeds}.");
            return weeds;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Vain shroud check failed: {e.Message}");
            return false;
        }
    }

    private bool MeetsMapRequirements(EnemyType type)
    {
        if (string.Equals(type.enemyName, "Bush Wolf", StringComparison.OrdinalIgnoreCase)
            && !_hasVainShrouds)
        {
            if (_warnedRequirements.Add(type.enemyName))
            {
                Plugin.Log.LogWarning(
                    $"Not spawning '{type.enemyName}' [{EnemyCatalog.AIClassName(type)}]: it needs " +
                    "vain shrouds to hide in and none grew on this moon. Weeds are grown at level " +
                    "load, so set [Integration] VainShroudIterations > 0 (or keep it at 0 with " +
                    "Bush Wolf enabled) and fly to Gordion again — enabling it mid-round is too late.");
            }
            return false;
        }
        return true;
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

    /// <summary>
    /// Makes a spawned enemy behave correctly inside the Company building.
    ///
    /// The critical part is <c>isOutside = true</c>. EnemyAI.PlayerIsTargetable
    /// requires <c>player.isInsideFactory != isOutside</c>, and players in the
    /// Company building are NOT flagged as inside a factory (there is no
    /// EntranceTeleport there). With isOutside = false no enemy can ever target
    /// a player — MeetsStandardPlayerCollisionConditions fails too, which is why
    /// Masked enemies used to walk up to players and calmly walk away again.
    ///
    /// Node assignment must then be forced by hand: with isOutside = true
    /// GetAINodes() hands out RoundManager's outdoor nodes, which sit far away
    /// across unreachable geometry, so enemies would trek to a wall and idle.
    /// </summary>
    private void ApplyInteriorAI(EnemyAI ai)
    {
        try
        {
            bool outside = Plugin.Cfg.TreatEnemiesAsOutside.Value;

            // SetEnemyOutside also refreshes the agent area mask for the enemy
            // size class, which we want; it clobbers allAINodes, so assign after.
            try { ai.SetEnemyOutside(outside); }
            catch (Exception e) { Plugin.DebugLog($"SetEnemyOutside threw: {e.Message}"); }
            ai.isOutside = outside;

            AssignNodes(ai);

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

    private void AssignNodes(EnemyAI ai)
    {
        GameObject[] nodes = _aiNodes.Where(n => n != null).ToArray();
        if (nodes.Length > 0)
            ai.allAINodes = nodes;
    }

    // ---------------------------------------------------------------- maintenance

    /// <summary>
    /// Periodic upkeep: enemy AI settings are re-applied (anything that calls
    /// GetAINodes() again would otherwise send an enemy off to the outdoor
    /// nodes), enemies that ended up somewhere unreachable are teleported back,
    /// and the ForeignEnemies policy is enforced.
    /// </summary>
    private IEnumerator MaintenanceLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(Mathf.Max(1f, Plugin.Cfg.MaintenanceInterval.Value));
            try
            {
                MaintenanceTick();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Maintenance tick failed: {e}");
            }
        }
    }

    private void MaintenanceTick()
    {
        if (StartOfRound.Instance == null || StartOfRound.Instance.shipIsLeaving)
            return;

        _ownedEnemies.RemoveAll(e => e == null || e.isEnemyDead);

        bool outside = Plugin.Cfg.TreatEnemiesAsOutside.Value;
        GameObject[] nodes = _aiNodes.Where(n => n != null).ToArray();

        foreach (var ai in _ownedEnemies)
        {
            if (ai == null || ai.isEnemyDead || ai.inSpecialAnimation)
                continue;

            if (ai.isOutside != outside)
            {
                ai.isOutside = outside;
                Plugin.DebugLog($"Re-applied isOutside={outside} to '{ai.enemyType?.enemyName}'.");
            }

            // Something replaced our node set (GetAINodes, another mod) — restore it.
            if (nodes.Length > 0 && (ai.allAINodes == null || ai.allAINodes.Length != nodes.Length
                                     || (ai.allAINodes.Length > 0 && ai.allAINodes[0] != nodes[0])))
            {
                ai.allAINodes = nodes;
                Plugin.DebugLog($"Restored patrol nodes for '{ai.enemyType?.enemyName}'.");
            }

            RescueIfStranded(ai);
        }

        EnforceForeignEnemyPolicy();
    }

    /// <summary>Teleports an enemy back inside if it can no longer reach the building.</summary>
    private void RescueIfStranded(EnemyAI ai)
    {
        if (_sampler == null || ai.agent == null || !ai.agent.isActiveAndEnabled)
            return;
        if (_sampler.IsReachable(ai.transform.position))
            return;

        Vector3? point = _sampler.GetRandomPoint(
            Plugin.Cfg.MinDistanceFromPlayers.Value, 8, Plugin.Cfg.UpperFloorSpawnShare.Value);
        if (point == null)
            return;

        ai.agent.Warp(point.Value + Vector3.up * Plugin.Cfg.SpawnYOffset.Value);
        Plugin.Log.LogInfo(
            $"Rescued stranded '{ai.enemyType?.enemyName}' — teleported back onto the interior navmesh.");
    }

    /// <summary>
    /// Applies the blacklist to enemies this mod did not spawn. Vanilla spawn
    /// cycles, BrutalCompanyMinus events and MoreEnemies all bypass our config,
    /// which is why blacklisted baboon hawks and worms could still show up.
    /// </summary>
    private void EnforceForeignEnemyPolicy()
    {
        var policy = Plugin.Cfg.ForeignEnemies.Value;
        if (policy == ForeignEnemyPolicy.Ignore)
            return;

        var rm = RoundManager.Instance;
        if (rm == null)
            return;

        foreach (var enemy in rm.SpawnedEnemies.Where(e => e != null).ToList())
        {
            if (enemy.isEnemyDead || enemy.enemyType == null)
                continue;
            if (_ownedEnemies.Contains(enemy))
                continue; // ours: already validated against the config
            if (enemy.inSpecialAnimation || enemy.inSpecialAnimationWithPlayer != null)
                continue; // mid kill animation — removing it would strand the player

            string name = enemy.enemyType.enemyName;
            bool remove = EnemyCatalog.IsExcluded(name);
            if (!remove && policy == ForeignEnemyPolicy.RemoveNotEnabled)
                remove = !Plugin.Cfg.For(enemy.enemyType).Enabled.Value;
            if (!remove)
                continue;

            if (TryDespawn(rm, enemy))
                Plugin.Log.LogInfo($"Removed '{name}' spawned by another mod (ForeignEnemies={policy}).");
        }
    }

    // ---------------------------------------------------------------- AI nodes

    /// <summary>
    /// The Company building has no vanilla interior AI nodes, so roam/patrol AI
    /// would have nothing to walk between. Generate our own on the navmesh.
    ///
    /// Each position gets two objects: one tagged "AINode" (what indoor logic
    /// and StarlancerAIFix look up) and one tagged "OutsideAINode" (what enemies
    /// flagged as outdoors look up). Whichever way an AI re-resolves its nodes,
    /// it lands on a point inside the building.
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

            _aiNodes.Add(CreateNode($"MG_AINode_{i}", point.Value, "AINode"));
            _aiNodes.Add(CreateNode($"MG_OutsideAINode_{i}", point.Value, "OutsideAINode"));
        }
        Plugin.Log.LogInfo($"Created {_aiNodes.Count / 2} interior AI patrol nodes (indoor + outdoor tagged).");
    }

    private GameObject CreateNode(string name, Vector3 position, string tagName)
    {
        var node = new GameObject(name);
        node.transform.SetParent(transform, worldPositionStays: false);
        node.transform.position = position;
        try { node.tag = tagName; }
        catch (Exception e) { Plugin.DebugLog($"Could not tag AI node '{tagName}': {e.Message}"); }
        return node;
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
