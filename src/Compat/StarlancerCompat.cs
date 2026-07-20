using System;
using System.Reflection;

namespace MonstersGordion.Compat;

/// <summary>
/// Soft integration with StarlancerAIFix.
/// Verified against StarlancerAIFix 3.13.2: its whole enemy fix is a Harmony
/// postfix on EnemyAI.Start (StarlancerAIFix.Patches.AIFix.AIFixPatch), which
/// runs for our enemies automatically. There is deliberately NO manual call
/// into it: on the Company moon (surface-level y) its position heuristics would
/// classify our interior spawns as outdoors and re-point them at outside AI
/// nodes. Instead, our own interior fix-up runs two frames after spawn, i.e.
/// after Start and after Starlancer's postfix, so our node assignment wins.
/// </summary>
internal static class StarlancerCompat
{
    private const string PreferredGuid = "AudioKnight.StarlancerAIFix";

    private static bool _scanned;

    public static bool Present { get; private set; }

    public static void Scan()
    {
        if (_scanned)
            return;
        _scanned = true;

        try
        {
            var info = CompatScanner.FindPluginInfo(PreferredGuid, "Starlancer");
            Assembly assembly = CompatScanner.FindAssembly(info, "Starlancer", out string how);
            if (assembly == null)
            {
                Plugin.Log.LogInfo(
                    "StarlancerAIFix not detected. Not required: this mod applies its own interior " +
                    "AI fix-up after spawning.");
                return;
            }

            Present = true;
            Plugin.Log.LogInfo(
                $"StarlancerAIFix detected (assembly via {how}, v{info?.Metadata?.Version?.ToString() ?? "?"}). " +
                "Its EnemyAI.Start postfix applies to spawned enemies automatically; " +
                "our interior node assignment runs afterwards and takes precedence.");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"StarlancerAIFix detection failed: {e.Message}");
        }
    }
}
