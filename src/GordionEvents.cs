using System;
using System.Collections.Generic;
using MonstersGordion.Compat;
using UnityEngine;

namespace MonstersGordion;

/// <summary>
/// One event per landing on the Company moon.
///
/// BrutalCompanyMinusExtraReborn refuses to run any event here — its
/// EventManager.ModifyLevel prefix returns early on levelID 3, before the event
/// roll is even reached — so this class does the roll instead. Stock BCMER
/// events are only ever *selected* by us; whether they may run at all is still
/// decided entirely by the user's BCMER config, which BcmeCompat.TryResolve
/// checks: the enabled flag, the type weight, the Special/Beta gates, the moon
/// lists, and the event's own AddEventIfOnly() veto.
///
/// Custom events are ours, written against what this moon actually provides.
/// They are deliberately NOT registered into BCMER's own list: they could never
/// be chosen there (same levelID gate), and BCMER matches config entries to
/// events by list index, so inserting into it risks shuffling a user's settings.
/// </summary>
internal static class GordionEvents
{
    /// <summary>Identifier of the one custom event, kept out of BCMER's namespace.</summary>
    internal const string MaskedHorde = "MaskedHordeGordion";

    private const string MaskedHordeTitle = "Masked Horde";
    private const string MaskedHordeColor = "#cc0000";

    private static readonly string[] MaskedHordeLines =
    {
        "Something has been wearing their faces for a while now.",
        "They came in from both ends of the dock. All of them.",
        "You stayed too long.",
    };

    /// <summary>The custom event armed for this landing, or null.</summary>
    internal static string ArmedCustomEvent { get; private set; }

    private static string _pendingAnnouncement;
    private static bool _announced;

    /// <summary>Whether the timed Masked Horde was rolled for this landing.</summary>
    internal static bool HordeArmed => ArmedCustomEvent == MaskedHorde;

    /// <summary>Clears per-landing state. Called when the ship leaves.</summary>
    internal static void Reset()
    {
        ArmedCustomEvent = null;
        _pendingAnnouncement = null;
        _announced = false;
    }

    /// <summary>
    /// Rolls this landing's event. Runs from the LoadNewLevel prefix, i.e. before
    /// the level is built, because hazard-placing events read their densities
    /// during generation and would do nothing if executed at touchdown.
    /// </summary>
    internal static void RollForLanding()
    {
        Reset();

        var cfg = Plugin.Cfg;
        bool wantStock = cfg.EnableStockEvents.Value;
        bool wantCustom = cfg.EnableCustomEvents.Value;
        if (!wantStock && !wantCustom)
            return;

        BcmeCompat.Scan();

        var candidates = new List<(string name, string display, int weight, object mEvent)>();

        if (wantStock)
            CollectStockCandidates(candidates);

        if (wantCustom)
            CollectCustomCandidates(candidates, AverageWeight(candidates));

        if (candidates.Count == 0)
        {
            Plugin.Log.LogInfo(
                "No event is eligible on the Company moon this landing — see the reasons above.");
            return;
        }

        var chosen = PickWeighted(candidates);
        Plugin.Log.LogInfo(
            $"Company moon event chosen: '{chosen.display}' " +
            $"(from {candidates.Count} eligible candidate(s)).");

        if (chosen.mEvent != null)
        {
            // Stock BCMER event: hand it straight back to BCMER to run.
            if (!BcmeCompat.Execute(chosen.mEvent, chosen.name))
                return;
            _pendingAnnouncement = Format(
                chosen.display,
                BcmeCompat.ColorOf(chosen.name),
                BcmeCompat.DescriptionOf(chosen.name));
        }
        else
        {
            // Custom event: armed now, run by the spawner once we are on the ground.
            ArmedCustomEvent = chosen.name;
            Plugin.DebugLog($"Custom event '{chosen.name}' armed for this landing.");
        }
    }

