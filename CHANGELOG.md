# Changelog

## 1.0.7

- **Nest-requiring enemies work now — Old Bird (RadMech) above all.** `EnemyAI.Start`
  contains `if (!foundNest && enemyType.requireNestObjectsToSpawn) { isEnemyDead = true;
  Destroy(gameObject); }`, and Gordion places no nests during level generation, so
  Old Birds destroyed themselves roughly 13 ms after spawning and the moon stayed
  empty. The mod now places the enemy's own nest prefab on the interior navmesh
  (properly network-spawned) before spawning it; the enemy finds it, teleports onto
  it via `UseNestSpawnObject` and lives inside the building. Nests are cleaned up
  when the ship leaves.
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
