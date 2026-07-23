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

    // [ToilHead] — per-enemy turret chances (only the three ToilHead supports).
    public readonly ConfigEntry<int> CoilHeadTurretChance;
    public readonly ConfigEntry<int> CoilHeadSlayerChance;
    public readonly ConfigEntry<int> ManticoilTurretChance;
    public readonly ConfigEntry<int> ManticoilSlayerChance;
    public readonly ConfigEntry<int> MaskedTurretChance;
    public readonly ConfigEntry<int> MaskedSlayerChance;

    // [Integration]
    public readonly ConfigEntry<int> VainShroudIterations;
    public readonly ConfigEntry<int> VainShroudPatches;
    public readonly ConfigEntry<bool> FeioparFakeTrees;

    /// <summary>Turret chance (%) for a ToilHead-eligible enemy, 0 if not one.</summary>
    public int ToilHeadTurretChance(Compat.ToilHeadCompat.Kind kind) => kind switch
    {
        Compat.ToilHeadCompat.Kind.CoilHead => CoilHeadTurretChance.Value,
        Compat.ToilHeadCompat.Kind.Manticoil => ManticoilTurretChance.Value,
        Compat.ToilHeadCompat.Kind.Masked => MaskedTurretChance.Value,
        _ => 0,
    };

    /// <summary>Slayer (minigun) chance (%) once a turret is granted, per kind.</summary>
    public int ToilHeadSlayerChance(Compat.ToilHeadCompat.Kind kind) => kind switch
    {
        Compat.ToilHeadCompat.Kind.CoilHead => CoilHeadSlayerChance.Value,
        Compat.ToilHeadCompat.Kind.Manticoil => ManticoilSlayerChance.Value,
        Compat.ToilHeadCompat.Kind.Masked => MaskedSlayerChance.Value,
        _ => 0,
    };

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
            ["Tulip Snake"]        = (true, 10, 0, 3),
            ["Flowersnake"]        = (true, 10, 0, 3), // Tulip Snake internal name in some builds

            // Outdoor enemies that path fine on the interior navmesh and can
            // target players (isOutside handling) — enabled at modest weights so
            // the building gets some big threats too. Nest-requiring types (Old
            // Bird, Giant Kiwi) get their nest placed automatically; the Old
            // Bird is additionally locked to the upper floor by default.
            ["Baboon hawk"]        = (true,  8, 0, 2),
            ["MouthDog"]           = (true,  5, 0, 1), // Eyeless Dog
            ["ForestGiant"]        = (true,  3, 0, 1), // Forest Keeper
            ["RadMech"]            = (true,  3, 0, 1), // Old Bird
            ["Old Bird"]           = (true,  3, 0, 1),
            ["GiantKiwi"]          = (true,  4, 0, 1), // v81 — nests indoors, confirmed working

            // ---- Disabled by default: do NOT work correctly on Gordion yet. ----
            // Kept in the config (and code) so they can be re-enabled for testing;
            // tracked on the 'experimental' branch. See the README.
            ["Feiopar"]            = (false, 6, 0, 1), // PumaAI needs trees; idles without them
            ["Cadaver Growths"]    = (false, 4, 0, 1), // needs a dungeon ("Found no dungeon")
            ["Cadaver Bloom"]      = (false, 4, 0, 2), // dormant seed planted by the Growth
            ["Bush Wolf"]          = (false, 6, 0, 1), // Kidnapper Fox — needs vain shrouds
            ["Earth Leviathan"]    = (false, 2, 0, 1), // burrows through terrain — broken indoors
            ["Manticoil"]          = (false, 10, 0, 3), // behaves erratically here
            ["Red Locust Bees"]    = (false, 5, 0, 1), // needs a hive
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

        // [ToilHead] — the three enemies ToilHead can turret, each configurable.
        // TurretChance = % of spawns of that enemy that get a turret; SlayerChance
        // = % of those turrets that are the minigun "Slayer" variant. All ignored
        // when ToilHead is not installed.
        var pct = new AcceptableValueRange<int>(0, 100);
        CoilHeadTurretChance = file.Bind("ToilHead", "CoilHeadTurretChance", 25,
            new ConfigDescription("Percent chance a spawned Coil-Head gets a turret head.", pct));
        CoilHeadSlayerChance = file.Bind("ToilHead", "CoilHeadSlayerChance", 0,
            new ConfigDescription("Percent of Coil-Head turrets that are the Slayer (minigun) variant.", pct));
        ManticoilTurretChance = file.Bind("ToilHead", "ManticoilTurretChance", 25,
            new ConfigDescription("Percent chance a spawned Manticoil gets a turret head.", pct));
        ManticoilSlayerChance = file.Bind("ToilHead", "ManticoilSlayerChance", 0,
            new ConfigDescription("Percent of Manticoil turrets that are the Slayer (minigun) variant.", pct));
        MaskedTurretChance = file.Bind("ToilHead", "MaskedTurretChance", 0,
            new ConfigDescription("Percent chance a spawned Masked (mimic) gets a turret head.", pct));
        MaskedSlayerChance = file.Bind("ToilHead", "MaskedSlayerChance", 0,
            new ConfigDescription("Percent of Masked turrets that are the Slayer (minigun) variant.", pct));

        VainShroudIterations = file.Bind("Integration", "VainShroudIterations", 0,
            new ConfigDescription(
                "Grows vain shrouds (weeds) on the Company moon so the Kidnapper Fox (Bush Wolf) has " +
                "somewhere to hide instead of despawning itself. The mod flips Gordion's canSpawnMold " +
                "flag and sets its moldSpreadIterations in StartOfRound.LoadPlanetsMoldSpreadData (the " +
                "same hook FoxLover uses), and the vanilla pipeline then generates and network-syncs " +
                "the weeds on landing. Applied at save load, so set this and reload the save / fly " +
                "fresh. 0 = automatic: weeds grow only when Bush Wolf is enabled (12 iterations). " +
                "Higher = more overgrowth.",
                new AcceptableValueRange<int>(0, 40)));

        VainShroudPatches = file.Bind("Integration", "VainShroudPatches", 1,
            new ConfigDescription(
                "How many separate weed patches to grow, each at its own random spot on the " +
                "interior navmesh. The location is re-rolled every landing, so the Fox's nest is " +
                "never in the same place twice. Raise this for several overgrown areas.",
                new AcceptableValueRange<int>(1, 4)));

        FeioparFakeTrees = file.Bind("Integration", "FeioparFakeTrees", false,
            "EXPERIMENTAL. Feiopar (PumaAI) only stalks players from trees tagged 'Tree'; the " +
            "Company building has none, so without this it just stands still. When enabled, the mod " +
            "fabricates fake tree nodes on the interior navmesh (with the overhead collider the game " +
            "checks for) so Feiopar can stalk and pounce. It may perch oddly near the ceiling — " +
            "turn this off if it looks broken. Ignored when Feiopar is disabled or absent.");

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
