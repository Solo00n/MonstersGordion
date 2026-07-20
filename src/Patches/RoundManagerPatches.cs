using System;
using HarmonyLib;

namespace MonstersGordion.Patches;

[HarmonyPatch]
internal static class RoundManagerPatches
{
    /// <summary>
    /// Grows vain shrouds on the Company moon.
    ///
    /// Weeds are driven entirely by SelectableLevel.moldSpreadIterations:
    /// RoundManager.LoadNewLevelWait reads it, picks a start node and calls
    /// GenerateNewLevelClientRpc, which makes every client run
    /// MoldSpreadManager.GenerateMold with the same seeded position. Setting the
    /// field before the level loads therefore produces properly network-synced
    /// weeds through the vanilla path — clients need no mod of their own — and
    /// gives the Kidnapper Fox (Bush Wolf) the hiding spots it requires.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(RoundManager), nameof(RoundManager.LoadNewLevel))]
    private static void LoadNewLevel_Prefix(SelectableLevel newLevel)
    {
        try
        {
            if (newLevel == null || !IsCompanyLevel(newLevel))
                return;

            int iterations = Plugin.Cfg.ResolveVainShroudIterations();
            if (newLevel.moldSpreadIterations == iterations)
                return;

            newLevel.moldSpreadIterations = iterations;
            newLevel.moldStartPosition = -1; // let the game pick (and sync) a fresh patch

            if (iterations > 0)
            {
                Plugin.Log.LogInfo(
                    $"Vain shrouds: growing weeds on '{newLevel.PlanetName}' " +
                    $"(moldSpreadIterations={iterations}). This uses the game's own generation and " +
                    "network sync, and is what lets Bush Wolf (BushWolfEnemy) survive here.");
            }
            else
            {
                Plugin.DebugLog($"Vain shrouds disabled on '{newLevel.PlanetName}'.");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Vain shroud setup failed: {e}");
        }
    }

    private static bool IsCompanyLevel(SelectableLevel level) =>
        level.sceneName == "CompanyBuilding"
        || level.levelID == 3
        || (level.PlanetName != null && level.PlanetName.Contains("Gordion"));
}
