using System;
using HarmonyLib;

namespace MonstersGordion.Patches;

/// <summary>
/// Grows vain shrouds (weeds) on the Company moon so the Kidnapper Fox
/// (Bush Wolf) has somewhere to hide and survives instead of despawning itself.
///
/// Technique confirmed against the game code and inspired by ButteryStancakes'
/// FoxLover (GPL): the canonical place mold amounts are decided is
/// <see cref="StartOfRound.LoadPlanetsMoldSpreadData"/>, which runs once from the
/// core save-load flow (not the level-load path LethalLevelLoader reworks, which
/// is why the earlier RoundManager.LoadNewLevel hook never fired). Gordion has
/// <c>canSpawnMold == false</c>, so the game never grows weeds there; we flip that
/// flag and raise <c>moldSpreadIterations</c> to the configured amount. When the
/// player then lands, the vanilla RoundManager pipeline generates and
/// network-syncs the weeds exactly as on any other moon.
///
/// Implementation is original (written from the decompiled game API); no FoxLover
/// code is copied.
/// </summary>
[HarmonyPatch]
internal static class MoldSpreadPatches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.LoadPlanetsMoldSpreadData))]
    private static void LoadPlanetsMoldSpreadData_Postfix(StartOfRound __instance)
    {
        try
        {
            int iterations = Plugin.Cfg.ResolveVainShroudIterations();
            if (__instance == null || __instance.levels == null)
                return;

            foreach (SelectableLevel level in __instance.levels)
            {
                if (level == null || !CompanyMonsterSpawner.IsCompanyLevel(level))
                    continue;

                if (iterations <= 0)
                {
                    Plugin.DebugLog(
                        $"Vain shrouds: not forcing weeds on '{level.PlanetName}' " +
                        "(VainShroudIterations resolved to 0 — Bush Wolf disabled and no explicit value).");
                    return;
                }

                // Gordion ships with canSpawnMold=false; enable it and seed the
                // iteration count. Max() so we never shrink weeds the save already has.
                level.canSpawnMold = true;
                int before = level.moldSpreadIterations;
                level.moldSpreadIterations = Math.Max(before, iterations);
                Plugin.Log.LogInfo(
                    $"Vain shrouds: enabled weed growth on '{level.PlanetName}' " +
                    $"(canSpawnMold set true, moldSpreadIterations {before} -> {level.moldSpreadIterations}). " +
                    "Weeds generate on landing via the vanilla pipeline; this lets Bush Wolf survive.");
                return;
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Vain shroud setup failed: {e}");
        }
    }
}
