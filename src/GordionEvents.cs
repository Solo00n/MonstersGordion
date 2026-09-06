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
/// The Horde is not part of this roll. It is a standing feature of the moon
/// under its own [Horde] section, driven by the spawner, so a landing can have
/// both it and an event.
/// </summary>
internal static class GordionEvents
{
    private const string HordeColor = "#cc0000";

    private static readonly string[] HordeLines =
    {
        "Something has been wearing their faces for a while now.",
        "They came in from both ends of the dock. All of them.",
        "You stayed too long.",
    };

    /// <summary>Clears per-landing state. Called when the ship leaves.</summary>
    internal static void Reset()
    {
    }

    /// <summary>
    /// Rolls this landing's event. Runs from the LoadNewLevel prefix, i.e. before
    /// the level is built, because hazard-placing events read their densities
    /// during generation and would do nothing if executed at touchdown.
    /// </summary>
    internal static void RollForLanding()
    {
        Reset();

        if (!Plugin.Cfg.EnableStockEvents.Value)
            return;

        BcmeCompat.Scan();

        var candidates = new List<(string name, string display, int weight, object mEvent)>();
        CollectStockCandidates(candidates);

        if (candidates.Count == 0)
        {
            BcmeCompat.PublishCurrentEvent(null);
            Plugin.Log.LogInfo(
                "No event is eligible on the Company moon this landing — see the reasons above.");
            return;
        }

        var chosen = PickWeighted(candidates);
        Plugin.Log.LogInfo(
            $"Company moon event chosen: '{chosen.display}' " +
            $"(from {candidates.Count} eligible candidate(s)).");

        // Hand it straight back to BCMER to run.
        if (!BcmeCompat.Execute(chosen.mEvent, chosen.name))
        {
            BcmeCompat.PublishCurrentEvent(null);
            return;
        }

        // Tell BCMER's own bookkeeping what ran. That list is what BCMER's own
        // panel and overlays like LCBridgeOverlay read, so it is also what reports
        // the event to the player — this mod deliberately says nothing in chat.
        BcmeCompat.PublishCurrentEvent(chosen.mEvent);
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
    /// Announces a horde wave as it arrives, not on landing — the whole point of
    /// the delay is that it is a surprise. Nothing else reports the horde: it is
    /// not an MEvent, so BCMER's list and the overlays that read it never see it.
    /// </summary>
    internal static void AnnounceHorde(string enemyName)
    {
        string title = string.IsNullOrWhiteSpace(enemyName) ? "Horde" : $"{enemyName} Horde";
        Say(Format(title, HordeColor, HordeLines[UnityEngine.Random.Range(0, HordeLines.Length)]),
            Plugin.Cfg.HordeAnnounce.Value);
    }

    private static string Format(string title, string colorHex, string description)
    {
        if (string.IsNullOrWhiteSpace(colorHex) || colorHex[0] != '#')
            colorHex = "#ffffff";
        string line = $"<color={colorHex}>{title}</color>";
        return string.IsNullOrWhiteSpace(description) ? line : $"{line}: {description}";
    }

    private static void Say(string message, bool allowed)
    {
        if (!allowed)
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
