using System;

namespace MonstersGordion.Compat;

/// <summary>
/// Coexistence with BrutalCompanyMinusExtraReborn (BCME).
/// BCME has no stable public spawn API, so integration is behavioural:
///  - this mod only ever spawns on the Company moon, which BCME events do not
///    target, so the two never fight over the same spawn budget;
///  - with CountForeignEnemies=true (default) every enemy BCME (or the game)
///    does create counts toward our GlobalCap and per-type limits, so the
///    combined population can never exceed the configured cap.
/// </summary>
internal static class BcmeCompat
{
    private static bool _scanned;

    public static bool Present { get; private set; }

    public static void Scan()
    {
        if (_scanned)
            return;
        _scanned = true;

        try
        {
            var info = CompatScanner.FindPluginInfo(
                "SoftDiamond.BrutalCompanyMinusExtraReborn", "BrutalCompanyMinus");
            if (info == null)
            {
                Plugin.DebugLog("BrutalCompanyMinus(-ExtraReborn) not detected.");
                return;
            }

            Present = true;
            Plugin.Log.LogInfo(
                $"BrutalCompanyMinus detected ({info.Metadata.GUID} v{info.Metadata.Version}). " +
                "Its enemies " +
                (Plugin.Cfg.CountForeignEnemies.Value
                    ? "count toward this mod's GlobalCap (shared budget)."
                    : "are IGNORED by this mod's caps (CountForeignEnemies=false)."));
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"BCME detection failed: {e.Message}");
        }
    }
}
