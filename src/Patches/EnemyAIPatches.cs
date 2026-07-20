using HarmonyLib;

namespace MonstersGordion.Patches;

[HarmonyPatch]
internal static class EnemyAIPatches
{
    /// <summary>
    /// Death notification — alive counters are recomputed from
    /// RoundManager.SpawnedEnemies each cycle, so this only feeds debug logging
    /// and prunes the owned-enemy list promptly.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.KillEnemy))]
    private static void KillEnemy_Postfix(EnemyAI __instance)
    {
        CompanyMonsterSpawner.NotifyEnemyKilled(__instance);
    }
}
