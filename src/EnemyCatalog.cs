using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace MonstersGordion;

/// <summary>
/// Resolves the list of spawnable EnemyType assets from loaded game resources
/// and binds a config block for each of them.
/// </summary>
internal static class EnemyCatalog
{
    // Broken / unused entities that must never enter the pool.
    private static readonly HashSet<string> HardExcluded =
        new(StringComparer.OrdinalIgnoreCase) { "Lasso", "Red pill" };

    // Enemies with map requirements this moon does not naturally satisfy.
    // They stay in the pool — the notes are logged so a silent no-show is never
    // a mystery, and the spawner re-checks the requirement before each spawn.
    private static readonly Dictionary<string, string> Notes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Bush Wolf"] =
                "BushWolfEnemy needs vain shrouds to hide in and despawns itself on spawn without " +
                "them. Set [Integration] VainShroudIterations (or just leave it at 0 — enabling " +
                "Bush Wolf grows weeds automatically)",
            ["Cadaver Bloom"] =
                "CadaverBloomAI spawns as a dormant, invisible, agent-disabled seed by design — it " +
                "is planted and woken by Cadaver Growths. Enable 'Cadaver Growths' instead of " +
                "spawning Blooms directly",
            ["Feiopar"] =
                "PumaAI normally stalks from trees; inside the Company building it falls back to " +
                "ground stalking",
            ["Earth Leviathan"] =
                "SandWormAI burrows through terrain and surfaces under players — it works, but " +
                "looks wrong indoors",
            ["GiantKiwi"] =
                "GiantKiwiAI requires its nest to spawn (birdNestPrefab); the mod places one on the " +
                "interior navmesh automatically",
            ["RadMech"] =
                "Old Bird. Does NOT use a nest. It has been observed to be culled by KillEnemy right " +
                "after spawning on Gordion; the early-death diagnostic logs a stack trace naming the " +
                "caller. Cause not yet confirmed",
        };

    /// <summary>Every name that is not allowed to exist, for the ForeignEnemies policy.</summary>
    internal static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase);

    internal static readonly List<EnemyType> Enemies = new();

    internal static bool IsExcluded(string enemyName) =>
        enemyName != null && Excluded.Contains(enemyName);

    /// <summary>The EnemyAI subclass on the prefab, e.g. "PumaAI" — used in logs.</summary>
    internal static string AIClassName(EnemyType type)
    {
        try
        {
            var ai = type.enemyPrefab != null
                ? type.enemyPrefab.GetComponentInChildren<EnemyAI>(includeInactive: true)
                : null;
            return ai != null ? ai.GetType().Name : "<no EnemyAI component>";
        }
        catch (Exception e)
        {
            return $"<unreadable: {e.GetType().Name}>";
        }
    }

    internal static void Resolve()
    {
        Enemies.Clear();
        Excluded.Clear();

        foreach (string name in HardExcluded)
            Excluded.Add(name);
        foreach (string name in (Plugin.Cfg.ExcludedEnemies.Value ?? string.Empty)
                     .Split(',')
                     .Select(s => s.Trim())
                     .Where(s => s.Length > 0))
        {
            Excluded.Add(name);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rejected = new List<string>();
        foreach (var type in Resources.FindObjectsOfTypeAll<EnemyType>())
        {
            if (type == null || string.IsNullOrWhiteSpace(type.enemyName))
                continue;
            if (!seen.Add(type.enemyName))
                continue; // FindObjectsOfTypeAll can return duplicates

            if (type.enemyPrefab == null)
            {
                rejected.Add($"'{type.enemyName}' [no prefab] — the EnemyType asset has no " +
                             "enemyPrefab, so it cannot be instantiated");
                continue;
            }
            if (Excluded.Contains(type.enemyName))
            {
                rejected.Add($"'{type.enemyName}' [{AIClassName(type)}] — excluded " +
                             "(ExcludedEnemies blacklist or built-in exclusion)");
                continue;
            }

            Enemies.Add(type);
        }

        Enemies.Sort((a, b) => string.CompareOrdinal(a.enemyName, b.enemyName));

        // Bind configs eagerly so the .cfg file lists every enemy after the first run.
        foreach (var type in Enemies)
            Plugin.Cfg.For(type);

        Plugin.Log.LogInfo($"Enemy catalog resolved: {Enemies.Count} spawnable types " +
                           $"({rejected.Count} rejected).");
        LogSpawnabilityReport(rejected);
    }

    /// <summary>
    /// Explains, per enemy, whether it can spawn and why not — so an enemy that
    /// never shows up can always be traced to a concrete reason.
    /// </summary>
    private static void LogSpawnabilityReport(List<string> rejected)
    {
        foreach (string line in rejected)
            Plugin.Log.LogInfo($"  rejected: {line}");

        var eligible = new List<string>();
        foreach (var type in Enemies)
        {
            var s = Plugin.Cfg.For(type);
            string reason = null;
            if (!s.Enabled.Value) reason = "Enabled = false";
            else if (s.SpawnWeight.Value <= 0) reason = "SpawnWeight = 0";
            else if (s.MaxSpawnCount.Value <= 0) reason = "MaxSpawnCount = 0";

            Notes.TryGetValue(type.enemyName, out string note);

            if (reason != null)
            {
                if (Plugin.Cfg.DebugMode.Value)
                    Plugin.DebugLog($"  off: '{type.enemyName}' [{AIClassName(type)}] — {reason}" +
                                    (note != null ? $"; note: {note}" : string.Empty));
                continue;
            }

            eligible.Add($"{type.enemyName} (w{s.SpawnWeight.Value}, max {s.MaxSpawnCount.Value}" +
                         $"{(type.isOutsideEnemy ? ", outdoor" : ", indoor")})");
            if (note != null)
                Plugin.Log.LogWarning($"'{type.enemyName}' [{AIClassName(type)}] is enabled, note: {note}.");
        }

        Plugin.Log.LogInfo(eligible.Count > 0
            ? "Spawn pool: " + string.Join(", ", eligible)
            : "Spawn pool is EMPTY — nothing is enabled with a non-zero weight and max count.");
    }
}
