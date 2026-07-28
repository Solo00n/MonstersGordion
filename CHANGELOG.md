# Changelog

## 1.4.1

- **`AllowHarmlessCreatures`** (renamed from `AllowDaytimeEnemies`) now governs only the
  two genuinely harmless ambient creatures — Manticoil and the Roaming/Docile Locust swarm.
  Tulip Snake (which can grab players) is no longer swept up by it and always stays in the
  normal spawn pool.

## 1.4.0

Ambient swarms, a harmless-creature master switch, and working versions of the three
previously-broken monsters — each traced to its exact cause in the game code.

- **Ambient swarms enabled** at low weight by default: Docile Locust Bees (harmless),
  Red Locust Bees and Butler Bees.
- **`[Balance] AllowHarmlessCreatures`** (default true): one switch to keep the building
  free of the harmless ambient creatures (Manticoil and the Roaming/Docile Locust swarm).
- **Earth Leviathan (worm) now attacks.** `SandWormAI.StartEmergeAnimation` only emerges
  where the surface is "natural" (a `naturalSurfaceTags` tag), and with no Terrain on the
  Company moon it cancelled every emerge and roamed under the floor forever. New
  `[Balance] EarthLeviathanFloorEmerge` adds the interior floor tag(s) to
  `naturalSurfaceTags` so it breaches up through the floor (restored on leave).
- **Feiopar (leopard) now stalks.** PumaAI hunts only from `Tree`-tagged objects and perches
  ~3 m above ground next to them (it does not climb a mesh). `[Integration] FeioparDeadTrees`
  grows visible dead-tree trunks on the navmesh, each with the canopy collider PumaAI
  validates, so it stalks and jumps between them. `FeioparTreeCount` controls how many.
- **Cadaver Bloom now works without a dungeon.** The Growth master hard-requires a DunGen
  dungeon (impossible in the Company building); the Bloom does not. `[Integration]
  CadaverBloomTraps` plants Blooms directly at random spots and bursts them when a player
  comes within `CadaverBloomTriggerRange`, so they act as standalone corpse traps.
  Cadaver Growths remains unsupported (needs a dungeon).
- Experimental monster fixes are host-side and off by default; enable per-enemy to test.

## 1.3.2

- **Vain shrouds no longer grow in the same place every time.** 1.3.1 spread them from
  the navmesh anchor, which is computed deterministically, so the Fox's nest always
  landed on the exact same spot. The origin is now a fresh random reachable point on
  the interior navmesh each landing (50/50 between floors, clear of where players stand
  when the ship lands). Since `GenerateMold` seeds its spread from that position, the
  shape of the patch changes too.
- New `[Integration] VainShroudPatches` (default 1, max 4) grows several separate
  overgrown areas, each at its own random spot.

## 1.3.1

