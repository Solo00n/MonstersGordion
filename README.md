# MonstersGordion

Dynamic, configurable spawning of vanilla Lethal Company monsters **inside the
Company building on 71-Gordion**, on the navmesh baked by
**NavMeshInCompanyRedux**. Host-authoritative: only the host spawns, clients
receive enemies through the game's own netcode — so only the host strictly
needs this mod (everyone needs NavMeshInCompanyRedux).

Built and tested against game **v81**.

## Features

- Timer-driven spawn cycle with a random interval in `[MinSpawnInterval, MaxSpawnInterval]`.
- Per-enemy config blocks (`Enabled`, `SpawnWeight`, `MinSpawnCount`, `MaxSpawnCount`)
  generated for every spawnable enemy type found in the game, modded ones included
  (unknown types default to disabled).
- Global alive cap; enemies from the game or other mods count toward it by default,
  so combined populations stay bounded.
- **Reachability-safe spawn points**: the interior navmesh is split into connected
  regions and every spawn point / patrol node must have a complete path to the
  main hall — no more monsters stuck on roof patches, shelf tops or in pits.
- **Floor balance** (`UpperFloorSpawnShare`): configurable split between the
  ship-landing level and the basement, instead of pure area weighting.
- **Outdoor/indoor pool split** (`OutsideEnemyShare`): each spawn rolls which pool
  to pick from, so dogs/giants/Old Birds actually show up when enabled.
- Interior patrol nodes are generated on the navmesh (the Company building has
  none), and spawned enemies are forced into interior AI mode.
- `RespawnOnLoad`: wipe and repopulate on every landing, or top up existing.
- Verbose `DebugMode` logging of every decision the spawner makes.

## Dependencies

Required (declared in the Thunderstore manifest):

| Package | Tested version |
|---|---|
| `BepInEx-BepInExPack` | 5.4.2305 |
| `TRizzle-NavMeshInCompanyRedux` | 1.2.0 |

Optional, auto-detected at runtime (no hard dependency):

| Package | Tested version | Integration |
|---|---|---|
| `Zehs-ToilHead` | 1.9.1 | per-enemy turret chances in the `[ToilHead]` config section — Coil-Head, Manticoil and Masked, each with a Slayer (minigun) sub-chance |
| `AudioKnight-StarlancerAIFix` | 3.13.2 | its `EnemyAI.Start` postfix applies automatically; this mod's interior fix-up runs after it |
| `SoftDiamond-BrutalCompanyMinusExtraReborn` | 1.70.1 | shared enemy budget via `CountForeignEnemies` |

## Installation

**r2modman / Thunderstore Mod Manager**: install from Thunderstore; dependencies
are pulled in automatically.

**Manual**: drop `MonstersGordion.dll` into `BepInEx/plugins/` and install the
required dependencies yourself.

## Configuration

`BepInEx/config/Timofey.MonstersGordion.cfg`, created on first run. Changes
require a game restart (standard BepInEx behaviour).

| Section | Key | Default | Meaning |
|---|---|---|---|
| General | GlobalCap | 5 | max enemies alive at once |
| General | MinSpawnInterval / MaxSpawnInterval | 15 / 45 | random delay range, seconds |
| General | RespawnOnLoad | true | wipe + restart population on every landing |
| General | DebugMode | false | verbose spawn logging |
| Balance | UpperFloorSpawnShare | 70 | % of spawns at ship-landing level (rest → basement) |
| Balance | OutsideEnemyShare | 50 | % chance to pick from outdoor enemy types |
| Balance | OldBirdUpperFloorOnly | true | spawn the Old Bird only on the upper floor |
| Balance | AllowHarmlessCreatures | true | master switch for Manticoil + Roaming/Docile Locust swarm |
| Balance | EarthLeviathanFloorEmerge | true | let the worm breach up through the building floor (experimental) |
| ToilHead | CoilHeadTurretChance / CoilHeadSlayerChance | 25 / 0 | % turret on a Coil-Head, and % of those that are the minigun Slayer |
| ToilHead | ManticoilTurretChance / ManticoilSlayerChance | 25 / 0 | same, for Manticoil |
| ToilHead | MaskedTurretChance / MaskedSlayerChance | 0 / 0 | same, for Masked (mimic) |
| Integration | VainShroudIterations / VainShroudPatches | 0 / 1 | grow weeds for Bush Wolf (0 = auto); number of random patches |
| Integration | FeioparDeadTrees / FeioparTreeCount | true / 10 | grow dead trees so Feiopar can stalk (experimental) |
| Integration | CadaverBloomTraps / CadaverBloomTriggerRange | true / 4 | plant standalone Cadaver Bloom burst traps (experimental) |
| Advanced | DespawnOnShipLeave | true | remove this mod's enemies when leaving |
| Advanced | CountForeignEnemies | true | other mods' enemies count toward caps |
| Advanced | MinDistanceFromPlayers | 12 | min spawn distance to players, meters |
| Advanced | AINodeCount | 20 | generated interior patrol nodes |
| Advanced | RequireIndoorPoints | true | require a ceiling above spawn points |
| Advanced | ExcludedEnemies | (empty) | comma-separated blacklist (or whitelist, see below) |
| Advanced | ExcludedEnemiesIsWhitelist | false | flip the list into an allow-list |
| Advanced | TreatEnemiesAsOutside | true | required for enemies to be able to target players (see below) |
| Advanced | MaintenanceInterval | 3 | seconds between AI re-apply / rescue / cleanup passes |
| Advanced | ForeignEnemies | RemoveExcluded | apply the blacklist to enemies spawned by other mods |

