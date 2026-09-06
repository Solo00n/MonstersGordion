using System;
using HarmonyLib;

namespace MonstersGordion.Patches;

[HarmonyPatch]
internal static class RoundManagerPatches
{
    /// <summary>
    /// Rolls this landing's Company-moon event.
    ///
    /// This is the mod's only prefix, and the timing is the reason: events that
    /// place outdoor hazards work by adjusting spawn densities that the level
    /// generator reads as it builds, so running them at touchdown would be too
    /// late. Priority.Low puts us behind BrutalCompanyMinus's own prefix on this
    /// method, which assigns Manager.currentLevel and recomputes its difficulty
    /// before bailing out on the Company moon — so by the time we run, BCMER's
    /// state is already correct for the event we are about to execute.
    ///
    /// Never throws into the game: a failed roll must not stop a level from
    /// loading.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Low)]
    [HarmonyPatch(typeof(RoundManager), nameof(RoundManager.LoadNewLevel),
        new[] { typeof(int), typeof(SelectableLevel) })]
    private static void LoadNewLevel_Prefix(SelectableLevel newLevel)
    {
        try
        {
            if (!CompanyMonsterSpawner.IsCompanyLevel(newLevel))
            {
                GordionEvents.Reset();
                return;
            }

            var rm = RoundManager.Instance;
            if (rm == null || !rm.IsServer)
                return;

            GordionEvents.RollForLanding();
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Company moon event roll failed: {e}");
        }
    }
}