    private static void CollectStockCandidates(
        List<(string name, string display, int weight, object mEvent)> candidates)
    {
        if (!BcmeCompat.Present)
        {
            Plugin.Log.LogInfo(
                "EnableStockEvents is on but BrutalCompanyMinus is not installed — skipping.");
            return;
        }
        if (!BcmeCompat.EventsUsable)
        {
            Plugin.Log.LogWarning(
                "EnableStockEvents is on but BCMER's event API is unavailable — skipping.");
            return;
        }

        foreach (string name in Plugin.Cfg.ResolveStockEventWhitelist())
        {
            object mEvent = BcmeCompat.TryResolve(name, out string reason);
            if (mEvent == null)
            {
                Plugin.Log.LogInfo($"  stock event '{name}' skipped: {reason}.");
                continue;
            }

            int weight = Mathf.Max(1, BcmeCompat.WeightOf(name));
            candidates.Add((name, name, weight, mEvent));
            Plugin.DebugLog($"  stock event '{name}' eligible ({BcmeCompat.TypeOf(name)}, w{weight}).");
        }
    }

    private static void CollectCustomCandidates(
        List<(string name, string display, int weight, object mEvent)> candidates, int weight)
    {
        if (!Plugin.Cfg.MaskedHorde.Value)
            return;

        // Spawning Masked is the point of the event, so an enemy list that bars
        // them bars the event too — silently ignoring the user's list would be
        // worse than not running.
        EnemyCatalog.RefreshExclusions();
        if (EnemyCatalog.IsExcluded("Masked"))
        {
            Plugin.Log.LogInfo(
                "  custom event 'Masked Horde' skipped: 'Masked' is excluded by ExcludedEnemies.");
            return;
        }

        candidates.Add((MaskedHorde, MaskedHordeTitle, weight, null));
        Plugin.DebugLog($"  custom event '{MaskedHordeTitle}' eligible (w{weight}).");
    }

    /// <summary>
    /// The weight a custom event gets: the average of the eligible stock weights,
    /// so it is about as likely as any single stock event rather than swamping or
    /// vanishing against BCMER's four-digit numbers. With no stock events in the
    /// pool the value is arbitrary, since the roll is then uniform anyway.
    /// </summary>
    private static int AverageWeight(
        List<(string name, string display, int weight, object mEvent)> candidates)
    {
        if (candidates.Count == 0)
            return 1;
        long total = 0;
        foreach (var c in candidates)
            total += c.weight;
        return (int)Mathf.Max(1, total / candidates.Count);
    }

    private static (string name, string display, int weight, object mEvent) PickWeighted(
        List<(string name, string display, int weight, object mEvent)> candidates)
    {
        long total = 0;
        foreach (var c in candidates)
            total += c.weight;
        if (total <= 0)
            return candidates[UnityEngine.Random.Range(0, candidates.Count)];

        long roll = (long)(UnityEngine.Random.value * total);
        foreach (var c in candidates)
        {
            roll -= c.weight;
            if (roll < 0)
                return c;
        }
        return candidates[candidates.Count - 1];
    }

    // ------------------------------------------------------------ announcements

    /// <summary>
    /// Announces the stock event picked at level load. Deferred to touchdown so
    /// the HUD exists and the message is not lost during the loading screen.
    /// </summary>
    internal static void AnnouncePending()
    {
        if (_announced || _pendingAnnouncement == null)
            return;
        _announced = true;
        Say(_pendingAnnouncement);
    }

    /// <summary>Announces the Masked Horde as it actually arrives, not on landing.</summary>
    internal static void AnnounceMaskedHorde()
    {
        Say(Format(MaskedHordeTitle, MaskedHordeColor,
            MaskedHordeLines[UnityEngine.Random.Range(0, MaskedHordeLines.Length)]));
    }

    private static string Format(string title, string colorHex, string description)
    {
        if (string.IsNullOrWhiteSpace(colorHex) || colorHex[0] != '#')
            colorHex = "#ffffff";
        string line = $"<color={colorHex}>{title}</color>";
        return string.IsNullOrWhiteSpace(description) ? line : $"{line}: {description}";
    }

    private static void Say(string message)
    {
        if (!Plugin.Cfg.AnnounceEvents.Value)
            return;
        try
        {
            if (HUDManager.Instance != null)
                HUDManager.Instance.AddChatMessage(message);
        }
        catch (Exception e)
        {
            Plugin.DebugLog($"Event announcement failed: {e.Message}");
        }
    }
}