- **Vain shrouds are now grown directly, so Bush Wolf finally works.** 1.3.0 seeded
  the level's `moldSpreadIterations` and relied on the vanilla level-load pipeline to
  generate the weeds — but in LethalLevelLoader modpacks that pipeline never runs on
  Gordion (neither our hooks nor the game's own mold logging appear at all). The mod
  now calls `MoldSpreadManager.GenerateMold` itself on landing. That call is
  self-contained: it places the mold props and then runs `grassInstancer.BatchChildren()`
  and `GetBiggestWeedPatch()`, which is exactly what `BushWolfEnemy`'s `GetWeeds()`
  check reads, so the Fox survives instead of despawning.
- The `LoadPlanetsMoldSpreadData` hook from 1.3.0 is kept for setups where the vanilla
  path does work; the direct call is the fallback that makes it reliable here.
- Weed generation is host-side. The host decides whether the Fox lives, so it works —
  but other players may not see the weed props themselves.

## 1.3.0

- **Vain shrouds now actually grow on Gordion**, so the Kidnapper Fox (Bush Wolf)
  can hide and survive. Replaced the old `RoundManager.LoadNewLevel` hook (which
  LethalLevelLoader bypassed, so it never fired) with a postfix on
  `StartOfRound.LoadPlanetsMoldSpreadData` — the canonical, core save-load method
  where the game decides weed amounts, the same hook the FoxLover mod uses. The
  Company moon ships with `canSpawnMold = false`; the mod flips it on and seeds
  `moldSpreadIterations`, then the vanilla pipeline generates and network-syncs the
  weeds on landing. Controlled by `[Integration] VainShroudIterations` (0 = auto,
  grows weeds only when Bush Wolf is enabled). Set it and reload the save / fly
  fresh — weeds are decided at save load, not mid-round.
- Original implementation written from the decompiled game API; credit to
  ButteryStancakes' FoxLover (GPL) for demonstrating the hook. No FoxLover code
  is included.

## 1.2.1

- New icon: a red bestiary-scan look (dark red scanlines, vignette, corner
  viewfinder brackets, blood glow). No code changes — republished so the store
  page shows the new artwork.

## 1.2.0

- **Per-enemy ToilHead settings for every turret-capable enemy.** ToilHead can put
  a turret on three enemies — Coil-Head, Manticoil and **Masked** — and each has a
  regular and a Slayer (minigun) variant. Previously only Coil-Head and Manticoil
  shared a single chance and Masked could not be configured at all. The old
  `[Integration] ToilHeadSpawnChance` / `ToilSlayerChance` are replaced by a
  `[ToilHead]` section: `CoilHeadTurretChance`/`CoilHeadSlayerChance`,
  `ManticoilTurretChance`/`ManticoilSlayerChance`, `MaskedTurretChance`/`MaskedSlayerChance`.
  Masked support was added to the reflection bindings (`SetToilMaskedOnServer` /
  `SetSlayerMaskedOnServer`).

## 1.1.1

- **Giant Kiwi enabled by default** — confirmed working: it is nest-requiring and
  the mod already places its nest on the interior navmesh automatically (the 1.1.0
  notes wrongly listed it as unconfirmed).
- Removed the stale Old Bird note ("cause not yet confirmed") — its instant-death
  was the missing nest, which automatic nest placement fixed; working enemies no
  longer log warning-level notes on every landing.

## 1.1.0

First stable release. Bundles every fix from the 1.0.x line and ships a curated
default roster of enemies that work correctly inside the Company building.

- **Enabled by default:** all interior enemies plus Stingray and Tulip Snake, and
  the outdoor threats that path fine indoors — Baboon Hawk, Eyeless Dog, Forest
  Keeper and Old Bird (the Old Bird has its nest placed automatically and is
  locked to the upper floor).
- **Disabled by default** (they don't work correctly on Gordion yet, but remain in
  the config/code and on the `experimental` git branch): Feiopar, Cadaver Growths,
  Cadaver Bloom, Bush Wolf, Earth Leviathan, Giant Kiwi, Manticoil and the bees.
  The experimental `FeioparFakeTrees` also defaults off.
- Includes: enemy targeting fix (`TreatEnemiesAsOutside`), reachability-checked
  spawns with upper/lower floor balance, whitelist mode, per-mod foreign-enemy
  policy, automatic nest placement for nest-requiring enemies, the maintenance
  pass (AI re-apply, stranded-enemy rescue, idle-stalker nudge), auto-disable of
  types that keep dying instantly, and full spawnability logging.

## 1.0.12

- **Diagnostic for the vain-shroud hook.** `RoundManager.LoadNewLevel` is patched to
  grow weeds, but its log never appeared — the hook may be bypassed (LethalLevelLoader
  reworks level loading) or bail on the level check. The prefix now logs unconditionally
  at entry (planet, scene, levelID, isCompany) so the next landing shows exactly whether
  it fires and why weed generation is or isn't happening.

## 1.0.11

- **No more spawn spam from enemies that can't survive here.** Some enemies destroy
  themselves on spawn via `Destroy()` (not `KillEnemy`), so the counter never rose and
  they were re-spawned endlessly — most visibly **Cadaver Growths** (`CadaverGrowthAI`
  logs "Found no dungeon" and self-destructs). The mod now watches spawns and, after a
  type dies within a few seconds twice, disables it for the rest of the landing with a
  clear log line. Cadaver Growths and Cadaver Bloom are also disabled by default now.
- **Idle-stalker nudge.** An owned enemy that stands essentially still for ~25 s is
  re-pathed to a fresh reachable point — this unsticks **Feiopar** (which parks on a
  fake tree and stops) and any other stalled enemy. Ambush-by-design enemies (Bracken,
  Coil-Head, Barber, Jester, Ghost Girl, Cadaver Bloom) are exempt so they behave normally.
- **Vain shroud diagnostics.** The level-load weed setup now always logs the Bush Wolf
  enabled state, the configured and resolved iteration counts, and the level's current
  `moldSpreadIterations`, so a failed weed generation can be diagnosed from the log.

## 1.0.10

- **Whitelist mode.** New `[Advanced] ExcludedEnemiesIsWhitelist` (default false): flip
  it and the `ExcludedEnemies` list becomes an allow-list — only the listed types may
  spawn, everything else is excluded. Lasso and Red pill stay excluded regardless, and
  an empty list in whitelist mode logs a warning (nothing would spawn). It also feeds
  `ForeignEnemies=RemoveExcluded`, so whitelist + that policy strips any non-listed
  enemy other mods spawn, letting you pin the whole moon to a chosen set of enemies.

## 1.0.9

- **Old Bird upper-floor lock.** New `[Balance] OldBirdUpperFloorOnly` (default true):
  the Old Bird (RadMech) spawns only on the ship-landing level, ignoring
  `UpperFloorSpawnShare`. It is huge and the basement is cramped, so it fights far
  better upstairs.
- **Feiopar (PumaAI) can stalk again — experimental.** PumaAI only stalks from
  objects tagged `Tree`; the Company building has none, so `ChooseTargetTree` finds
  nothing and Feiopar never sets a destination (it just stands there). New
  `[Integration] FeioparFakeTrees` (default true) fabricates tree nodes on the
  interior navmesh, each with the overhead canopy collider (layer 25) that
  `PumaAI.Start` validates against, and clears PumaAI's static tree cache so it
  adopts them. It may perch oddly near the ceiling — set the flag to false if it
  looks broken.

## 1.0.8

- **Corrected the nest attribution.** The nest-requiring enemy in vanilla v81 is
  the **Giant Kiwi** (`GiantKiwiAI`, `birdNestPrefab`), not the Old Bird — the
  1.0.7 notes were wrong. The nest feature is data-driven off the EnemyType flags,
  so it already served the correct enemy; only the documentation is fixed.
- **Old Bird (RadMech) instant-death is still open.** It does not use a nest, so
  1.0.7 did not address it. Added an early-death diagnostic: when an enemy we
  spawned dies within 4 s, the log records its AI class and a **stack trace naming
  whatever called `KillEnemy`**, so the real cause can be identified from one
  landing instead of guessed.

## 1.0.7

- **Nest-requiring enemies work now.** `EnemyAI.Start` contains
  `if (!foundNest && enemyType.requireNestObjectsToSpawn) { isEnemyDead = true;
  Destroy(gameObject); }`, and Gordion places no nests during level generation, so
  such an enemy destroyed itself one frame after spawning. The mod now places the
  enemy's own nest prefab on the interior navmesh (properly network-spawned) before
  spawning it; the enemy finds it, teleports onto it via `UseNestSpawnObject` and
  lives inside the building. Nests are cleaned up when the ship leaves. (In v81 this
  is the Giant Kiwi — see 1.0.8; the 1.0.7 notes mis-named it the Old Bird.)
- If a nest cannot be placed, the enemy is skipped with a logged reason naming its
  AI class instead of being spawned into an instant self-destruct.

## 1.0.6

- **Vain shrouds can now be grown on Gordion**, which makes the Kidnapper Fox
  (Bush Wolf) playable instead of excluded. New `[Integration]
  VainShroudIterations`: the mod sets the level's own `moldSpreadIterations`
  before load, so weeds are generated and network-synced through the vanilla
  path (clients need no mod). `0` = automatic — weeds grow only when Bush Wolf
  is enabled. Bush Wolf is no longer force-excluded; it is simply skipped, with
  a logged explanation, if no weeds exist on the moon.
- **Correct defaults for the v81 vanilla enemies** that previously fell through
  to the "unknown, disabled" fallback: `Feiopar` (PumaAI), `Cadaver Growths`
  (CadaverGrowthAI), `Cadaver Bloom` (CadaverBloomAI), `Stingray` (StingrayAI)
  and `GiantKiwi` (GiantKiwiAI).
- **Spawnability report in the log.** Every landing now lists the resulting
  spawn pool and, for anything that will not spawn, the enemy name, its AI class
  and the concrete reason (no prefab, blacklisted, `Enabled = false`,
  `SpawnWeight = 0`, `MaxSpawnCount = 0`, or an unmet map requirement).
- Enabled enemies with known map caveats now log them: Bush Wolf's weed
  requirement, Cadaver Bloom being a dormant seed that only Cadaver Growths
  plants and wakes (enable `Cadaver Growths` instead of spawning Blooms
  directly), Feiopar's tree stalking, and Earth Leviathan's burrowing.

## 1.0.5

- **Enemies can finally see, chase and kill you.** `EnemyAI.PlayerIsTargetable`
  requires `player.isInsideFactory != enemy.isOutside`, and players inside the
  Company building are *not* flagged as being in a factory (there is no
  EntranceTeleport there). Enemies were being flagged as interior, so the check
  failed for every player — Masked walked up and calmly walked away,
  `MeetsStandardPlayerCollisionConditions` refused every kill, and many AIs had
  no valid target to path toward. Spawned enemies are now flagged as outdoor
  enemies (`[Advanced] TreatEnemiesAsOutside`, default true) while still being
  pointed at interior patrol nodes.
- **Patrol nodes are now created twice per position**, tagged `AINode` and
  `OutsideAINode`, so an AI that re-resolves its nodes lands inside the building
  either way instead of trekking to the outdoor node field.
- **New maintenance pass** (`[Advanced] MaintenanceInterval`, default 3 s):
  re-applies AI flags and node sets to spawned enemies, and teleports any enemy
  that ended up somewhere unreachable back onto the interior navmesh.
- **The blacklist now applies to the whole moon**: `[Advanced] ForeignEnemies`
  (default `RemoveExcluded`) despawns blacklisted enemy types even when another
  mod spawned them (vanilla spawn cycles, BrutalCompanyMinus, MoreEnemies), which
  is why blacklisted baboon hawks and worms could still appear. `Ignore` restores
  the old behaviour; `RemoveNotEnabled` also removes types with `Enabled = false`.
  Enemies in a kill animation are never removed.
- **Bush Wolf (Kidnapper Fox) is now excluded automatically** with a logged
  reason: `BushWolfEnemy.Start()` kills itself immediately when the map has no
  vain shrouds to hide in, and Gordion has none.

## 1.0.4

- No gameplay changes: version bump for Thunderstore (1.0.3 was already
  published there) with the website_url pointing to the GitHub repository.

## 1.0.3

- **Manticoil is now disabled by default** (it behaves erratically on Gordion); enable it in `[Enemy.Manticoil]` if you want it back — the ToilHead "Manti-Toil" integration still applies.
- **Floor balance**: new `[Balance] UpperFloorSpawnShare` (default 70) — percentage of spawns placed at the ship-landing level; the rest go to the basement. Previously the much larger basement floor dominated the area-weighted pick and acted as a funnel.
- **Outdoor/indoor pools**: new `[Balance] OutsideEnemyShare` (default 50) — each spawn first rolls whether to pick from outdoor enemy types (dogs, giants, baboon hawks, Old Birds...) or indoor ones, then runs the weighted random within that pool.
- Region filtering relaxed: all navmesh regions with a complete path to the main-hall anchor are now eligible (previously only the single largest region), so a legitimately connected upper floor can no longer be discarded wholesale.

## 1.0.2

- **Reachability check**: spawn points and patrol nodes are now validated with connected-region analysis plus `NavMesh.CalculatePath` to a main-hall anchor. Enemies can no longer spawn on roof patches, in pits or other one-way spots they cannot walk out of (fixes the vanilla "eliminated all possible nodes" AI error).
- Fixed ToilHead and StarlancerAIFix being reported as "not detected" even when installed: detection no longer relies on `PluginInfo.Instance` and falls back to scanning loaded assemblies.
- ToilHead integration bound to the exact ToilHead 1.9.1 API (`Api.SetToilHeadOnServer` / `Api.SetMantiToilOnServer`); added `[Integration] ToilSlayerChance` (default 0) for the minigun turret variant.
- StarlancerAIFix is intentionally no longer invoked manually: its `EnemyAI.Start` postfix runs automatically, and this mod's interior node assignment executes afterwards so it takes precedence on the surface-level Company moon.

## 1.0.1

- Landing detection rewritten from a Harmony hook on the doors-opening sequence to polling `StartOfRound.shipHasLanded` (LandingWatcher) — robust against other mods transpiling the landing coroutine; includes a 20 s fallback trigger.
- Explicit landing diagnostics in the log (planet, scene, host flag, company-moon flag), including a clear message when the session is not the host.

## 1.0.0

- Initial release: timer-driven weighted spawning of vanilla enemies inside the Company building on the NavMeshInCompanyRedux navmesh, with per-enemy Enabled/weight/min/max, global cap, RespawnOnLoad, debug logging, generated interior patrol nodes, and host-only network-authoritative spawning.
