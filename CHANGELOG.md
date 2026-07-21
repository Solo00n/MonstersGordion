# Changelog

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
