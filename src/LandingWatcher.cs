using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MonstersGordion;

/// <summary>
/// Persistent poller that drives the spawner lifecycle from ship state
/// (StartOfRound.shipHasLanded / shipIsLeaving) instead of relying on a
/// Harmony hook into openingDoorsSequence — with 60+ mods installed that
/// coroutine is frequently transpiled/replaced and the hook can be starved.
/// Polling public fields twice a second is unbreakable and costs nothing.
/// </summary>
internal sealed class LandingWatcher : MonoBehaviour
{
    private const float PollInterval = 0.5f;
    // If the doors-opening sequence is broken by another mod, shipHasLanded may
    // never be set even though we are standing on Gordion. After this many
    // seconds of "left orbit + company scene loaded + not leaving" we treat the
    // ship as landed anyway.
    private const float FallbackLandedAfter = 20f;

    private float _nextPoll;
    private bool _landedHandled;
    private float _fallbackTimer;

    internal static void Create()
    {
        var go = new GameObject("MonstersGordion_LandingWatcher");
        DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.HideAndDontSave;
        go.AddComponent<LandingWatcher>();
    }

    private void Update()
    {
        if (Time.unscaledTime < _nextPoll)
            return;
        _nextPoll = Time.unscaledTime + PollInterval;

        try
        {
            Poll();
        }
        catch (Exception e)
        {
            Plugin.DebugLog($"LandingWatcher poll failed: {e.Message}");
        }
    }

    private void Poll()
    {
        var sor = StartOfRound.Instance;
        if (sor == null)
        {
            // Main menu / disconnected.
            ResetLandingState();
            return;
        }

        bool landed = sor.shipHasLanded && !sor.shipIsLeaving;

        if (!landed && !sor.shipIsLeaving && !sor.inShipPhase && IsCompanySceneLoaded())
        {
            // Possible broken doors sequence: we're clearly down on Gordion but
            // shipHasLanded never went true. Give the normal path a grace
            // period, then force-start.
            _fallbackTimer += PollInterval;
            if (_fallbackTimer >= FallbackLandedAfter && !_landedHandled)
            {
                Plugin.Log.LogWarning(
                    "shipHasLanded was never set (another mod likely altered the landing sequence) — " +
                    "starting the spawner via fallback detection.");
                landed = true;
            }
        }
        else
        {
            _fallbackTimer = 0f;
        }

        if (landed && !_landedHandled)
        {
            _landedHandled = true;
            CompanyMonsterSpawner.OnShipLanded();
        }
        else if (!landed && _landedHandled && (sor.shipIsLeaving || !sor.shipHasLanded))
        {
            ResetLandingState();
            CompanyMonsterSpawner.OnShipLeaving();
        }
    }

    private void ResetLandingState()
    {
        _landedHandled = false;
        _fallbackTimer = 0f;
    }

    private static bool IsCompanySceneLoaded()
    {
        if (!CompanyMonsterSpawner.IsCompanyLevel())
            return false;
        Scene scene = SceneManager.GetSceneByName("CompanyBuilding");
        return scene.IsValid() && scene.isLoaded;
    }
}
