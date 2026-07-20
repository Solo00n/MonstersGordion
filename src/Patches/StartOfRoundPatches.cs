using System;
using HarmonyLib;

namespace MonstersGordion.Patches;

[HarmonyPatch]
internal static class StartOfRoundPatches
{
    // Landing is detected by LandingWatcher (polling shipHasLanded), not by a
    // Harmony hook: openingDoorsSequence is a favourite transpiler target in
    // large modpacks and a postfix there proved unreliable. The patches below
    // only provide *fast* cleanup; the watcher would catch these transitions
    // too, half a second later.

    [HarmonyPostfix]
    [HarmonyPatch(typeof(StartOfRound), "ShipLeave")]
    private static void ShipLeave_Postfix()
    {
        try
        {
            CompanyMonsterSpawner.OnShipLeaving();
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"ShipLeave handler failed: {e}");
        }
    }

    /// <summary>Safety net: disconnect / return to menu tears StartOfRound down.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(StartOfRound), "OnDestroy")]
    private static void OnDestroy_Postfix()
    {
        try
        {
            CompanyMonsterSpawner.OnShipLeaving();
        }
        catch
        {
            // Scene is being destroyed; nothing useful to log.
        }
    }
}
