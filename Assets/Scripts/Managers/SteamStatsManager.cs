// Steam Stats plumbing for gameplay analytics. Mirrors SteamManager.cs's init-then-poll
// shape and lives alongside it (co-located on the SteamManager prefab). Exposes a tiny,
// generic API (SetMilestoneOnce / IncrementCounter / FlushIfDirty) that gameplay-facing
// hooks in SteamStatsManager.Hooks.cs call into. Every Steam call is fail-silent: gameplay
// must behave identically whether Steam analytics submission succeeds, fails, or is
// unavailable entirely.
#if !(UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define DISABLESTEAMWORKS
#endif

using UnityEngine;
#if !DISABLESTEAMWORKS
using Steamworks;
#endif

[DisallowMultipleComponent]
public partial class SteamStatsManager : MonoBehaviour
{
    public static SteamStatsManager Instance { get; private set; }

    [Tooltip("Steam accounts (SteamID64) excluded from analytics submission. Leave unassigned to disable exclusion.")]
    [SerializeField] private SteamStatsExclusionList exclusionList;

    // Implemented in SteamStatsManager.Hooks.cs — wires the six gameplay-event subscriptions.
    partial void HooksUpdate();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }

    private void Update()
    {
        TryBecomeReady();
        HooksUpdate();
    }

#if !DISABLESTEAMWORKS
    private Callback<UserStatsReceived_t> _userStatsReceivedCallback;
    private bool _statsReady;
    private bool _excluded;
    private bool _dirty;

    private void TryBecomeReady()
    {
        if (_statsReady) return;
        if (!SteamManager.Initialized) return;

        if (_userStatsReceivedCallback == null)
        {
            // Note: this SDK's SteamUserStats does not expose RequestCurrentStats() — Valve's
            // own doc comment says stats now sync automatically before the process begins, so
            // there is nothing to explicitly request. We still listen for the callback in case
            // it fires; if it never does, SteamManager.Initialized alone is treated as
            // sufficient per that guarantee (see MarkReady below).
            try
            {
                _userStatsReceivedCallback = Callback<UserStatsReceived_t>.Create(OnUserStatsReceived);
            }
            catch (System.Exception e)
            {
                if (GameFlowManager.VerboseLogging) Debug.LogWarning($"[SteamStatsManager] Failed to register UserStatsReceived_t callback: {e.Message}");
            }
        }

        MarkReady();
    }

    private void OnUserStatsReceived(UserStatsReceived_t cb)
    {
        if (cb.m_eResult != EResult.k_EResultOK)
        {
            if (GameFlowManager.VerboseLogging) Debug.LogWarning($"[SteamStatsManager] UserStatsReceived_t failed: {cb.m_eResult}");
            return;
        }
        MarkReady();
    }

    private void MarkReady()
    {
        if (_statsReady) return;
        _statsReady = true;
        ResolveExclusion();
    }

    private void ResolveExclusion()
    {
        try
        {
            var id = SteamUser.GetSteamID();
            string id64 = id.m_SteamID.ToString();
            _excluded = exclusionList != null
                && exclusionList.excludedSteamId64s != null
                && exclusionList.excludedSteamId64s.Contains(id64);

            if (GameFlowManager.VerboseLogging)
                Debug.Log($"[SteamStatsManager] Ready. SteamID64={id64} excluded={_excluded}");
        }
        catch (System.Exception e)
        {
            _excluded = false;
            if (GameFlowManager.VerboseLogging) Debug.LogWarning($"[SteamStatsManager] Failed to resolve SteamID for exclusion check: {e.Message}");
        }
    }

    public void SetMilestoneOnce(string statName)
    {
        if (!_statsReady || _excluded || string.IsNullOrEmpty(statName)) return;

        try
        {
            if (!SteamUserStats.GetStat(statName, out int current))
            {
                if (GameFlowManager.VerboseLogging) Debug.LogWarning($"[SteamStatsManager] GetStat failed for '{statName}'");
                return;
            }
            if (current != 0) return;

            if (SteamUserStats.SetStat(statName, 1))
            {
                _dirty = true;
                if (GameFlowManager.VerboseLogging) Debug.Log($"[SteamStatsManager] SetStat '{statName}' = 1");
            }
            else if (GameFlowManager.VerboseLogging)
            {
                Debug.LogWarning($"[SteamStatsManager] SetStat failed for '{statName}'");
            }
        }
        catch (System.Exception e)
        {
            if (GameFlowManager.VerboseLogging) Debug.LogWarning($"[SteamStatsManager] SetMilestoneOnce('{statName}') threw: {e.Message}");
        }
    }

    public void IncrementCounter(string statName, int by = 1)
    {
        if (!_statsReady || _excluded || string.IsNullOrEmpty(statName)) return;

        try
        {
            if (!SteamUserStats.GetStat(statName, out int current))
            {
                if (GameFlowManager.VerboseLogging) Debug.LogWarning($"[SteamStatsManager] GetStat failed for '{statName}'");
                return;
            }

            if (SteamUserStats.SetStat(statName, current + by))
            {
                _dirty = true;
                if (GameFlowManager.VerboseLogging) Debug.Log($"[SteamStatsManager] IncrementCounter '{statName}' {current} -> {current + by}");
            }
            else if (GameFlowManager.VerboseLogging)
            {
                Debug.LogWarning($"[SteamStatsManager] SetStat failed for '{statName}'");
            }
        }
        catch (System.Exception e)
        {
            if (GameFlowManager.VerboseLogging) Debug.LogWarning($"[SteamStatsManager] IncrementCounter('{statName}') threw: {e.Message}");
        }
    }

    public void FlushIfDirty()
    {
        if (!_dirty || !_statsReady || _excluded) return;

        try
        {
            if (SteamUserStats.StoreStats())
            {
                _dirty = false;
                if (GameFlowManager.VerboseLogging) Debug.Log("[SteamStatsManager] StoreStats OK");
            }
            else if (GameFlowManager.VerboseLogging)
            {
                Debug.LogWarning("[SteamStatsManager] StoreStats failed — will retry on next flush.");
            }
        }
        catch (System.Exception e)
        {
            if (GameFlowManager.VerboseLogging) Debug.LogWarning($"[SteamStatsManager] StoreStats threw: {e.Message}");
        }
    }
#else
    private void TryBecomeReady() { }
    public void SetMilestoneOnce(string statName) { }
    public void IncrementCounter(string statName, int by = 1) { }
    public void FlushIfDirty() { }
#endif
}
