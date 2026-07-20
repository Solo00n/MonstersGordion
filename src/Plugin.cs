using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using MonstersGordion.Compat;
using MonstersGordion.Patches;

namespace MonstersGordion;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
// Soft dependencies: only enforce load order when these mods are present.
// Actual runtime detection is done by GUID/name scan in the Compat classes,
// so a slightly different GUID on the other mod's side is not fatal.
[BepInDependency("Kittenji.NavMeshInCompany", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("NavMeshInCompanyRedux", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("AudioKnight.StarlancerAIFix", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("com.github.zehsteam.ToilHead", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("SoftDiamond.BrutalCompanyMinusExtraReborn", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("SoftDiamond.BrutalCompanyMinus", BepInDependency.DependencyFlags.SoftDependency)]
public class Plugin : BaseUnityPlugin
{
    internal static Plugin Instance { get; private set; }
    internal static ManualLogSource Log { get; private set; }
    internal static PluginConfig Cfg { get; private set; }

    private Harmony _harmony;

    private void Awake()
    {
        Instance = this;
        Log = Logger;
        Cfg = new PluginConfig(Config);

        _harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        _harmony.PatchAll(typeof(StartOfRoundPatches));
        _harmony.PatchAll(typeof(EnemyAIPatches));

        LandingWatcher.Create();

        Log.LogInfo($"{MyPluginInfo.PLUGIN_NAME} v{MyPluginInfo.PLUGIN_VERSION} loaded. " +
                    "Waiting for a landing on 71-Gordion (Company building).");
    }

    /// <summary>Gated debug logging (see [General] DebugMode).</summary>
    internal static void DebugLog(string message)
    {
        if (Cfg != null && Cfg.DebugMode.Value)
            Log.LogInfo($"[Debug] {message}");
    }

    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
    }
}
