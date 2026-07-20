using System;
using System.Linq;
using System.Reflection;

namespace MonstersGordion.Compat;

/// <summary>
/// Soft integration with ToilHead (turret on a Coil-Head / Manticoil).
/// Binds to ToilHead's public static API class (verified against ToilHead 1.9.1:
/// com.github.zehsteam.ToilHead.Api.SetToilHeadOnServer / SetMantiToilOnServer /
/// SetToilSlayerOnServer / SetMantiSlayerOnServer, all taking one EnemyAI),
/// with a name-based reflection fallback for older/newer versions.
/// </summary>
internal static class ToilHeadCompat
{
    private const string PreferredGuid = "com.github.zehsteam.ToilHead";

    // Regular turret and "Slayer" (minigun) variants, per enemy kind.
    private static readonly string[] ToilHeadNames = { "SetToilHeadOnServer", "SpawnToilHeadOnServer" };
    private static readonly string[] MantiToilNames = { "SetMantiToilOnServer", "SetManToilOnServer", "SetMantoilOnServer" };
    private static readonly string[] ToilSlayerNames = { "SetToilSlayerOnServer" };
    private static readonly string[] MantiSlayerNames = { "SetMantiSlayerOnServer" };

    private static bool _scanned;
    private static MethodInfo _toilHead;
    private static MethodInfo _mantiToil;
    private static MethodInfo _toilSlayer;
    private static MethodInfo _mantiSlayer;

    public static bool Present { get; private set; }

    public static bool IsEligible(string enemyName) =>
        enemyName == "Spring" || enemyName == "Manticoil";

    public static void Scan()
    {
        if (_scanned)
            return;
        _scanned = true;

        try
        {
            var info = CompatScanner.FindPluginInfo(PreferredGuid, "ToilHead");
            Assembly assembly = CompatScanner.FindAssembly(info, "ToilHead", out string how);
            if (assembly == null)
            {
                Plugin.Log.LogInfo("ToilHead not detected — ToilHeadSpawnChance will be ignored.");
                return;
            }

            Present = true;

            foreach (Type type in CompatScanner.SafeGetTypes(assembly))
            {
                foreach (MethodInfo method in type.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != 1 || !typeof(EnemyAI).IsAssignableFrom(parameters[0].ParameterType))
                        continue;

                    if (_toilHead == null && ToilHeadNames.Contains(method.Name)) _toilHead = method;
                    else if (_mantiToil == null && MantiToilNames.Contains(method.Name)) _mantiToil = method;
                    else if (_toilSlayer == null && ToilSlayerNames.Contains(method.Name)) _toilSlayer = method;
                    else if (_mantiSlayer == null && MantiSlayerNames.Contains(method.Name)) _mantiSlayer = method;
                }
            }

            Plugin.Log.LogInfo(
                $"ToilHead detected (assembly via {how}, v{info?.Metadata?.Version?.ToString() ?? "?"}). " +
                $"Hooks — Coil-Head: {Describe(_toilHead)}, Manticoil: {Describe(_mantiToil)}, " +
                $"Slayer: {Describe(_toilSlayer)}/{Describe(_mantiSlayer)}.");

            if (_toilHead == null && _mantiToil == null)
                Plugin.Log.LogWarning(
                    "ToilHead is installed but no compatible attach method was found — " +
                    "the turret integration will be inactive. Please report your ToilHead version.");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"ToilHead detection failed: {e.Message}");
        }
    }

    /// <summary>Attaches a turret to the given enemy. Host-only. Returns success.</summary>
    public static bool TryApply(EnemyAI enemy, bool slayer)
    {
        if (enemy == null || enemy.enemyType == null)
            return false;

        MethodInfo method = enemy.enemyType.enemyName switch
        {
            "Spring" => slayer ? (_toilSlayer ?? _toilHead) : _toilHead,
            "Manticoil" => slayer ? (_mantiSlayer ?? _mantiToil) : _mantiToil,
            _ => null,
        };
        if (method == null)
            return false;

        try
        {
            object result = method.Invoke(null, new object[] { enemy });
            return result is not bool ok || ok;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning(
                $"ToilHead attach failed for '{enemy.enemyType.enemyName}': {e.InnerException?.Message ?? e.Message}");
            return false;
        }
    }

    private static string Describe(MethodInfo method) =>
        method == null ? "—" : $"{method.DeclaringType?.Name}.{method.Name}";
}
