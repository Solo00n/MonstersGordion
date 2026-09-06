using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace MonstersGordion.Compat;

/// <summary>
/// Integration with BrutalCompanyMinus(-ExtraReborn).
///
/// Two separate concerns live here.
///
/// Coexistence, which has always been the case: with CountForeignEnemies=true
/// (default) every enemy BCME or the game creates counts toward our GlobalCap,
/// so the combined population can never exceed the configured cap.
///
/// Events, new in 1.6.0: BCMER never runs a single event on the Company moon.
/// Its EventManager.ModifyLevel prefix on RoundManager.LoadNewLevel bails out on
/// "newLevel.levelID == 3" before ChooseEvents() is reached, so no config key,
/// no per-event moon whitelist and not even the MEVENT terminal command can
/// bring an event here. Patching that out would corrupt BCMER's level-indexed
/// bookkeeping, so instead we roll one event ourselves and call Execute() on it.
/// Everything needed for that is public: API.* for the queries, MEvent.Execute()
/// and MEvent.AddEventIfOnly() for the run. Manager.currentLevel and the
/// difficulty are assigned *before* that early return, so BCMER's own state is
/// already correct when we call in.
///
/// Bound by reflection so the mod still builds and runs without BCMER installed.
/// </summary>
internal static class BcmeCompat
{
    private const string RebornGuid = "SoftDiamond.BrutalCompanyMinusExtraReborn";
    private const string LegacyGuid = "SoftDiamond.BrutalCompanyMinus";

    private static bool _scanned;

    // API.* — all public static, all taking the event name as a single string.
    private static MethodInfo _doesEventExist;
    private static MethodInfo _getEvent;
    private static MethodInfo _isEnabled;
    private static MethodInfo _getWeight;
    private static MethodInfo _getType;
    private static MethodInfo _isSpecial;
    private static MethodInfo _isBeta;
    private static MethodInfo _onBlacklist;
    private static MethodInfo _onWhitelist;
    private static MethodInfo _getColorHex;
    private static MethodInfo _getDescriptions;

    // MEvent.* — public instance members on the returned event object.
    private static MethodInfo _mEventName;
    private static MethodInfo _mEventExecute;
    private static MethodInfo _mEventAddIfOnly;
    private static FieldInfo _mEventMoonMode;

    // EventManager.currentEvents — the list BCMER's own UI and third-party
    // overlays read to show what is happening this round.
    private static FieldInfo _currentEventsField;

    /// <summary>Any BrutalCompanyMinus flavour is loaded.</summary>
    public static bool Present { get; private set; }

    /// <summary>Specifically ExtraReborn, the only flavour whose event API we bind.</summary>
    public static bool IsReborn { get; private set; }

    /// <summary>True when enough of the event API resolved to roll and run an event.</summary>
    public static bool EventsUsable =>
        IsReborn && _doesEventExist != null && _getEvent != null && _mEventExecute != null;

