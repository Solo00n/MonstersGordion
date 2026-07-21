using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;

namespace MonstersGordion;

/// <summary>What to do about enemies this mod did not spawn (game, BCME, MoreEnemies...).</summary>
internal enum ForeignEnemyPolicy
{
    /// <summary>Leave them alone.</summary>
    Ignore,

    /// <summary>Despawn types listed in ExcludedEnemies (and unsupported ones).</summary>
    RemoveExcluded,

    /// <summary>Despawn anything that is excluded or has Enabled = false in this config.</summary>
    RemoveNotEnabled,
}

/// <summary>Per-enemy-type spawn settings bound to the BepInEx config file.</summary>
internal sealed class EnemySpawnSettings
{
    public ConfigEntry<bool> Enabled;
    public ConfigEntry<int> SpawnWeight;
    public ConfigEntry<int> MinSpawnCount;
    public ConfigEntry<int> MaxSpawnCount;
}

internal sealed class PluginConfig
{
    // [General]
    public readonly ConfigEntry<int> GlobalCap;
    public readonly ConfigEntry<float> MinSpawnInterval;
    public readonly ConfigEntry<float> MaxSpawnInterval;
    public readonly ConfigEntry<bool> RespawnOnLoad;
    public readonly ConfigEntry<bool> DebugMode;

    // [Balance]
    public readonly ConfigEntry<int> UpperFloorSpawnShare;
    public readonly ConfigEntry<int> OutsideEnemyShare;
    public readonly ConfigEntry<bool> OldBirdUpperFloorOnly;

    // [Integration]
    public readonly ConfigEntry<int> ToilHeadSpawnChance;
    public readonly ConfigEntry<int> ToilSlayerChance;
    public readonly ConfigEntry<int> VainShroudIterations;
    public readonly ConfigEntry<bool> FeioparFakeTrees;

    /// <summary>Iterations to actually use, resolving the "0 = automatic" default.</summary>
    public int ResolveVainShroudIterations()
    {
        if (VainShroudIterations.Value > 0)
            return VainShroudIterations.Value;
        return ForName("Bush Wolf").Enabled.Value ? 12 : 0;
    }

    // [Advanced]
    public readonly ConfigEntry<bool> DespawnOnShipLeave;
    public readonly ConfigEntry<bool> CountForeignEnemies;
    public readonly ConfigEntry<float> MinDistanceFromPlayers;
    public readonly ConfigEntry<float> SpawnYOffset;
    public readonly ConfigEntry<int> AINodeCount;
    public readonly ConfigEntry<bool> RequireIndoorPoints;
    public readonly ConfigEntry<string> ExcludedEnemies;
    public readonly ConfigEntry<bool> ExcludedEnemiesIsWhitelist;
    public readonly ConfigEntry<bool> TreatEnemiesAsOutside;
    public readonly ConfigEntry<float> MaintenanceInterval;
    public readonly ConfigEntry<ForeignEnemyPolicy> ForeignEnemies;

    private readonly ConfigFile _file;
    private readonly Dictionary<string, EnemySpawnSettings> _enemySettings =
        new(StringComparer.OrdinalIgnoreCase);

    // Known vanilla enemies, keyed by EnemyType.enemyName:
    // (enabled, weight, minCount, maxCount). Unknown/modded types fall back to
    // UnknownEnemyDefaults (disabled, so nothing unexpected enters the pool).
    private static readonly (bool en, int w, int min, int max) UnknownEnemyDefaults = (false, 10, 0, 1);

