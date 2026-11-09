using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Ownership: SessionStateCoordinator may mutate only player session state
/// (joined players, readiness flags, game-over guards, and run-state transitions).
/// </summary>
public sealed class SessionStateCoordinator
{
    private readonly GameFlowManager _gameFlow;
    private readonly List<LocalPlayer> _players = new();
    private bool _hasGameOverStarted;
    private bool _matchEnded;
    private bool _modeLocked;
    private bool _ghostCycleInProgress;
    private bool _bridgePending;
    private GameState _currentState = GameState.Begin;

    public SessionStateCoordinator(GameFlowManager gameFlow)
    {
        _gameFlow = gameFlow;
    }

    private GameMode _mode = GameMode.Coop;

    public event Action<GameMode> ModeChanged;
    public event Action PlayersChanged;
    public event Action<GameState> GameStateChanged;
    public event Action<bool> BridgePendingChanged;
    public event Action<bool> GhostCycleChanged;

    public IReadOnlyList<LocalPlayer> Players => _players;
    public List<LocalPlayer> MutablePlayers => _players;
    public GameState CurrentState => _currentState;
    public bool HasGameOverStarted => _hasGameOverStarted;
    public bool GhostCycleInProgress => _ghostCycleInProgress;
    public bool BridgePending => _bridgePending;

    /// <summary>Selected mode. Competitive modes only take effect with 2+ players (see <see cref="IsCompetitive"/>).</summary>
    public GameMode Mode => _mode;
    public bool IsCompetitive => _mode != GameMode.Coop && _players.Count > 1;
    public bool MatchEnded => _matchEnded;

    /// <summary>Multiplayer: set by the extra confirm press after everyone is ready; the mode can no longer change.</summary>
    public bool ModeLocked => _modeLocked;

    /// <summary>2+ players, all ready, mode not yet locked: waiting for one more confirm press before the tutorial.</summary>
    public bool AwaitingModeConfirm =>
        !_modeLocked && _players.Count >= 2 && _players.All(p => p != null && p.IsReady);

    public void LockMode() => _modeLocked = true;
    public void UnlockMode() => _modeLocked = false;

    /// <summary>Players still in the match (competitive: not permanently eliminated).</summary>
    public int AliveCount => _players.Count(p => p != null && !p.IsEliminated);

    public void SetMode(GameMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        ModeChanged?.Invoke(mode);
    }

    /// <summary>Steps through the modes; dir &gt; 0 forward, otherwise backward.</summary>
    public void CycleMode(int dir)
    {
        int count = Enum.GetValues(typeof(GameMode)).Length;
        SetMode((GameMode)(((int)_mode + (dir > 0 ? 1 : count - 1)) % count));
    }

    /// <summary>
    /// Claims the one-shot match end. False if it already started, or a bridge/maze ghost cycle is
    /// running (callers re-run their check once the cycle clears).
    /// </summary>
    public bool TryBeginMatchEnd()
    {
        if (_matchEnded || _hasGameOverStarted || _ghostCycleInProgress) return false;
        _matchEnded = true;
        _hasGameOverStarted = true;
        SetGameOverState();
        return true;
    }

    public void RegisterPlayer(LocalPlayer player)
    {
        if (player == null || _players.Contains(player)) return;
        _players.Add(player);
        PlayersChanged?.Invoke();
    }

    /// <summary>Removes a single player (e.g. backing out of ship select). Returns true if no players remain.</summary>
    public bool UnregisterPlayer(LocalPlayer player)
    {
        _players.Remove(player);
        PlayersChanged?.Invoke();
        return _players.Count == 0;
    }

    public bool ReadyToPlay() => _currentState == GameState.Playing && _players.Count > 0;

    public void SetCurrentState(GameState state)
    {
        _currentState = state;
        GameStateChanged?.Invoke(state);
    }

    public void SetBridgePending(bool value)
    {
        _bridgePending = value;
        BridgePendingChanged?.Invoke(value);
    }

    public void SetGhostCycleInProgress(bool value)
    {
        _ghostCycleInProgress = value;
        GhostCycleChanged?.Invoke(value);
    }

    public void CheckAllPlayersReady(Action beginTutorial, Action beginGameplay)
    {
        if (!_players.All(p => p != null && p.IsReady)) return;
        if (AwaitingModeConfirm) return;

        if (ControlTutorialDirector.Instance != null)
        {
            beginTutorial?.Invoke();
            return;
        }

        beginGameplay?.Invoke();
    }

    public void BeginGameAfterTutorial(Action beginGameplay)
    {
        beginGameplay?.Invoke();
    }

    public void StartShipSelectionPhase()
    {
        SetCurrentState(GameState.Selection);
        if (GameFlowManager.VerboseLogging) Debug.Log("✅ Ship selection phase started. Waiting for players to join.");
    }

    public bool AllPlayersOutOfEnergy() =>
        _players.Where(p => p != null).All(p => !p.IsReady && !p.IsInContinueWindow && !p.IsRespawning);

    public bool CheckAllPlayersOutOfEnergy()
    {
        if (_hasGameOverStarted || _ghostCycleInProgress) return false;

        if (AllPlayersOutOfEnergy())
        {
            _hasGameOverStarted = true;
            return true;
        }

        return false;
    }

    public void ResetForTrackSelection()
    {
        Hangar.Instance?.ResetForNewSession();
        SetCurrentState(GameState.Selection);
        _hasGameOverStarted = false;
        _matchEnded = false;
        _modeLocked = false;

        CleanupDestroyedPlayers();
        foreach (var player in _players)
        {
            player.IsReady = false;
            player.ResetReady();
            player.CreatePlayerSelect();
            player.SetStats();
        }
    }

    public void ResetForNewRun()
    {
        _hasGameOverStarted = false;
        _matchEnded = false;
        _modeLocked = false;
        SetGhostCycleInProgress(false);
        SetBridgePending(false);
        SetCurrentState(GameState.Begin);
    }

    public void SetGameOverState()
    {
        SetCurrentState(GameState.GameOver);
    }

    public void CleanupDestroyedPlayers()
    {
        _players.RemoveAll(p => p == null);
    }

    public IEnumerator DestroyPlayersForSelection()
    {
        foreach (var lp in _players)
        {
            if (lp == null) continue;
            var go = lp.gameObject;
            if (GameFlowManager.VerboseLogging) Debug.Log($"[GFM] Destroying LocalPlayer GameObject '{go.name}'");
            UnityEngine.Object.Destroy(go);
        }

        _players.Clear();
        yield break;
    }
}