    public static void Scan()
    {
        if (_scanned)
            return;
        _scanned = true;

        try
        {
            var info = CompatScanner.FindPluginInfo(RebornGuid, "BrutalCompanyMinus");
            if (info == null)
            {
                Plugin.DebugLog("BrutalCompanyMinus(-ExtraReborn) not detected.");
                return;
            }

            Present = true;
            string guid = info.Metadata?.GUID ?? "<unknown>";
            string version = info.Metadata?.Version?.ToString() ?? "?";
            IsReborn = !string.Equals(guid, LegacyGuid, StringComparison.OrdinalIgnoreCase);

            Plugin.Log.LogInfo(
                $"BrutalCompanyMinus detected ({guid} v{version}). Its enemies " +
                (Plugin.Cfg.CountForeignEnemies.Value
                    ? "count toward this mod's GlobalCap (shared budget)."
                    : "are IGNORED by this mod's caps (CountForeignEnemies=false)."));

            if (!IsReborn)
            {
                Plugin.Log.LogInfo(
                    "This is the legacy BrutalCompanyMinus, which has no event API — " +
                    "the [BrutalCompany] event settings will stay inactive.");
                return;
            }

            BindEventApi(info);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"BCME detection failed: {e.Message}");
        }
    }

    private static void BindEventApi(BepInEx.PluginInfo info)
    {
        Assembly assembly = CompatScanner.FindAssembly(info, "BrutalCompanyMinus", out string how);
        if (assembly == null)
        {
            Plugin.Log.LogWarning(
                "BCMER is loaded but its assembly could not be resolved — event integration is off.");
            return;
        }

        Type api = null, mEvent = null;
        foreach (Type type in CompatScanner.SafeGetTypes(assembly))
        {
            if (api == null && type.FullName == "BrutalCompanyMinus.Minus.API")
                api = type;
            else if (mEvent == null && type.FullName == "BrutalCompanyMinus.Minus.MEvent")
                mEvent = type;
            if (api != null && mEvent != null)
                break;
        }

        if (api == null || mEvent == null)
        {
            Plugin.Log.LogWarning(
                $"BCMER assembly found (via {how}) but its API/MEvent types were not — " +
                "event integration is off. Please report your BCMER version.");
            return;
        }

        // Every API query we need takes the event name as a single string.
        _doesEventExist  = FindApi(api, "DoesEventExist");
        _getEvent        = FindApi(api, "GetEventsByName", "GetEventByName");
        _isEnabled       = FindApi(api, "IsEventEnabled");
        _getWeight       = FindApi(api, "GetEventWeight");
        _getType         = FindApi(api, "GetEventType");
        _isSpecial       = FindApi(api, "IsEventSpecial");
        _isBeta          = FindApi(api, "IsEventBeta");
        _onBlacklist     = FindApi(api, "IsEventOnBlacklist");
        _onWhitelist     = FindApi(api, "IsEventOnWhitelist");
        _getColorHex     = FindApi(api, "GetEventColorHex");
        _getDescriptions = FindApi(api, "GetEventDescriptions");

        _mEventName      = mEvent.GetMethod("Name", Type.EmptyTypes);
        _mEventExecute   = mEvent.GetMethod("Execute", Type.EmptyTypes);
        _mEventAddIfOnly = mEvent.GetMethod("AddEventIfOnly", Type.EmptyTypes);
        _mEventMoonMode  = mEvent.GetField("MoonMode", BindingFlags.Public | BindingFlags.Instance);

        foreach (Type type in CompatScanner.SafeGetTypes(assembly))
        {
            if (type.FullName != "BrutalCompanyMinus.Minus.EventManager")
                continue;
            _currentEventsField = type.GetField("currentEvents",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            break;
        }

        Plugin.Log.LogInfo(
            $"BCMER event API bound (assembly via {how}). " +
            $"exists={_doesEventExist != null}, get={_getEvent != null}, " +
            $"enabled={_isEnabled != null}, weight={_getWeight != null}, " +
            $"moonLists={_onBlacklist != null && _onWhitelist != null}, " +
            $"execute={_mEventExecute != null}, veto={_mEventAddIfOnly != null}, currentEvents={_currentEventsField != null}.");

        if (!EventsUsable)
            Plugin.Log.LogWarning(
                "BCMER is installed but its event API did not resolve well enough to run events. " +
                "Stock events on the Company moon will be skipped.");
    }

    /// <summary>
    /// Finds a public static API method taking one string, under any of the given
    /// names. Matching is case-insensitive because BCMER's API mixes casing
    /// (isEventOnWhitelist next to IsEventOnBlacklist).
    /// </summary>
    private static MethodInfo FindApi(Type api, params string[] names)
    {
        foreach (MethodInfo method in api.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (!names.Any(n => string.Equals(n, method.Name, StringComparison.OrdinalIgnoreCase)))
                continue;
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
                return method;
        }
        return null;
    }

    // ------------------------------------------------------------------ queries

    private static bool CallBool(MethodInfo method, string name, bool fallback)
    {
        if (method == null)
            return fallback;
        try
        {
            return method.Invoke(null, new object[] { name }) is bool b ? b : fallback;
        }
        catch (Exception e)
        {
            Plugin.DebugLog($"BCMER {method.Name}('{name}') threw: {Unwrap(e)}");
            return fallback;
        }
    }

    public static bool EventExists(string name) => CallBool(_doesEventExist, name, false);

    /// <summary>
    /// The weight BCMER currently gives this event. UpdateAllEventWeights assigns
    /// 0 to every event whose type scale is zeroed in Difficulty_Settings.cfg, so
    /// "weight above 0" is exactly "this event's type is live in the user's
    /// config" — no config parsing needed, and their tuning is obeyed for free.
    /// </summary>
    public static int WeightOf(string name)
    {
        if (_getWeight == null)
            return 1;
        try
        {
            return _getWeight.Invoke(null, new object[] { name }) is int w ? w : 1;
        }
        catch (Exception e)
        {
            Plugin.DebugLog($"BCMER GetEventWeight('{name}') threw: {Unwrap(e)}");
            return 1;
        }
    }

    public static string TypeOf(string name)
    {
        if (_getType == null)
            return "?";
        try
        {
            return _getType.Invoke(null, new object[] { name }) as string ?? "?";
        }
        catch { return "?"; }
    }

    public static string ColorOf(string name)
    {
        if (_getColorHex == null)
            return null;
        try
        {
            return _getColorHex.Invoke(null, new object[] { name }) as string;
        }
        catch { return null; }
    }

    /// <summary>One of the event's flavour lines, picked at random, or null.</summary>
    public static string DescriptionOf(string name)
    {
        if (_getDescriptions == null)
            return null;
        try
        {
            if (_getDescriptions.Invoke(null, new object[] { name }) is not IEnumerable raw)
                return null;

            var lines = new List<string>();
            foreach (object item in raw)
            {
                if (item is string line && !string.IsNullOrWhiteSpace(line))
                    lines.Add(line);
            }
            return lines.Count == 0 ? null : lines[UnityEngine.Random.Range(0, lines.Count)];
        }
        catch { return null; }
    }

    // ------------------------------------------------------------- gate and run

    /// <summary>
    /// Resolves an event and checks it against the user's BCMER config the same
    /// way ChooseEvents would: enabled flag, type weight, Special/Beta flags and
    /// the per-event moon lists. Returns null and fills <paramref name="reason"/>
    /// when the event must not run, so a no-show is never a mystery in the log.
    /// </summary>
    public static object TryResolve(string name, out string reason)
    {
        reason = null;
        if (!EventsUsable)
        {
            reason = "BCMER event API unavailable";
            return null;
        }

        if (!EventExists(name))
        {
            reason = "no such event in this BCMER build";
            return null;
        }

        if (!CallBool(_isEnabled, name, true))
        {
            reason = "'Event Enabled?' is false in your BCMER config";
            return null;
        }

        if (WeightOf(name) <= 0)
        {
            reason = $"its type ({TypeOf(name)}) has weight 0 — raise that type's scale in " +
                     "BCMER's Difficulty_Settings.cfg [_EventType Weights] to allow it";
            return null;
        }

        if (CallBool(_isSpecial, name, false))
        {
            reason = "it is a Special event and 'Enable Special Events?' is off";
            return null;
        }

        if (CallBool(_isBeta, name, false))
        {
            reason = "it is a Beta event and 'Enable Beta Events?' is off";
            return null;
        }

        object mEvent;
        try
        {
            mEvent = _getEvent.Invoke(null, new object[] { name });
        }
        catch (Exception e)
        {
            reason = $"BCMER failed to hand over the event: {Unwrap(e)}";
            return null;
        }

        if (mEvent == null)
        {
            reason = "BCMER returned no event object";
            return null;
        }

        // BCMER falls back to a Nothing event when a name does not resolve, so a
        // typo would silently become a no-op rather than an error.
        string actual = NameOf(mEvent);
        if (actual != null && !string.Equals(actual, name, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"BCMER resolved it to '{actual}' instead — check the spelling";
            return null;
        }

        bool whitelistMode = false;
        try
        {
            if (_mEventMoonMode != null)
                whitelistMode = _mEventMoonMode.GetValue(mEvent) is bool m && m;
        }
        catch { /* fall through as blacklist mode, the BCMER default */ }

        if (whitelistMode)
        {
            if (!CallBool(_onWhitelist, name, true))
            {
                reason = "its 'Moons To Spawn Only On' list does not include this moon";
                return null;
            }
        }
        else if (CallBool(_onBlacklist, name, false))
        {
            reason = "its 'Moons To Not Spawn On' list excludes this moon";
            return null;
        }

        // The event's own precondition. AllWeather uses this to require a populated
        // randomWeathers pool, which on this moon only WeatherGordion provides.
        if (_mEventAddIfOnly != null)
        {
            try
            {
                if (_mEventAddIfOnly.Invoke(mEvent, null) is bool ok && !ok)
                {
                    reason = "the event's own AddEventIfOnly() check failed — it needs something " +
                             "this moon or your modpack does not currently provide";
                    return null;
                }
            }
            catch (Exception e)
            {
                reason = $"its AddEventIfOnly() threw: {Unwrap(e)}";
                return null;
            }
        }

        return mEvent;
    }

    public static string NameOf(object mEvent)
    {
        if (_mEventName == null || mEvent == null)
            return null;
        try
        {
            return _mEventName.Invoke(mEvent, null) as string;
        }
        catch { return null; }
    }

    /// <summary>Runs the event. Host-only, and never allowed to break the caller.</summary>
    public static bool Execute(object mEvent, string name)
    {
        if (_mEventExecute == null || mEvent == null)
            return false;
        try
        {
            _mEventExecute.Invoke(mEvent, null);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"BCMER event '{name}' threw while executing: {Unwrap(e)}");
            return false;
        }
    }

    /// <summary>
    /// Puts the event we just ran into EventManager.currentEvents, which is where
    /// BCMER's own panel and third-party overlays look to see what is happening
    /// this round. Without this an event runs on the Company moon but every
    /// display insists nothing did.
    ///
    /// Pass null to leave the list empty. BCMER clears it itself in its
    /// StartOfRound.ShipLeave prefix, which is not gated by the moon, so nothing
    /// we write here can outlive the landing.
    ///
    /// Deliberately does NOT set MEvent.Executed. BCMER assigns that flag in
    /// ApplyEvents and never resets it anywhere in the assembly, and ApplyEvents
    /// skips any event already carrying it — so marking one here would silently
    /// bar that event from every other moon for the rest of the session.
    /// </summary>
    public static void PublishCurrentEvent(object mEvent)
    {
        if (_currentEventsField == null)
            return;
        try
        {
            if (_currentEventsField.GetValue(null) is not IList list)
                return;

            list.Clear();
            if (mEvent != null)
                list.Add(mEvent);
        }
        catch (Exception e)
        {
            Plugin.DebugLog($"Publishing to BCMER's currentEvents failed: {Unwrap(e)}");
        }
    }

    private static string Unwrap(Exception e) => e.InnerException?.Message ?? e.Message;
}