    private static readonly Dictionary<string, (bool en, int w, int min, int max)> VanillaDefaults =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Interior enemies — enabled by default.
            ["Flowerman"]          = (true, 10, 0, 1), // Bracken
            ["Crawler"]            = (true, 15, 0, 2), // Thumper
            ["Hoarding bug"]       = (true, 15, 0, 3),
            ["Centipede"]          = (true, 15, 0, 3), // Snare Flea
            ["Bunker Spider"]      = (true, 12, 0, 2),
            ["Spring"]             = (true, 10, 0, 2), // Coil-Head (ToilHead target)
            ["Girl"]               = (true,  3, 0, 1), // Ghost Girl
            ["Puffer"]             = (true, 12, 0, 2), // Spore Lizard
            ["Nutcracker"]         = (true,  8, 0, 2),
            ["Jester"]             = (true,  2, 0, 1),
            ["Masked"]             = (true,  8, 0, 2),
            ["Blob"]               = (true,  8, 0, 1), // Hygrodere
            ["Butler"]             = (true,  8, 0, 2),
            ["Clay Surgeon"]       = (true,  6, 0, 2), // Barber
            ["Maneater"]           = (true,  4, 0, 1),
            ["CaveDweller"]        = (true,  4, 0, 1), // Maneater internal name in some builds
            ["Stingray"]           = (true,  8, 0, 2), // v81, hides on ceilings
            ["Feiopar"]            = (true,  6, 0, 1), // v81 PumaAI — stalks, normally from trees
            // v81 Cadaver pair: the Growth is the map-wide master that plants and
            // wakes the Blooms, so it is the one worth enabling. A Bloom spawned on
            // its own stays a dormant, invisible seed (see EnemyCatalog notes).
            ["Cadaver Growths"]    = (true,  4, 0, 1),
            ["Cadaver Bloom"]      = (false, 4, 0, 2),
            // Docile daytime critters.
            // Manticoil is disabled by default: it behaves erratically on the
            // Company moon. Enable it manually if you want it (ToilHead's
            // "Manti-Toil" integration still applies when enabled).
            ["Manticoil"]          = (false, 10, 0, 3),
            ["Tulip Snake"]        = (true, 10, 0, 3),
            ["Flowersnake"]        = (true, 10, 0, 3), // Tulip Snake internal name in some builds
            // Outdoor enemies — present in the config but disabled by default;
            // they path on the interior navmesh but look/behave oddly indoors.
            ["Baboon hawk"]        = (false, 8, 0, 2),
            ["MouthDog"]           = (false, 5, 0, 1), // Eyeless Dog
            ["ForestGiant"]        = (false, 3, 0, 1), // Forest Keeper
            ["Earth Leviathan"]    = (false, 2, 0, 1), // burrows through terrain — broken indoors
            ["RadMech"]            = (false, 2, 0, 1), // Old Bird
            ["Old Bird"]           = (false, 2, 0, 1),
            ["Bush Wolf"]          = (false, 6, 0, 1), // Kidnapper Fox — needs vain shrouds
            ["GiantKiwi"]          = (false, 4, 0, 1), // v81, large outdoor bird
            ["Red Locust Bees"]    = (false, 5, 0, 1), // needs a hive to behave properly
            ["Docile Locust Bees"] = (false, 5, 0, 2),
            ["Butler Bees"]        = (false, 3, 0, 1), // normally spawned from a dead Butler
        };

    public PluginConfig(ConfigFile file)
    {
        _file = file;

        GlobalCap = file.Bind("General", "GlobalCap", 5,
            new ConfigDescription(
                "Maximum number of enemies alive at the same time on the moon. " +
                "Keep this at or below ~10-15 to preserve FPS.",
                new AcceptableValueRange<int>(0, 40)));

        MinSpawnInterval = file.Bind("General", "MinSpawnInterval", 15f,
            new ConfigDescription("Minimum delay between spawn attempts, in seconds.",
                new AcceptableValueRange<float>(1f, 600f)));

        MaxSpawnInterval = file.Bind("General", "MaxSpawnInterval", 45f,
            new ConfigDescription("Maximum delay between spawn attempts, in seconds. " +
                "Each cycle picks a random value in [Min, Max].",
                new AcceptableValueRange<float>(1f, 600f)));

        RespawnOnLoad = file.Bind("General", "RespawnOnLoad", true,
            "If true, every landing on the Company moon despawns all existing enemies " +
            "and spawning restarts from zero. If false, existing enemies are left alone " +
            "and the spawner just tops up toward the configured limits.");

        DebugMode = file.Bind("General", "DebugMode", false,
            "Verbose logging: timer intervals, alive counts, weighted-pick results, spawn errors.");

        UpperFloorSpawnShare = file.Bind("Balance", "UpperFloorSpawnShare", 70,
            new ConfigDescription(
                "Percent of spawn points placed on the upper level (at the ship landing height). " +
                "The rest go to the lower level / basement. The basement floor is much larger, " +
                "so pure area-weighted sampling would funnel almost everything down there.",
                new AcceptableValueRange<int>(0, 100)));

        OutsideEnemyShare = file.Bind("Balance", "OutsideEnemyShare", 50,
            new ConfigDescription(
                "Percent chance each spawn picks from OUTDOOR enemy types (dogs, giants, baboon " +
                "hawks, Old Birds...) instead of indoor ones. Each pool only contains enemies " +
                "you enabled; if the rolled pool is empty the other one is used. 50 = even split.",
                new AcceptableValueRange<int>(0, 100)));

        OldBirdUpperFloorOnly = file.Bind("Balance", "OldBirdUpperFloorOnly", true,
            "Spawn the Old Bird (RadMech) only on the upper floor (ship-landing level), ignoring " +
            "UpperFloorSpawnShare for it. The Old Bird is huge and the basement is cramped, so it " +
            "moves and fights much better upstairs.");

        ToilHeadSpawnChance = file.Bind("Integration", "ToilHeadSpawnChance", 25,
            new ConfigDescription(
                "Percent chance (0-100) that a spawned Coil-Head or Manticoil gets a turret " +
                "on its head via the ToilHead mod. Ignored when ToilHead is not installed.",
                new AcceptableValueRange<int>(0, 100)));

        VainShroudIterations = file.Bind("Integration", "VainShroudIterations", 0,
            new ConfigDescription(
                "Grows vain shrouds (weeds) on the Company moon by setting the level's own " +
                "moldSpreadIterations, so the game generates and network-syncs them exactly like " +
                "on any other moon. Required by the Kidnapper Fox (Bush Wolf), which despawns " +
                "itself on spawn when there is nothing to hide in. 0 = automatic: weeds are grown " +
                "only when Bush Wolf is enabled (12 iterations). Higher = more overgrowth.",
                new AcceptableValueRange<int>(0, 40)));

        FeioparFakeTrees = file.Bind("Integration", "FeioparFakeTrees", true,
            "EXPERIMENTAL. Feiopar (PumaAI) only stalks players from trees tagged 'Tree'; the " +
            "Company building has none, so without this it just stands still. When enabled, the mod " +
            "fabricates fake tree nodes on the interior navmesh (with the overhead collider the game " +
            "checks for) so Feiopar can stalk and pounce. It may perch oddly near the ceiling — " +
            "turn this off if it looks broken. Ignored when Feiopar is disabled or absent.");

        ToilSlayerChance = file.Bind("Integration", "ToilSlayerChance", 0,
            new ConfigDescription(
                "Of the turret rolls that succeed, percent chance (0-100) the turret is the " +
                "'Slayer' (minigun) variant instead of a regular one.",
                new AcceptableValueRange<int>(0, 100)));

        DespawnOnShipLeave = file.Bind("Advanced", "DespawnOnShipLeave", true,
            "Despawn enemies created by this mod when the ship leaves the Company moon.");

        CountForeignEnemies = file.Bind("Advanced", "CountForeignEnemies", true,
            "If true, enemies spawned by the game or other mods (e.g. BrutalCompanyMinus events) " +
            "count toward GlobalCap and per-type limits, so the mods share one budget instead of " +
            "stacking on top of each other. If false, only enemies spawned by this mod are counted.");

        MinDistanceFromPlayers = file.Bind("Advanced", "MinDistanceFromPlayers", 12f,
            new ConfigDescription("Never spawn closer than this many meters to a player.",
                new AcceptableValueRange<float>(0f, 60f)));

        SpawnYOffset = file.Bind("Advanced", "SpawnYOffset", 0.25f,
            new ConfigDescription("Small vertical offset applied to spawn points so enemies " +
                "don't clip into the floor.", new AcceptableValueRange<float>(0f, 2f)));

        AINodeCount = file.Bind("Advanced", "AINodeCount", 20,
            new ConfigDescription("Number of AI patrol nodes generated on the interior navmesh " +
                "for spawned enemies to roam between.", new AcceptableValueRange<int>(4, 64)));

        RequireIndoorPoints = file.Bind("Advanced", "RequireIndoorPoints", true,
            "Only accept navmesh points that have a ceiling above them (i.e. inside the building). " +
            "Disable if the spawner reports it cannot find valid points.");

        ExcludedEnemies = file.Bind("Advanced", "ExcludedEnemies", "",
            "Comma-separated list of EnemyType names. By default this is a BLACKLIST: the listed " +
            "types are removed from the pool (on top of the built-in exclusions Lasso and Red pill). " +
            "See ExcludedEnemiesIsWhitelist to flip its meaning.");

        ExcludedEnemiesIsWhitelist = file.Bind("Advanced", "ExcludedEnemiesIsWhitelist", false,
            "When true, the ExcludedEnemies list becomes a WHITELIST: ONLY the listed types are " +
            "allowed to spawn and everything else is excluded. Lasso and Red pill stay excluded " +
            "regardless. An empty list in whitelist mode means nothing spawns. Combine with " +
            "ForeignEnemies=RemoveExcluded to also strip non-whitelisted enemies spawned by other mods.");

        TreatEnemiesAsOutside = file.Bind("Advanced", "TreatEnemiesAsOutside", true,
            "REQUIRED for enemies to be able to see, chase and kill you. The game decides whether " +
            "an enemy may target a player with 'player.isInsideFactory != enemy.isOutside'. Inside " +
            "the Company building players are NOT flagged as being in a factory, so enemies must be " +
            "flagged as outside enemies or they will walk past you and never attack. Only turn this " +
            "off for debugging.");

        MaintenanceInterval = file.Bind("Advanced", "MaintenanceInterval", 3f,
            new ConfigDescription(
                "How often (seconds) to re-apply AI settings to spawned enemies, rescue enemies that " +
                "wandered somewhere unreachable, and apply the ForeignEnemies policy.",
                new AcceptableValueRange<float>(1f, 30f)));

        ForeignEnemies = file.Bind("Advanced", "ForeignEnemies", ForeignEnemyPolicy.RemoveExcluded,
            "What to do with enemies this mod did NOT spawn (vanilla spawns, BrutalCompanyMinus, " +
            "MoreEnemies...). Ignore = leave them; RemoveExcluded = despawn types listed in " +
            "ExcludedEnemies, so the blacklist applies to the whole moon no matter who spawned them; " +
            "RemoveNotEnabled = also despawn any type with Enabled = false.");
    }

    /// <summary>
    /// Gets (binding on first use) the config block for one enemy type.
    /// Called lazily because EnemyType assets only exist once the game has loaded.
    /// </summary>
    public EnemySpawnSettings For(EnemyType type) => ForName(type.enemyName);

    /// <summary>
    /// Same as <see cref="For"/> but keyed by name, so settings can be read
    /// before the EnemyType assets are looked up (e.g. at level load).
    /// </summary>
    public EnemySpawnSettings ForName(string name)
    {
        if (_enemySettings.TryGetValue(name, out var cached))
            return cached;

        if (!VanillaDefaults.TryGetValue(name, out var d))
            d = UnknownEnemyDefaults;

        string section = "Enemy." + SanitizeForSection(name);
        var settings = new EnemySpawnSettings
        {
            Enabled = _file.Bind(section, "Enabled", d.en,
                $"Include '{name}' in the spawn pool."),
            SpawnWeight = _file.Bind(section, "SpawnWeight", d.w,
                new ConfigDescription("Relative weight for the weighted-random pick (0 disables).",
                    new AcceptableValueRange<int>(0, 100))),
            MinSpawnCount = _file.Bind(section, "MinSpawnCount", d.min,
                new ConfigDescription("The spawner tries to keep at least this many alive " +
                    "(global cap permitting).", new AcceptableValueRange<int>(0, 20))),
            MaxSpawnCount = _file.Bind(section, "MaxSpawnCount", d.max,
                new ConfigDescription("Never allow more than this many alive at once.",
                    new AcceptableValueRange<int>(0, 20))),
        };

        _enemySettings[name] = settings;
        return settings;
    }

    /// <summary>Strips characters BepInEx does not allow in section names.</summary>
    private static string SanitizeForSection(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (c is '=' or '\n' or '\t' or '\\' or '"' or '\'' or '[' or ']')
                continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }
}
