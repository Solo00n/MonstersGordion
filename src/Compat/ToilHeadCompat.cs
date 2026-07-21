using System;
using System.Linq;
using System.Reflection;

namespace MonstersGordion.Compat;

/// <summary>
/// Soft integration with ToilHead — puts a turret on a Coil-Head, Manticoil or
/// Masked. Binds to ToilHead's public static API (verified against ToilHead 1.9.1:
/// Api.SetToilHeadOnServer / SetMantiToilOnServer / SetToilMaskedOnServer and the
/// matching *SlayerOnServer minigun variants, all taking one EnemyAI), with a
/// name-based reflection fallback for other versions.
/// </summary>
internal static class ToilHeadCompat
{
    private const string PreferredGuid = "com.github.zehsteam.ToilHead";

    /// <summary>The three enemy kinds ToilHead can turret, keyed by EnemyType.enemyName.</summary>
    internal enum Kind { None, CoilHead, Manticoil, Masked }

    // Regular-turret and "Slayer" (minigun) method-name candidates per kind.
    private static readonly string[] CoilHeadNames      = { "SetToilHeadOnServer", "SpawnToilHeadOnServer" };
    private static readonly string[] CoilSlayerNames    = { "SetToilSlayerOnServer" };
    private static readonly string[] ManticoilNames     = { "SetMantiToilOnServer", "SetManToilOnServer", "SetMantoilOnServer" };
    private static readonly string[] MantiSlayerNames   = { "SetMantiSlayerOnServer" };
    private static readonly string[] MaskedNames        = { "SetToilMaskedOnServer" };
    private static readonly string[] MaskedSlayerNames  = { "SetSlayerMaskedOnServer" };

    private static bool _scanned;
    private static MethodInfo _coilHead, _coilSlayer;
    private static MethodInfo _manticoil, _mantiSlayer;
    private static MethodInfo _masked, _maskedSlayer;

    public static bool Present { get; private set; }

    /// <summary>Maps an enemy's name to the ToilHead kind, or None if unsupported.</summary>
    public static Kind KindOf(string enemyName) => enemyName switch
    {
        "Spring" => Kind.CoilHead,
        "Manticoil" => Kind.Manticoil,
        "Masked" => Kind.Masked,
        _ => Kind.None,
    };

    public static bool IsEligible(string enemyName) => KindOf(enemyName) != Kind.None;

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
                Plugin.Log.LogInfo("ToilHead not detected — its turret chances will be ignored.");
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

                    if (_coilHead == null && CoilHeadNames.Contains(method.Name)) _coilHead = method;
                    else if (_coilSlayer == null && CoilSlayerNames.Contains(method.Name)) _coilSlayer = method;
                    else if (_manticoil == null && ManticoilNames.Contains(method.Name)) _manticoil = method;
                    else if (_mantiSlayer == null && MantiSlayerNames.Contains(method.Name)) _mantiSlayer = method;
                    else if (_masked == null && MaskedNames.Contains(method.Name)) _masked = method;
                    else if (_maskedSlayer == null && MaskedSlayerNames.Contains(method.Name)) _maskedSlayer = method;
                }
            }

            Plugin.Log.LogInfo(
                $"ToilHead detected (assembly via {how}, v{info?.Metadata?.Version?.ToString() ?? "?"}). " +
                $"Hooks — Coil-Head: {Describe(_coilHead)}/{Describe(_coilSlayer)}, " +
                $"Manticoil: {Describe(_manticoil)}/{Describe(_mantiSlayer)}, " +
                $"Masked: {Describe(_masked)}/{Describe(_maskedSlayer)}.");

            if (_coilHead == null && _manticoil == null && _masked == null)
                Plugin.Log.LogWarning(
                    "ToilHead is installed but no compatible attach method was found — the turret " +
                    "integration will be inactive. Please report your ToilHead version.");
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

        MethodInfo method = KindOf(enemy.enemyType.enemyName) switch
        {
            Kind.CoilHead => slayer ? (_coilSlayer ?? _coilHead) : _coilHead,
            Kind.Manticoil => slayer ? (_mantiSlayer ?? _manticoil) : _manticoil,
            Kind.Masked => slayer ? (_maskedSlayer ?? _masked) : _masked,
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
