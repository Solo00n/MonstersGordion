<!-- The GIF and screenshot use absolute raw.githubusercontent URLs so they render
     on BOTH GitHub and the Thunderstore page. They resolve once `main` (with the
     files under docs/) is pushed to GitHub. Drop your demo GIF at docs/demo.gif. -->

![MonstersGordion](https://raw.githubusercontent.com/Solo00n/MonstersGordion/main/docs/demo.gif)

# MonstersGordion

**Bring the monsters to the Company.** MonstersGordion spawns vanilla Lethal
Company enemies **inside the Company building on 71-Gordion** — the one moon that
is normally completely safe — on the navmesh baked by **NavMeshInCompanyRedux**.
Timers, weights, caps, floor balance and every enemy are fully configurable.

Host-authoritative: only the host spawns; clients receive enemies through the
game's own netcode. Built and tested against game **v81**.

![Monsters in the Company building](https://raw.githubusercontent.com/Solo00n/MonstersGordion/main/docs/company-monsters.jpg)

## What it does

- A timer picks a random interval and, while below the global cap, spawns a
  weighted-random enemy on a **reachability-checked** point of the interior
  navmesh — no monsters stuck on roofs, shelves or in pits.
- **Upper/lower floor balance** and an **outdoor/indoor pool split** so both the
  cramped basement and the open landing level stay populated.
- Every spawnable enemy gets its own config block (`Enabled`, `SpawnWeight`,
  `MinSpawnCount`, `MaxSpawnCount`). By default only **GlobalCap** limits the
  moon's total population — cap individual types yourself if you want.
- A **blacklist / whitelist** for the whole moon (optionally applied to enemies
  other mods spawn too), and a one-switch toggle for harmless ambient creatures.

## Monsters

**Work great (on by default):** every interior enemy — Bracken, Thumper, Hoarding
Bug, Snare Flea, Bunker Spider, Coil-Head, Ghost Girl, Spore Lizard, Nutcracker,
Jester, Masked, Hygrodere, Butler, Barber, Maneater — plus **Stingray**, **Tulip
Snake**, the outdoor threats that path fine indoors (**Baboon Hawk, Eyeless Dog,
Forest Keeper, Old Bird, Giant Kiwi** — nest-requiring ones get their nest placed
automatically) and the ambient **bee swarms**.

**Made to work here (experimental, opt-in):**

- 🐆 **Feiopar** (leopard) — the mod grows real dead trees on the navmesh so it can
  climb, perch and pounce, just like outdoors.
- 🪱 **Earth Leviathan** (worm) — breaches up through the Company floor.
- 🌸 **Cadaver Bloom** — planted as standalone corpse traps that burst and chase
  when you walk close (no dungeon needed).

Every landing writes a spawnability report to the log, and any enemy that keeps
dying instantly is auto-disabled so nothing gets spammed.

## Integrations (all optional, auto-detected)

| Mod | What it adds |
|---|---|
| **NavMeshInCompanyRedux** | required — the interior navmesh everything spawns on |
| **ToilHead** | per-enemy turret chances for Coil-Head, Manticoil and Masked |
| **StarlancerAIFix** | its `EnemyAI.Start` fix applies automatically |
| **BrutalCompanyMinus(ExtraReborn)** | shares one enemy budget via `CountForeignEnemies` |
| **FoxLover** technique | vain shrouds grown so the Kidnapper Fox can live here |

## Installation

Install with a mod manager (r2modman / Thunderstore Mod Manager) and dependencies
are pulled in automatically. Manual: drop `MonstersGordion.dll` into
`BepInEx/plugins/` and install the dependencies yourself. Configuration lives in
`BepInEx/config/Timofey.MonstersGordion.cfg` and applies on game restart.

## Config highlights

| Section | Key | Default | Meaning |
|---|---|---|---|
| General | GlobalCap | 5 | max enemies alive at once (raise for chaos) |
| General | MinSpawnInterval / MaxSpawnInterval | 15 / 45 | random delay range, seconds |
| Balance | UpperFloorSpawnShare | 70 | % of spawns at the ship-landing level |
| Balance | OutsideEnemyShare | 50 | % chance to pick an outdoor enemy type |
| Balance | AllowHarmlessCreatures | true | toggle Manticoil + Roaming Locust swarm |
| Advanced | ExcludedEnemies / ExcludedEnemiesIsWhitelist | — | blacklist, or flip to a whitelist |
| Advanced | ForeignEnemies | RemoveExcluded | apply the list to other mods' spawns too |

Per-enemy sections (`[Enemy.Flowerman]`, …) set `Enabled`, `SpawnWeight`,
`MinSpawnCount`, `MaxSpawnCount`. `MaxSpawnCount` defaults high, so `GlobalCap` is
the only limit until you cap a type yourself.

## Credits

Made by Timofey. Vain-shroud technique inspired by ButteryStancakes' **FoxLover**.
Not affiliated with Zeekerss or Lethal Company.
