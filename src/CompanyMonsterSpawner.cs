using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GameNetcodeStuff;
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
    private readonly List<GameObject> _fakeTrees = new();
    private readonly List<EnemyAI> _ownedEnemies = new();
    private readonly List<GameObject> _ownedNests = new();
    private readonly Dictionary<int, float> _spawnTimes = new();

    // Original StartOfRound.naturalSurfaceTags, saved when we extend it so the worm
    // can breach the Company floor; restored on shutdown. null when untouched.
    private string[] _originalSurfaceTags;

    // Early-fail detection: types whose spawns die almost immediately (needing a
    // dungeon/weeds/etc. this moon lacks) are disabled for the landing so they
    // don't get spammed. Reset every landing (the spawner is recreated).
    private readonly List<(EnemyAI ai, string name, float time)> _recentSpawns = new();
    private readonly Dictionary<string, int> _earlyDeathCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _disabledThisLanding = new(StringComparer.OrdinalIgnoreCase);
    private const int EarlyDeathsBeforeDisable = 2;

    // Stuck detection: last recorded position + time per owned enemy.
    private readonly Dictionary<int, (Vector3 pos, float time)> _lastMovement = new();
    private const float StuckSeconds = 25f;
    private const float StuckDistance = 1.5f;
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

        // Weeds: normally grown during level load, but LethalLevelLoader reworks
        // that path so on Gordion it never runs. Grow them here instead — see
        // GrowVainShrouds. Evaluated once so GetWeeds() (which logs on every
        // call) is not hit each spawn cycle.
        _hasVainShrouds = CheckVainShrouds();
        if (!_hasVainShrouds)
        {
            int weedIterations = cfg.ResolveVainShroudIterations();
            if (weedIterations > 0)
                _hasVainShrouds = GrowVainShrouds(weedIterations);
        }

        CreateAINodes(cfg.AINodeCount.Value);
        CreateDeadTreesIfNeeded();
        EnableWormFloorEmergeIfNeeded();
        _loop = StartCoroutine(SpawnLoop());
        _maintenance = StartCoroutine(MaintenanceLoop());
        Plugin.Log.LogInfo(
            $"Company spawner active: cap={cfg.GlobalCap.Value}, " +
            $"interval=[{cfg.MinSpawnInterval.Value:F0}s..{cfg.MaxSpawnInterval.Value:F0}s], " +
            $"outsideAIMode={cfg.TreatEnemiesAsOutside.Value}, foreignEnemies={cfg.ForeignEnemies.Value}, " +
            $"ToilHead={(ToilHeadCompat.Present ? $"Coil {cfg.CoilHeadTurretChance.Value}% / Manti {cfg.ManticoilTurretChance.Value}% / Masked {cfg.MaskedTurretChance.Value}%" : "absent")}, " +
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
            DespawnOwnedNests();
            RestoreWormFloorEmerge();
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Cleanup despawn failed (scene tearing down?): {e.Message}");
        }

        foreach (var node in _aiNodes)
            if (node != null)
                Destroy(node);
        _aiNodes.Clear();
        foreach (var tree in _fakeTrees)
            if (tree != null)
                Destroy(tree);
        _fakeTrees.Clear();
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

        ScanForEarlyDeaths();

        // Build the candidate list: enabled, weighted, below its own max.
        var candidates = new List<(EnemyType type, EnemySpawnSettings s, int alive)>();
        foreach (var type in EnemyCatalog.Enemies)
        {
            var s = Plugin.Cfg.For(type);
            if (!s.Enabled.Value || s.SpawnWeight.Value <= 0 || s.MaxSpawnCount.Value <= 0)
                continue;
            if (!cfg.AllowHarmlessCreatures.Value && IsHarmlessCreature(type))
                continue; // Manticoil + Roaming Locust turned off as a group
            if (_disabledThisLanding.Contains(type.enemyName))
                continue; // keeps dying on this moon — stop retrying it
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
            cfg.MinDistanceFromPlayers.Value, 12, UpperShareFor(picked.type));
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
    /// <summary>
    /// Some enemies refuse to exist without their nest: EnemyAI.Start() runs
    /// <c>if (!foundNest &amp;&amp; enemyType.requireNestObjectsToSpawn) { isEnemyDead = true;
    /// Destroy(gameObject); }</c>. In vanilla v81 the Giant Kiwi (GiantKiwiAI,
    /// with its birdNestPrefab) is the clear case; Gordion places no nests during
    /// level generation, so such an enemy self-destructs one frame after spawning.
    ///
    /// This is data-driven off the EnemyType flags, not a hardcoded enemy list —
    /// it applies to whatever type actually declares a nest requirement. We place
    /// the nest ourselves on the interior navmesh; the enemy finds it, calls
    /// UseNestSpawnObject (which teleports it onto the nest and consumes it) and
    /// lives inside the building.
    ///
    /// NOTE: the Old Bird (RadMech) does NOT use a nest — its instant death on
    /// Gordion has a different, still-unconfirmed cause (see the early-death
    /// stack-trace diagnostic in OnEnemyKilled).
    /// </summary>
    private bool EnsureNestFor(EnemyType type)
    {
        if (type.nestSpawnPrefab == null || !type.requireNestObjectsToSpawn)
            return true;

        var rm = RoundManager.Instance;
        if (rm == null)
            return true;

        rm.enemyNestSpawnObjects.RemoveAll(n => n == null);
        if (rm.enemyNestSpawnObjects.Any(n => n != null && n.enemyType == type))
            return true; // an unused nest is already waiting

        Vector3? point = _sampler.GetRandomPoint(
            Plugin.Cfg.MinDistanceFromPlayers.Value, 12, Plugin.Cfg.UpperFloorSpawnShare.Value);
        if (point == null)
        {
            WarnRequirementOnce(type, "no interior navmesh point was free for its nest object");
            return false;
        }

        GameObject nest = null;
        try
        {
            nest = Instantiate(type.nestSpawnPrefab, point.Value,
                Quaternion.Euler(0f, UnityEngine.Random.Range(-180f, 180f), 0f));

            var nestComponent = nest.GetComponent<EnemyAINestSpawnObject>();
            var netObj = nest.GetComponentInChildren<NetworkObject>();
            if (nestComponent == null || netObj == null)
            {
                WarnRequirementOnce(type,
                    $"its nest prefab '{type.nestSpawnPrefab.name}' has no " +
                    $"{(nestComponent == null ? "EnemyAINestSpawnObject" : "NetworkObject")} component");
                Destroy(nest);
                return false;
            }

            netObj.Spawn(destroyWithScene: true);
            rm.enemyNestSpawnObjects.Add(nestComponent);
            _ownedNests.Add(nest);
            Plugin.DebugLog($"Placed a nest for '{type.enemyName}' at {point.Value:F1}.");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Could not place a nest for '{type.enemyName}': {e.Message}");
            if (nest != null)
                Destroy(nest);
            return false;
        }
    }

    private void WarnRequirementOnce(EnemyType type, string reason)
    {
        if (_warnedRequirements.Add(type.enemyName))
        {
            Plugin.Log.LogWarning(
                $"Not spawning '{type.enemyName}' [{EnemyCatalog.AIClassName(type)}]: {reason}. " +
                "Without it the game destroys the enemy inside EnemyAI.Start().");
        }
    }

    private void DespawnOwnedNests()
    {
        foreach (var nest in _ownedNests.Where(n => n != null).ToList())
        {
            try
            {
                var component = nest.GetComponent<EnemyAINestSpawnObject>();
                if (component != null && RoundManager.Instance != null)
                    RoundManager.Instance.enemyNestSpawnObjects.Remove(component);

                var netObj = nest.GetComponentInChildren<NetworkObject>();
                if (netObj != null && netObj.IsSpawned)
                    netObj.Despawn(destroy: true);
                else
                    Destroy(nest);
            }
            catch (Exception e)
            {
                Plugin.DebugLog($"Nest cleanup failed: {e.Message}");
            }
        }
        _ownedNests.Clear();
    }

    /// <summary>
    /// Grows vain shrouds by calling MoldSpreadManager.GenerateMold directly.
    ///
    /// The vanilla route (level's moldSpreadIterations -> RoundManager's level-load
    /// coroutine -> GenerateMold) never executes on Gordion in modpacks that use
    /// LethalLevelLoader: neither our hooks nor the game's own mold logging appear.
    /// Calling GenerateMold ourselves sidesteps that entirely, and it is
    /// self-contained — it instantiates the mold props and then runs
    /// grassInstancer.BatchChildren() + GetBiggestWeedPatch(), which is exactly what
    /// BushWolfEnemy's GetWeeds() check reads. That is what keeps the Fox alive.
    ///
    /// Host-side: only the server decides whether the Fox survives, so this is
    /// enough for it to work. Other players may not see the weed props themselves.
    /// </summary>
    private bool GrowVainShrouds(int iterations)
    {
        try
        {
            var mold = UnityEngine.Object.FindObjectOfType<MoldSpreadManager>();
            if (mold == null)
            {
                Plugin.Log.LogWarning("Cannot grow vain shrouds: no MoldSpreadManager on this moon.");
                return false;
            }

            // GenerateMold returns immediately if it believes it already ran for
            // this level; clear that private flag so our call actually generates.
            try
            {
                typeof(MoldSpreadManager)
                    .GetField("finishedGeneratingMold", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.SetValue(mold, false);
            }
            catch (Exception e)
            {
                Plugin.DebugLog($"Could not reset finishedGeneratingMold: {e.Message}");
            }

            var flagField = typeof(MoldSpreadManager)
                .GetField("finishedGeneratingMold", BindingFlags.NonPublic | BindingFlags.Instance);
            var level = StartOfRound.Instance != null ? StartOfRound.Instance.currentLevel : null;

            int patches = Mathf.Max(1, Plugin.Cfg.VainShroudPatches.Value);
            var origins = new List<string>();

            for (int i = 0; i < patches; i++)
            {
                // GenerateMold seeds its spread from the starting position and marks
                // itself finished, so re-clear the flag and feed it a fresh random
                // origin — that is what makes the nest land somewhere new each time.
                try { flagField?.SetValue(mold, false); }
                catch (Exception e) { Plugin.DebugLog($"Could not reset finishedGeneratingMold: {e.Message}"); }

                if (level != null)
                {
                    level.canSpawnMold = true;
                    level.moldSpreadIterations = iterations;
                    level.moldStartPosition = -1;
                }

                Vector3? start = PickVainShroudOrigin();
                if (start == null)
                {
                    Plugin.DebugLog("No free interior point left for another weed patch.");
                    break;
                }

                mold.GenerateMold(start.Value, iterations);
                origins.Add(start.Value.ToString("F1"));
            }

            bool grown = mold.GetWeeds();
            string where = origins.Count > 0 ? string.Join(", ", origins) : "nowhere";
            if (grown)
            {
                Plugin.Log.LogInfo(
                    $"Vain shrouds grown on Gordion: {origins.Count} patch(es) x {iterations} iterations " +
                    $"at {where}. Bush Wolf can now hide and will survive.");
            }
            else
            {
                Plugin.Log.LogWarning(
                    $"Tried to grow vain shrouds ({origins.Count} patch(es) x {iterations} iterations at " +
                    $"{where}) but the game still reports none — Bush Wolf will be skipped. Try a higher " +
                    "[Integration] VainShroudIterations.");
            }
            return grown;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Growing vain shrouds failed: {e}");
            return false;
        }
    }

    /// <summary>
    /// A fresh random spot on the interior navmesh for a weed patch. Uses the same
    /// reachability-checked sampler as spawning, with a 50/50 floor split, and keeps
    /// clear of where players stand at landing.
    /// </summary>
    private Vector3? PickVainShroudOrigin()
    {
        if (_sampler != null)
        {
            Vector3? point = _sampler.GetRandomPoint(Plugin.Cfg.MinDistanceFromPlayers.Value, 12, 50)
                          ?? _sampler.GetRandomPoint(0f, 12, 50);
            if (point != null)
                return point;
        }
        return StartOfRound.Instance != null
            ? StartOfRound.Instance.shipLandingPosition.position
            : (Vector3?)null;
    }

    // ---------------------------------------------------------------- worm floor emerge

    /// <summary>
    /// Lets the Earth Leviathan breach through the Company building floor.
    ///
    /// SandWormAI.StartEmergeAnimation only allows an emerge where the surface it
    /// would rise through is "natural": with no active Terrain (the Company moon)
    /// it checks the hit collider's tag against StartOfRound.naturalSurfaceTags.
    /// The interior floor isn't tagged as a natural surface, so the worm cancels
    /// every emerge and never attacks. We probe the actual floor tag and append it
    /// (plus a couple of common building tags) to naturalSurfaceTags for this
    /// landing, then restore the original array on shutdown.
    /// </summary>
    private void EnableWormFloorEmergeIfNeeded()
    {
        if (!Plugin.Cfg.EarthLeviathanFloorEmerge.Value)
            return;
        var worm = EnemyCatalog.Enemies.FirstOrDefault(
            e => string.Equals(e.enemyName, "Earth Leviathan", StringComparison.OrdinalIgnoreCase));
        if (worm == null || !Plugin.Cfg.For(worm).Enabled.Value)
            return;

        var sor = StartOfRound.Instance;
        if (sor == null || sor.naturalSurfaceTags == null)
            return;

        try
        {
            var tags = new HashSet<string>(sor.naturalSurfaceTags, StringComparer.Ordinal);

            // Probe the floor tag under the anchor / a sample point.
            Vector3 probe = _sampler?.Anchor ?? sor.shipLandingPosition.position;
            if (Physics.Raycast(probe + Vector3.up * 3f, Vector3.down, out RaycastHit hit, 12f,
                    sor.collidersAndRoomMaskAndDefault, QueryTriggerInteraction.Ignore))
            {
                string floorTag = hit.collider.tag;
                if (!string.IsNullOrEmpty(floorTag))
                    tags.Add(floorTag);
                Plugin.DebugLog($"Worm floor probe hit '{hit.collider.name}' tag='{floorTag}'.");
            }

            // Common Company building surface tags, so the worm can breach anywhere.
            foreach (string t in new[] { "Untagged", "Catwalk", "Metal", "Concrete", "Wood", "Tiles" })
                tags.Add(t);

            _originalSurfaceTags = sor.naturalSurfaceTags;
            sor.naturalSurfaceTags = tags.ToArray();
            Plugin.Log.LogInfo(
                $"Earth Leviathan: extended naturalSurfaceTags to {sor.naturalSurfaceTags.Length} entries " +
                "so the worm can breach up through the Company floor.");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Worm floor-emerge setup failed: {e.Message}");
            _originalSurfaceTags = null;
        }
    }

    private void RestoreWormFloorEmerge()
    {
        if (_originalSurfaceTags == null)
            return;
        try
        {
            if (StartOfRound.Instance != null)
                StartOfRound.Instance.naturalSurfaceTags = _originalSurfaceTags;
        }
        catch (Exception e)
        {
            Plugin.DebugLog($"Restoring naturalSurfaceTags failed: {e.Message}");
        }
        _originalSurfaceTags = null;
    }

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

        // A lone Cadaver Bloom without its Growth is invisible and inert; only
        // allow it when the standalone-trap driver is on to actually burst it.
        if (string.Equals(type.enemyName, "Cadaver Bloom", StringComparison.OrdinalIgnoreCase)
            && !Plugin.Cfg.CadaverBloomTraps.Value)
        {
            if (_warnedRequirements.Add(type.enemyName))
                Plugin.Log.LogWarning(
                    "Not spawning 'Cadaver Bloom': it needs its Growth (a dungeon) to activate. " +
                    "Enable [Integration] CadaverBloomTraps to plant standalone burst traps instead.");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Detects spawns that die within EarlyDeathSeconds — including those killed
    /// via Destroy() rather than KillEnemy() (e.g. CadaverGrowthAI's "Found no
    /// dungeon" self-destruct) which the KillEnemy patch never sees. After a few
    /// such deaths the type is disabled for the landing so it isn't spammed.
    /// </summary>
    private void ScanForEarlyDeaths()
    {
        float now = Time.realtimeSinceStartup;
        for (int i = _recentSpawns.Count - 1; i >= 0; i--)
        {
            var (ai, name, time) = _recentSpawns[i];
            bool dead = ai == null || ai.isEnemyDead;

            if (!dead)
            {
                if (now - time > EarlyDeathSeconds)
                    _recentSpawns.RemoveAt(i); // survived the window — stop tracking
                continue;
            }

            _recentSpawns.RemoveAt(i);
            if (now - time > EarlyDeathSeconds)
                continue; // died, but not early — normal death

            _earlyDeathCounts.TryGetValue(name, out int count);
            count++;
            _earlyDeathCounts[name] = count;

            if (count >= EarlyDeathsBeforeDisable && _disabledThisLanding.Add(name))
            {
                Plugin.Log.LogWarning(
                    $"'{name}' died within {EarlyDeathSeconds:F0}s of spawning {count} times — it " +
                    "cannot survive on Gordion (it likely needs a dungeon, weeds or other map " +
                    "feature this moon lacks). Disabling it until the next landing.");
            }
        }
    }

    // The genuinely harmless ambient creatures the AllowHarmlessCreatures switch
    // governs. Deliberately just Manticoil (bird) and the Roaming/Docile Locust
    // swarm — Tulip Snake is NOT included (it can grab players) and stays in the
    // normal pool regardless of the switch.
    private static readonly HashSet<string> HarmlessCreatures =
        new(StringComparer.OrdinalIgnoreCase)
        { "Manticoil", "Docile Locust Bees" };

    private static bool IsHarmlessCreature(EnemyType type) =>
        HarmlessCreatures.Contains(type.enemyName);

    /// <summary>Percentage of upper-floor spawns to use for a given type.</summary>
    private static int UpperShareFor(EnemyType type)
    {
        if (Plugin.Cfg.OldBirdUpperFloorOnly.Value
            && string.Equals(type.enemyName, "RadMech", StringComparison.OrdinalIgnoreCase))
        {
            return 100; // Old Bird: upper floor only.
        }
        return Plugin.Cfg.UpperFloorSpawnShare.Value;
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
        Instance.OnEnemyKilled(enemy);
    }

    // Enemies that die almost immediately are being culled by something (another
    // mod's KillEnemy, a vanilla validity check, a collision). Static analysis
    // cannot name the caller, so log a stack trace for the first few early
    // deaths — the trace names exactly who called KillEnemy.
    private const float EarlyDeathSeconds = 4f;
    private int _earlyDeathTracesLogged;

    private void OnEnemyKilled(EnemyAI enemy)
    {
        string name = enemy.enemyType.enemyName;
        int id = enemy.GetInstanceID();

        if (_spawnTimes.TryGetValue(id, out float spawnedAt))
        {
            float age = Time.realtimeSinceStartup - spawnedAt;
            _spawnTimes.Remove(id);

            if (age <= EarlyDeathSeconds)
            {
                if (_earlyDeathTracesLogged < 5)
                {
                    _earlyDeathTracesLogged++;
                    Plugin.Log.LogWarning(
                        $"'{name}' [{EnemyCatalog.AIClassName(enemy.enemyType)}] died only {age:F2}s " +
                        $"after we spawned it — something is culling it. Caller stack trace:\n" +
                        new System.Diagnostics.StackTrace(fNeedFileInfo: false));
                }
                else
                {
                    Plugin.Log.LogWarning($"'{name}' died {age:F2}s after spawn (stack trace suppressed after 5).");
                }
                return;
            }
        }

        Plugin.DebugLog($"Enemy died: {name}.");
    }

    // ---------------------------------------------------------------- spawning

    private void SpawnEnemy(EnemyType type, Vector3 point)
    {
        var cfg = Plugin.Cfg;
        try
        {
            if (!EnsureNestFor(type))
                return;

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
            _spawnTimes[ai.GetInstanceID()] = Time.realtimeSinceStartup;
            _recentSpawns.Add((ai, type.enemyName, Time.realtimeSinceStartup));
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

        // ToilHead: per-enemy chance to put a turret on Coil-Head / Manticoil / Masked.
        string name = ai.enemyType != null ? ai.enemyType.enemyName : string.Empty;
        var kind = ToilHeadCompat.KindOf(name);
        if (ToilHeadCompat.Present && kind != ToilHeadCompat.Kind.None)
        {
            int chance = Plugin.Cfg.ToilHeadTurretChance(kind);
            int roll = UnityEngine.Random.Range(0, 100);
            if (roll < chance)
            {
                bool slayer = UnityEngine.Random.Range(0, 100) < Plugin.Cfg.ToilHeadSlayerChance(kind);
                bool ok = ToilHeadCompat.TryApply(ai, slayer);
                Plugin.DebugLog($"ToilHead: '{name}' rolled {roll} < {chance} (slayer={slayer}) — " +
                                $"{(ok ? "turret attached" : "attach FAILED, see warnings above")}.");
            }
            else
            {
                Plugin.DebugLog($"ToilHead: '{name}' rolled {roll} >= {chance} — plain spawn.");
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

        ScanForEarlyDeaths();
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

            if (!RescueIfStranded(ai))
                UnstickIfIdle(ai);
        }

        _lastMovement.Keys
            .Where(id => _ownedEnemies.All(e => e == null || e.GetInstanceID() != id))
            .ToList()
            .ForEach(id => _lastMovement.Remove(id));

        DriveCadaverBlooms();
        EnforceForeignEnemyPolicy();
    }

    /// <summary>
    /// Standalone Cadaver Bloom traps. A Bloom spawned without its Growth just
    /// lies dormant and invisible forever (the Growth is what bursts it). Here we
    /// play that role on the host: when a living player comes within the trigger
    /// range of a dormant owned Bloom, call BurstForth so it erupts and chases.
    /// Host-side; other clients may not see the burst animation.
    /// </summary>
    private void DriveCadaverBlooms()
    {
        var sor = StartOfRound.Instance;
        if (sor == null)
            return;
        float range = Plugin.Cfg.CadaverBloomTriggerRange.Value;
        float sqrRange = range * range;

        foreach (var ai in _ownedEnemies)
        {
            if (ai is not CadaverBloomAI bloom || bloom.isEnemyDead || bloom.hasBurst)
                continue;

            PlayerControllerB nearest = null;
            float bestSqr = sqrRange;
            Vector3 bloomPos = bloom.transform.position;
            foreach (var player in sor.allPlayerScripts)
            {
                if (player == null || !player.isPlayerControlled || player.isPlayerDead)
                    continue;
                float sqr = (player.transform.position - bloomPos).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    nearest = player;
                }
            }

            if (nearest == null)
                continue;

            try
            {
                bloom.BurstForth(nearest, kill: false, bloomPos, bloom.transform.eulerAngles);
                Plugin.Log.LogInfo(
                    $"Cadaver Bloom burst on a player within {Mathf.Sqrt(bestSqr):F1} m at {bloomPos:F1}.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Cadaver Bloom BurstForth failed: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Nudges an enemy that has stood essentially still for StuckSeconds by
    /// re-pathing it to a fresh reachable point. Enemies designed to ambush from
    /// one spot (Bracken, Coil-Head, Barber, Jester winding, Ghost Girl) are
    /// exempt so their intended behaviour is not disrupted. Aimed mainly at
    /// stalkers like Feiopar that park on a tree and never move on indoors.
    /// </summary>
    private void UnstickIfIdle(EnemyAI ai)
    {
        if (ai.agent == null || !ai.agent.isActiveAndEnabled)
            return;

        string name = ai.enemyType != null ? ai.enemyType.enemyName : string.Empty;
        if (StationaryByDesign.Contains(name))
            return;

        int id = ai.GetInstanceID();
        Vector3 pos = ai.transform.position;
        float now = Time.realtimeSinceStartup;

        if (!_lastMovement.TryGetValue(id, out var last))
        {
            _lastMovement[id] = (pos, now);
            return;
        }

        if ((pos - last.pos).sqrMagnitude > StuckDistance * StuckDistance)
        {
            _lastMovement[id] = (pos, now); // it moved — reset the timer
            return;
        }

        if (now - last.time < StuckSeconds)
            return;

        Vector3? point = _sampler.GetRandomPoint(
            Plugin.Cfg.MinDistanceFromPlayers.Value, 8, Plugin.Cfg.UpperFloorSpawnShare.Value);
        if (point == null)
            return;

        try { ai.SetDestinationToPosition(point.Value, checkForPath: false); }
        catch (Exception e) { Plugin.DebugLog($"Unstick SetDestination failed: {e.Message}"); }
        _lastMovement[id] = (pos, now);
        Plugin.DebugLog($"Nudged idle '{name}' toward a new point (was stuck ~{StuckSeconds:F0}s).");
    }

    // Enemies whose AI legitimately keeps them still — never nudge these.
    private static readonly HashSet<string> StationaryByDesign =
        new(StringComparer.OrdinalIgnoreCase)
        { "Flowerman", "Spring", "Clay Surgeon", "Jester", "Girl", "Cadaver Bloom" };

    /// <summary>
    /// Teleports an enemy back inside if it can no longer reach the building.
    /// Returns true if a rescue happened (so the idle-nudge is skipped this tick).
    /// </summary>
    private bool RescueIfStranded(EnemyAI ai)
    {
        if (_sampler == null || ai.agent == null || !ai.agent.isActiveAndEnabled)
            return false;
        if (_sampler.IsReachable(ai.transform.position))
            return false;

        Vector3? point = _sampler.GetRandomPoint(
            Plugin.Cfg.MinDistanceFromPlayers.Value, 8, Plugin.Cfg.UpperFloorSpawnShare.Value);
        if (point == null)
            return false;

        ai.agent.Warp(point.Value + Vector3.up * Plugin.Cfg.SpawnYOffset.Value);
        _lastMovement[ai.GetInstanceID()] = (point.Value, Time.realtimeSinceStartup);
        Plugin.Log.LogInfo(
            $"Rescued stranded '{ai.enemyType?.enemyName}' — teleported back onto the interior navmesh.");
        return true;
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
            // IsExcluded already honours whitelist mode: in whitelist mode any
            // enemy not on the list counts as excluded and is despawned here.
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

    // ---------------------------------------------------------------- dead trees

    // Layer index PumaAI.Start() looks for when validating a tree: it does
    // Physics.CheckSphere(treePos + up*16, 15, 1<<25) with the tree itself
    // deactivated, i.e. it wants some *other* geometry (a canopy) on layer 25
    // near the treetop. We satisfy that with a separate collider on layer 25.
    private const int TreeCanopyLayer = 25;

    /// <summary>
    /// Grows dead trees for Feiopar (PumaAI) to stalk from.
    ///
    /// PumaAI only hunts from objects tagged "Tree"; the Company building has none,
    /// so it just idles. Each dead tree is: a "Tree"-tagged node (the AllTreeNodes
    /// entry PumaAI navigates to) with a visible trunk mesh for looks, plus a
    /// sibling canopy collider on layer 25 that passes PumaAI's tree-validation
    /// CheckSphere. Because PumaAI perches ~3 m above the ground next to the tree
    /// (not by climbing a mesh), a floor-level tree puts it at a sane height.
    /// </summary>
    private void CreateDeadTreesIfNeeded()
    {
        if (!Plugin.Cfg.FeioparDeadTrees.Value)
            return;

        var feiopar = EnemyCatalog.Enemies.FirstOrDefault(
            e => string.Equals(e.enemyName, "Feiopar", StringComparison.OrdinalIgnoreCase));
        if (feiopar == null)
            return;
        if (!Plugin.Cfg.For(feiopar).Enabled.Value)
        {
            Plugin.DebugLog("Feiopar disabled — skipping dead-tree generation.");
            return;
        }

        int count = Plugin.Cfg.FeioparTreeCount.Value;
        int created = 0;
        for (int i = 0; i < count; i++)
        {
            Vector3? point = _sampler.GetRandomPoint(0f, 15, 50);
            if (point == null)
                continue;

            // "Tree"-tagged node PumaAI paths to, plus a cosmetic trunk under it.
            var tree = CreateNode($"MG_DeadTree_{i}", point.Value, "Tree");
            AttachTrunkMesh(tree);
            _fakeTrees.Add(tree);

            // Canopy collider — a SIBLING (not a child) so it stays active while
            // PumaAI deactivates the tree during its CheckSphere validation.
            var canopy = new GameObject($"MG_DeadTreeCanopy_{i}") { layer = TreeCanopyLayer };
            canopy.transform.SetParent(transform, worldPositionStays: false);
            canopy.transform.position = point.Value + Vector3.up * 12f;
            var col = canopy.AddComponent<SphereCollider>();
            col.radius = 1f;
            col.isTrigger = false;
            _fakeTrees.Add(canopy);
            created++;
        }

        // PumaAI caches tree nodes in a static list on first spawn and never
        // rebuilds while it is non-empty; clear it so it discovers ours (and so
        // stale entries from a previous moon do not shadow them).
        ResetPumaTreeCache(feiopar);

        Plugin.Log.LogInfo(
            $"Feiopar: grew {created} dead trees for it to stalk from (EXPERIMENTAL — " +
            "set [Integration] FeioparDeadTrees=false to disable).");
    }

    /// <summary>Adds a simple, collider-less dead-tree trunk under a tree node.</summary>
    private static void AttachTrunkMesh(GameObject treeNode)
    {
        try
        {
            var trunk = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            trunk.name = "Trunk";
            // Visual only — strip the collider so it never blocks agents/players.
            var col = trunk.GetComponent<Collider>();
            if (col != null)
                Destroy(col);
            trunk.transform.SetParent(treeNode.transform, worldPositionStays: false);
            trunk.transform.localScale = new Vector3(0.35f, 2.2f, 0.35f); // ~0.7 m thick, ~4.4 m tall
            trunk.transform.localPosition = new Vector3(0f, 2.2f, 0f);    // base at the node (floor)
            var renderer = trunk.GetComponent<Renderer>();
            if (renderer != null)
                renderer.material.color = new Color(0.16f, 0.11f, 0.08f); // dead-wood brown
        }
        catch (Exception e)
        {
            Plugin.DebugLog($"Could not build trunk mesh: {e.Message}");
        }
    }

    private static void ResetPumaTreeCache(EnemyType feiopar)
    {
        try
        {
            var ai = feiopar.enemyPrefab != null
                ? feiopar.enemyPrefab.GetComponentInChildren<EnemyAI>(includeInactive: true)
                : null;
            Type pumaType = ai?.GetType();
            var field = pumaType?.GetField("AllTreeNodes", BindingFlags.Public | BindingFlags.Static);
            if (field != null)
            {
                field.SetValue(null, null);
                Plugin.DebugLog($"Reset {pumaType.Name}.AllTreeNodes so it rebuilds from our fake trees.");
            }
            else
            {
                Plugin.DebugLog("Could not find PumaAI.AllTreeNodes to reset (field renamed?).");
            }
        }
        catch (Exception e)
        {
            Plugin.DebugLog($"ResetPumaTreeCache failed: {e.Message}");
        }
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