### Why `TreatEnemiesAsOutside` exists

The game gates targeting behind `player.isInsideFactory != enemy.isOutside`.
Players inside the Company building are **not** flagged as being in a factory
(there is no EntranceTeleport there), so enemies must be flagged as outdoor
enemies or `PlayerIsTargetable` — and with it every chase and every
collision kill — silently fails. The mod still assigns interior patrol nodes,
so enemies stay in the building. Only turn this off for debugging.

### Which enemies are enabled by default

**Enabled — work well:** all interior enemies (Bracken, Thumper, Hoarding Bug,
Snare Flea, Bunker Spider, Coil-Head, Ghost Girl, Spore Lizard, Nutcracker,
Jester, Masked, Hygrodere, Butler, Barber, Maneater), plus **Stingray** and
**Tulip Snake**, the outdoor threats that path fine indoors — **Baboon Hawk,
Eyeless Dog, Forest Keeper, Old Bird and Giant Kiwi** — and the ambient swarms
**Docile / Red Locust Bees and Butler Bees**. Nest-requiring types (Old Bird,
Giant Kiwi) get their nest placed on the interior navmesh automatically; the Old
Bird is additionally locked to the upper floor.

Set **`[Balance] AllowHarmlessCreatures = false`** to keep the building free of the
two genuinely harmless ambient creatures — the Manticoil and the Roaming/Docile
Locust swarm — regardless of their individual `Enabled`. (Tulip Snake can grab
players, so it is not affected and stays in the normal pool.)

**Bush Wolf** (Kidnapper Fox) works when enabled: the mod grows vain shrouds on
Gordion by calling `MoldSpreadManager.GenerateMold` directly on landing (the
vanilla level-load path never runs here under LethalLevelLoader). Tune with
`[Integration] VainShroudIterations` / `VainShroudPatches`.

**Experimental — disabled by default, made to work via dedicated mechanics** (each
has a config toggle; may look imperfect indoors):

- **Feiopar** (leopard, PumaAI) stalks only from trees. `[Integration]
  FeioparDeadTrees` grows dead-tree trunks on the navmesh (with the canopy collider
  the game validates); it perches ~3 m up and jumps between them.
- **Earth Leviathan** (worm, SandWormAI) only emerges through natural ground, which
  the interior lacks. `[Balance] EarthLeviathanFloorEmerge` adds the building floor
  to the game's `naturalSurfaceTags` so it breaches up through the floor.
- **Cadaver Bloom** normally needs its Growth (a dungeon). `[Integration]
  CadaverBloomTraps` plants Blooms directly as standalone corpse traps that burst
  and chase when a player walks within `CadaverBloomTriggerRange`.
- **Cadaver Growths** is *not* supported — it hard-requires a DunGen dungeon the
  Company building doesn't have; it stays disabled.

These experimental fixes are host-side (the host decides behaviour). Every landing
also writes a spawnability report to the log, and the mod auto-disables any type
that keeps dying within seconds so nothing gets spammed.

Per-enemy sections: `[Enemy.Flowerman]`, `[Enemy.Bunker Spider]`, … with
`Enabled`, `SpawnWeight`, `MinSpawnCount`, `MaxSpawnCount`. `Lasso` and
`Red pill` are hard-excluded.

## Building from source

Requires the .NET SDK 8+ and NuGet access — game assemblies come from the
`LethalCompany.GameLibs.Steam` package (stripped + publicized), no manual DLL
copying.

```
dotnet build -c Release
```

Output: `bin/Release/netstandard2.1/MonstersGordion.dll`. Set the `LC_PLUGINS`
environment variable to your plugins folder to auto-copy after each build.

To assemble the Thunderstore package zip:

```
powershell -ExecutionPolicy Bypass -File build-package.ps1
```

## Project layout

```
MonstersGordion/
├── MonstersGordion.csproj      # SDK project, NuGet-only references
├── NuGet.config                # nuget.org + BepInEx feed
├── manifest.json               # Thunderstore manifest
├── icon.png                    # Thunderstore icon (256x256)
├── CHANGELOG.md
├── build-package.ps1           # builds dist/MonstersGordion-<ver>.zip
└── src/
    ├── Plugin.cs               # BepInEx entry point, Harmony bootstrap
    ├── PluginConfig.cs         # all config entries incl. per-enemy blocks
    ├── EnemyCatalog.cs         # EnemyType discovery + exclusions
    ├── NavMeshSampler.cs       # tiered, reachability-checked point sampling
    ├── LandingWatcher.cs       # polling-based landing/leave detection
    ├── CompanyMonsterSpawner.cs# timer, pools, weighted pick, caps, cleanup
    ├── Patches/                # ShipLeave/OnDestroy cleanup, death logging
    └── Compat/                 # ToilHead, StarlancerAIFix, BCME, scanner
```

## How it works (short)

On every landing on Gordion (detected by polling `StartOfRound.shipHasLanded`,
host only) the mod triangulates the navmesh, welds it into connected regions,
anchors reachability in the main hall, generates patrol nodes, and starts the
spawn coroutine. Each cycle: roll interval → check `GlobalCap` → fill
`MinSpawnCount` deficits first, otherwise roll outdoor/indoor pool → weighted
pick → tiered reachable spawn point → `RoundManager.SpawnEnemyGameObject` →
two frames later force interior AI (`SetEnemyOutside(false)`, custom node set,
agent warp) and roll the ToilHead turret. Leaving the moon stops everything and
(by default) despawns the mod's enemies.
