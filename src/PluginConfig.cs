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
    public readonly ConfigEntry<bool> AllowHarmlessCreatures;
    public readonly ConfigEntry<bool> EarthLeviathanFloorEmerge;

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
    public readonly ConfigEntry<bool> FeioparDeadTrees;
    public readonly ConfigEntry<int> FeioparTreeCount;
    public readonly ConfigEntry<bool> CadaverBloomTraps;
    public readonly ConfigEntry<float> CadaverBloomTriggerRange;

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

    /// <summary>
    /// The stock BCMER event names allowed on this moon, resolving the
    /// "empty = use the built-in list" default. Order is preserved so the log
    /// reads the same way the config does.
    /// </summary>
    public List<string> ResolveStockEventWhitelist()
    {
        string raw = StockEventWhitelist.Value;
        if (string.IsNullOrWhiteSpace(raw))
            raw = DefaultStockEvents;

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in raw.Split(','))
        {
            string name = part.Trim();
            if (name.Length > 0 && seen.Add(name))
                names.Add(name);
        }
        return names;
    }

    /// <summary>Iterations to actually use, resolving the "0 = automatic" default.</summary>
    public int ResolveVainShroudIterations()
    {
        if (VainShroudIterations.Value > 0)
            return VainShroudIterations.Value;
        return ForName("Bush Wolf").Enabled.Value ? 12 : 0;
    }

    // [BrutalCompany]
    public readonly ConfigEntry<bool> EnableStockEvents;
    public readonly ConfigEntry<string> StockEventWhitelist;
    public readonly ConfigEntry<bool> AnnounceEvents;

    // [Horde]
    public readonly ConfigEntry<bool> HordeEnabled;
    public readonly ConfigEntry<string> HordeEnemy;
    public readonly ConfigEntry<int> HordeCountPerSide;
    public readonly ConfigEntry<int> HordeDelaySeconds;
    public readonly ConfigEntry<int> HordeRepeatSeconds;
    public readonly ConfigEntry<bool> HordeAnnounce;

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
    public readonly ConfigEntry<bool> LegacyCapsMigrated;

    private readonly ConfigFile _file;
    private readonly Dictionary<string, EnemySpawnSettings> _enemySettings =
        new(StringComparer.OrdinalIgnoreCase);

    // Default per-type MaxSpawnCount. Deliberately high so no monster is capped
    // below GlobalCap by default — users set a lower per-type value themselves if
    // they want to limit a specific enemy. The 'max' fields in the table below are
    // now only used as a legacy hint and no longer drive the default.
    // Stock BCMER events that can actually work on this moon. The rule behind the
    // list: no dungeon, no scrap generation, and - most importantly - nothing that
    // edits the level's enemy lists, since BCMER skips its own snapshot pass here
    // and would have nothing to restore them from. Monsters are this mod's job.
    private const string DefaultStockEvents =
        "Nothing, Gloomy, Meteors, OutsideLandmines, OutsideTurrets, AllWeather, " +
        "DoorFailure, ShipLightsFailure, ManualCameraFailure, IsMetal, " +
        "LeaflessTrees, LeaflessBrownTrees";

    // The 1.6.0 list, kept only to recognise a whitelist the user never edited.
    // Warzone was in it and can never fire here: its own gate wants a turret or a
    // landmine in the level's spawnableMapObjects, and this moon's array is empty.
    private const string LegacyStockEvents =
        "Nothing, Gloomy, Meteors, OutsideLandmines, OutsideTurrets, Warzone, AllWeather";

    private const int MaxSpawnDefault = 40;

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

            ["Bush Wolf"]          = (false, 6, 0, 1), // Kidnapper Fox — needs vain shrouds
            ["Manticoil"]          = (false, 10, 0, 3), // behaves erratically here

            // Ambient swarms — enabled at low weight. Docile Locust Bees are the
            // harmless firefly swarm (daytime); the two aggressive bee swarms work
            // via the mod's isOutside handling.
            ["Red Locust Bees"]    = (true,  4, 0, 1),
            ["Docile Locust Bees"] = (false, 5, 0, 2), // Roaming Locust — disabled (not visible/useful indoors)
            ["Butler Bees"]        = (true,  3, 0, 1),

            // ---- Experimental: disabled by default, made to work on Gordion via
            // dedicated mechanics (see the spawner). Enable to test. ----
            ["Earth Leviathan"]    = (false, 2, 0, 1), // breaches up through the floor
            ["Feiopar"]            = (false, 6, 0, 1), // stalks from generated dead trees
            ["Cadaver Bloom"]      = (false, 4, 0, 2), // standalone burst trap (no Growth/dungeon)
            // Cadaver Growths is intentionally NOT listed: it hard-requires a DunGen
            // dungeon and cannot work on the Company moon. Falls back to disabled.
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

        AllowHarmlessCreatures = file.Bind("Balance", "AllowHarmlessCreatures", true,
            "Master switch for the genuinely harmless ambient creatures — the Manticoil (bird) and " +
            "the Roaming/Docile Locust swarm. Set false to keep the building free of just those two, " +
            "regardless of their per-enemy Enabled setting. (Tulip Snake is not affected — it can " +
            "grab players and stays in the normal pool.)");

        EarthLeviathanFloorEmerge = file.Bind("Balance", "EarthLeviathanFloorEmerge", true,
            "EXPERIMENTAL. Lets the Earth Leviathan (worm) breach up through the Company building " +
            "floor. The worm normally only emerges through natural ground, which Gordion's interior " +
            "lacks, so without this it roams under the floor forever and never attacks. Disable to " +
            "leave the worm inert (or if it emerges through unwanted surfaces).");

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

        FeioparDeadTrees = file.Bind("Integration", "FeioparDeadTrees", true,
            "EXPERIMENTAL. Feiopar (PumaAI) only stalks players from trees tagged 'Tree'; the " +
            "Company building has none, so without this it just stands still. When enabled, the mod " +
            "grows simple dead-tree trunks on the interior navmesh (with the overhead canopy collider " +
            "the game validates against) so Feiopar can climb, stalk from ~3 m up, and pounce. " +
            "Ignored when Feiopar is disabled or absent.");

        FeioparTreeCount = file.Bind("Integration", "FeioparTreeCount", 14,
            new ConfigDescription("How many dead trees to grow for Feiopar to stalk and jump between " +
                "(placed mostly on the open upper floor).",
                new AcceptableValueRange<int>(3, 30)));

        CadaverBloomTraps = file.Bind("Integration", "CadaverBloomTraps", true,
            "EXPERIMENTAL. Cadaver Bloom normally needs its map-wide Growth (which requires a dungeon " +
            "the Company building lacks). When enabled, the mod instead plants Blooms directly at " +
            "random spots as standalone corpse traps that burst and chase when a player walks close, " +
            "so no dungeon is needed. Ignored when Cadaver Bloom is disabled or absent.");

        CadaverBloomTriggerRange = file.Bind("Integration", "CadaverBloomTriggerRange", 4f,
            new ConfigDescription("How close a player must get (meters) for a planted Cadaver Bloom to burst.",
                new AcceptableValueRange<float>(1.5f, 12f)));

        EnableStockEvents = file.Bind("BrutalCompany", "EnableStockEvents", false,
            "Let a curated set of stock BrutalCompanyMinusExtraReborn events run on the Company " +
            "moon. BCMER itself skips this moon entirely (its LoadNewLevel prefix returns early on " +
            "levelID 3), so nothing here happens without this mod. Only events that do not touch the " +
            "level's enemy lists are eligible, because BCMER never snapshots those on this moon and " +
            "could not restore them. Your BCMER config is always obeyed: an event that is disabled, " +
            "whose type has weight 0, or whose moon lists exclude Gordion, will not run.");

        StockEventWhitelist = file.Bind("BrutalCompany", "StockEventWhitelist", DefaultStockEvents,
            "Comma-separated BCMER event names allowed on the Company moon, checked against your " +
            "BCMER config before each landing. Empty means the built-in list. Extras you can add " +
            "at your own risk: DoorCircuitFailure (shuts the ship door 3PM-10PM, so it needs a mod " +
            "running the clock here and can strand you), ShipmentFees, LateShip and VeryLateShip " +
            "(they move the departure time, which can fight a mod that leaves at midnight), and " +
            "BigBonus. Scrap and item events (Clock, ToiletPaper, TransmuteScrapBig and the rest) " +
            "are pointless here: this moon generates no loot for them to act on, and they share " +
            "BCMER's one-transmutation-per-round flag, which this moon never clears. Names not " +
            "listed here are usually blocked by a gate of their own - the log always says which.");

        // The stock list gained six events in 1.8.0 and lost Warzone, but BepInEx
        // never overwrites a value that already exists, so an upgrade would have
        // silently kept the old seven. Only a whitelist still identical to the old
        // default is replaced, so an edited one is never touched.
        if (StockEventWhitelist.Value != null
            && StockEventWhitelist.Value.Trim() == LegacyStockEvents)
        {
            StockEventWhitelist.Value = DefaultStockEvents;
            Plugin.Log.LogInfo(
                "Config migration: StockEventWhitelist was still the 1.6.0 list, replaced with the " +
                "current one (Warzone dropped, it can never fire on this moon; six events added).");
        }

        AnnounceEvents = file.Bind("BrutalCompany", "AnnounceEvents", true,
            "Announce the chosen event in chat, the way BrutalCompanyMinus announces its own.");

        HordeEnabled = file.Bind("Horde", "Enabled", true,
            "A group of enemies closes in from both far edges of the map on every landing at the " +
            "Company. This is a standing feature of the moon, not a random event: it does not take " +
            "part in the [BrutalCompany] roll and does not compete with the events there, so a " +
            "landing can have both. The edges are worked out from the navmesh as the mod runs, so " +
            "moon overhaul mods that reshape the outdoor area do not strand the arrivals.");

        HordeEnemy = file.Bind("Horde", "Enemy", "Masked",
            "Which enemy arrives, by its in-game name (Masked, Flowerman, Crawler, Spring, " +
            "Baboon hawk...). It must also be spawnable — an enemy your ExcludedEnemies list bars " +
            "will not arrive, and the reason is logged.");

        HordeCountPerSide = file.Bind("Horde", "CountPerSide", 5,
            new ConfigDescription("How many arrive at each of the two edges, so the total is twice " +
                "this. The horde deliberately ignores GlobalCap — one a cap could shave down to " +
                "two would not be a horde.",
                new AcceptableValueRange<int>(1, 20)));

        HordeDelaySeconds = file.Bind("Horde", "DelaySeconds", 240,
            new ConfigDescription("Seconds after landing before they arrive. Real time, so it " +
                "behaves the same whether or not a mod runs the clock on this moon.",
                new AcceptableValueRange<int>(10, 1800)));

        HordeRepeatSeconds = file.Bind("Horde", "RepeatSeconds", 0,
            new ConfigDescription("0 arrives once per landing. Any other value is the gap between " +
                "waves, which then keep coming for as long as you stay.",
                new AcceptableValueRange<int>(0, 1800)));

        HordeAnnounce = file.Bind("Horde", "Announce", true,
            "Announce each wave in chat as it arrives.");

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

        LegacyCapsMigrated = file.Bind("Advanced", "LegacyCapsMigrated", false,
            "Set automatically. Older versions shipped a low per-enemy MaxSpawnCount (1-3), and " +
            "BepInEx never overwrites values that already exist in a config file — so upgrading " +
            "kept those caps and GlobalCap could not be reached. On first run the mod raises any " +
            "MaxSpawnCount still sitting on its old default up to the new one and sets this to true, " +
            "so it only ever happens once. Values you changed yourself are left alone.");
    }

    /// <summary>Marks the one-time legacy cap migration as done (called after all enemies bind).</summary>
    public void FinishLegacyCapMigration()
    {
        if (!LegacyCapsMigrated.Value)
            LegacyCapsMigrated.Value = true;
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
                    "(global cap permitting).", new AcceptableValueRange<int>(0, 40))),
            // Default is unrestricted (only GlobalCap limits the moon's population);
            // set a lower value here if you want to cap a specific enemy type.
            MaxSpawnCount = _file.Bind(section, "MaxSpawnCount", MaxSpawnDefault,
                new ConfigDescription("Never allow more than this many of this type alive at once. " +
                    "Defaults high so only GlobalCap limits the total — lower it to cap this type.",
                    new AcceptableValueRange<int>(0, 40))),
        };

        // One-time migration for configs written by older versions. Those shipped a
        // low per-enemy cap (1-3) and BepInEx keeps existing values on upgrade, so the
        // moon stayed capped far below GlobalCap. Only raise a value that still equals
        // that enemy's OLD default — anything the user tuned themselves is untouched.
        if (!LegacyCapsMigrated.Value
            && d.max < MaxSpawnDefault
            && settings.MaxSpawnCount.Value == d.max)
        {
            settings.MaxSpawnCount.Value = MaxSpawnDefault;
            Plugin.Log.LogInfo(
                $"Config migration: '{name}' MaxSpawnCount {d.max} -> {MaxSpawnDefault} " +
                "(old per-enemy cap raised; GlobalCap is now the limit).");
        }

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
