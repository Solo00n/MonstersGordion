using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;

namespace MonstersGordion.Compat;

/// <summary>
/// Shared plugin-discovery helpers for the soft integrations.
/// Deliberately does NOT rely on PluginInfo.Instance: in heavily patched
/// modpacks (log prepatchers, chainloader wrappers) that property has been
/// observed to be null even for successfully loaded plugins, which broke
/// detection in v1.0.1. The loaded assembly itself is the source of truth.
/// </summary>
internal static class CompatScanner
{
    /// <summary>Finds the chainloader entry by exact GUID or GUID/name fragment.</summary>
    public static PluginInfo FindPluginInfo(string preferredGuid, string fragment)
    {
        try
        {
            var infos = Chainloader.PluginInfos;
            if (infos.TryGetValue(preferredGuid, out var exact))
                return exact;
            return infos.Values.FirstOrDefault(p =>
                Matches(p?.Metadata?.GUID, fragment) || Matches(p?.Metadata?.Name, fragment));
        }
        catch (Exception e)
        {
            Plugin.DebugLog($"PluginInfos lookup for '{fragment}' failed: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Resolves the plugin's loaded assembly: via its Instance when available,
    /// otherwise by scanning the AppDomain for an assembly whose name matches.
    /// </summary>
    public static Assembly FindAssembly(PluginInfo info, string assemblyFragment, out string how)
    {
        if (info?.Instance != null)
        {
            how = "plugin instance";
            return info.Instance.GetType().Assembly;
        }

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            string name;
            try { name = assembly.GetName().Name; }
            catch { continue; }
            if (Matches(name, assemblyFragment))
            {
                how = info != null
                    ? $"AppDomain scan (PluginInfo.Instance was null for '{info.Metadata?.GUID}')"
                    : "AppDomain scan (no chainloader entry matched)";
                return assembly;
            }
        }

        how = "not found";
        return null;
    }

    public static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.Where(t => t != null);
        }
    }

    private static bool Matches(string value, string fragment) =>
        value != null && value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
}
