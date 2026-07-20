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

    // Enemies that cannot work on Gordion no matter what this mod does,
    // with the reason logged once so the exclusion is not mysterious.
    private static readonly Dictionary<string, string> Unsupported =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Bush Wolf"] =
                "BushWolfEnemy.Start() calls KillEnemyOnOwnerClient() immediately when the map has " +
                "no vain shrouds (weeds) to hide in, and Gordion has none — it would despawn itself " +
                "the moment it spawns",
        };

    /// <summary>Every name that is not allowed to exist, for the ForeignEnemies policy.</summary>
    internal static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase);

    internal static readonly List<EnemyType> Enemies = new();

    internal static bool IsExcluded(string enemyName) =>
        enemyName != null && Excluded.Contains(enemyName);

    internal static void Resolve()
    {
        Enemies.Clear();
        Excluded.Clear();

        foreach (string name in HardExcluded)
            Excluded.Add(name);
        foreach (string name in Unsupported.Keys)
            Excluded.Add(name);
        foreach (string name in (Plugin.Cfg.ExcludedEnemies.Value ?? string.Empty)
                     .Split(',')
                     .Select(s => s.Trim())
                     .Where(s => s.Length > 0))
        {
            Excluded.Add(name);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skippedUnsupported = new List<string>();
        foreach (var type in Resources.FindObjectsOfTypeAll<EnemyType>())
        {
            if (type == null || string.IsNullOrWhiteSpace(type.enemyName))
                continue;
            if (type.enemyPrefab == null)
                continue; // not spawnable
            if (!seen.Add(type.enemyName))
                continue; // FindObjectsOfTypeAll can return duplicates
            if (Unsupported.ContainsKey(type.enemyName))
            {
                skippedUnsupported.Add(type.enemyName);
                continue;
            }
            if (Excluded.Contains(type.enemyName))
                continue;

            Enemies.Add(type);
        }

        Enemies.Sort((a, b) => string.CompareOrdinal(a.enemyName, b.enemyName));

        // Bind configs eagerly so the .cfg file lists every enemy after the first run.
        foreach (var type in Enemies)
            Plugin.Cfg.For(type);

        Plugin.Log.LogInfo($"Enemy catalog resolved: {Enemies.Count} spawnable types.");
        foreach (string name in skippedUnsupported)
            Plugin.Log.LogInfo($"Skipping '{name}': {Unsupported[name]}.");

        if (Plugin.Cfg.DebugMode.Value)
        {
            foreach (var type in Enemies)
            {
                var s = Plugin.Cfg.For(type);
                Plugin.DebugLog(
                    $"  {type.enemyName}: enabled={s.Enabled.Value} weight={s.SpawnWeight.Value} " +
                    $"min={s.MinSpawnCount.Value} max={s.MaxSpawnCount.Value} " +
                    $"outsideType={type.isOutsideEnemy}");
            }
        }
    }
}
