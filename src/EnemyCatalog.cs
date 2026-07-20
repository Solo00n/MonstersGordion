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

    internal static readonly List<EnemyType> Enemies = new();

    internal static void Resolve()
    {
        Enemies.Clear();

        var userExcluded = new HashSet<string>(
            (Plugin.Cfg.ExcludedEnemies.Value ?? string.Empty)
                .Split(',')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in Resources.FindObjectsOfTypeAll<EnemyType>())
        {
            if (type == null || string.IsNullOrWhiteSpace(type.enemyName))
                continue;
            if (type.enemyPrefab == null)
                continue; // not spawnable
            if (HardExcluded.Contains(type.enemyName) || userExcluded.Contains(type.enemyName))
                continue;
            if (!seen.Add(type.enemyName))
                continue; // FindObjectsOfTypeAll can return duplicates

            Enemies.Add(type);
        }

        Enemies.Sort((a, b) => string.CompareOrdinal(a.enemyName, b.enemyName));

        // Bind configs eagerly so the .cfg file lists every enemy after the first run.
        foreach (var type in Enemies)
            Plugin.Cfg.For(type);

        Plugin.Log.LogInfo($"Enemy catalog resolved: {Enemies.Count} spawnable types.");
        if (Plugin.Cfg.DebugMode.Value)
        {
            foreach (var type in Enemies)
            {
                var s = Plugin.Cfg.For(type);
                Plugin.DebugLog(
                    $"  {type.enemyName}: enabled={s.Enabled.Value} weight={s.SpawnWeight.Value} " +
                    $"min={s.MinSpawnCount.Value} max={s.MaxSpawnCount.Value}");
            }
        }
    }
}
