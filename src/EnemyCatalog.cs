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
                "BushWolfEnemy needs vain shrouds to hide in. The mod now grows them on Gordion at " +
                "save load (see [Integration] VainShroudIterations), so enable Bush Wolf and reload " +
                "the save / fly fresh — enabling it mid-round is too late for weed generation",
            ["Cadaver Growths"] =
                "Intentionally unsupported: CadaverGrowthAI hard-requires a DunGen dungeon, which the " +
                "Company building is not (even BrutalCompanyMinus's own Cadaver event can't bypass it). " +
                "Use Cadaver Bloom traps (CadaverBloomTraps) for a working cadaver on Gordion",
            ["Cadaver Bloom"] =
                "Enabled via [Integration] CadaverBloomTraps: planted directly as standalone corpse " +
                "traps that burst and chase when a player walks close, so no Growth/dungeon is needed",
            ["Feiopar"] =
                "PumaAI hunts from 'Tree'-tagged colliders on layer 25. With [Integration] " +
                "FeioparDeadTrees the mod grows proper dead trees (tag + layer 25 collider + canopy) " +
                "so it perches ~3 m up on a tree and pounces players who come near",
            ["Earth Leviathan"] =
                "SandWormAI only emerges through natural ground, which the Company interior lacks. With " +
                "[Balance] EarthLeviathanFloorEmerge the mod lets it breach up through the building floor",
            // No notes for RadMech / GiantKiwi: both are nest-requiring and work
            // correctly now that EnsureNestFor places their nest automatically.
        };

    // The configured ExcludedEnemies names, plus whether the list is a whitelist.
    private static readonly HashSet<string> ListedNames = new(StringComparer.OrdinalIgnoreCase);
    private static bool _whitelistMode;

    internal static readonly List<EnemyType> Enemies = new();

    /// <summary>
    /// Whether a type is barred from the moon (drives the catalog filter and the
    /// ForeignEnemies despawn policy). Lasso and Red pill are always barred; the
    /// configured list is either a blacklist (default) or, in whitelist mode, an
    /// allow-list where everything not listed is barred.
    /// </summary>
    internal static bool IsExcluded(string enemyName)
    {
        if (enemyName == null)
            return false;
        if (HardExcluded.Contains(enemyName))
            return true;
        return _whitelistMode ? !ListedNames.Contains(enemyName) : ListedNames.Contains(enemyName);
    }

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
        ListedNames.Clear();
        _whitelistMode = Plugin.Cfg.ExcludedEnemiesIsWhitelist.Value;

        foreach (string name in (Plugin.Cfg.ExcludedEnemies.Value ?? string.Empty)
                     .Split(',')
                     .Select(s => s.Trim())
                     .Where(s => s.Length > 0))
        {
            ListedNames.Add(name);
        }

        Plugin.Log.LogInfo(_whitelistMode
            ? $"Enemy list is a WHITELIST of {ListedNames.Count} name(s): only these may spawn."
            : $"Enemy list is a blacklist of {ListedNames.Count} name(s).");
        if (_whitelistMode && ListedNames.Count == 0)
            Plugin.Log.LogWarning(
                "ExcludedEnemiesIsWhitelist=true but ExcludedEnemies is empty — nothing will spawn.");

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
            if (IsExcluded(type.enemyName))
            {
                string why = HardExcluded.Contains(type.enemyName) ? "built-in exclusion"
                    : _whitelistMode ? "not on the whitelist"
                    : "on the blacklist";
                rejected.Add($"'{type.enemyName}' [{AIClassName(type)}] — excluded ({why})");
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
