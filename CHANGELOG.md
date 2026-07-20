# Changelog

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
