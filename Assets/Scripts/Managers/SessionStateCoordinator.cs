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
    private bool _ghostCycleInProgress;
    private bool _bridgePending;
    private GameState _currentState = GameState.Begin;

    public SessionStateCoordinator(GameFlowManager gameFlow)
    {
        _gameFlow = gameFlow;
    }

    public event Action<GameState> GameStateChanged;
    public event Action<bool> BridgePendingChanged;
    public event Action<bool> GhostCycleChanged;

    public IReadOnlyList<LocalPlayer> Players => _players;
    public List<LocalPlayer> MutablePlayers => _players;
    public GameState CurrentState => _currentState;
    public bool HasGameOverStarted => _hasGameOverStarted;
    public bool GhostCycleInProgress => _ghostCycleInProgress;
    public bool BridgePending => _bridgePending;

    public void RegisterPlayer(LocalPlayer player)
    {
        if (player == null || _players.Contains(player)) return;
        _players.Add(player);
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
        Debug.Log("✅ Ship selection phase started. Waiting for players to join.");
    }

    public bool CheckAllPlayersOutOfEnergy()
    {
        if (_hasGameOverStarted || _ghostCycleInProgress) return false;

        if (_players.Where(p => p != null).All(p => !p.IsReady || p.GetVehicleEnergy() <= 0f))
        {
            _hasGameOverStarted = true;
            return true;
        }

        return false;
    }

    public void ResetForTrackSelection()
    {
        SetCurrentState(GameState.Selection);
        _hasGameOverStarted = false;

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
            Debug.Log($"[GFM] Destroying LocalPlayer GameObject '{go.name}'");
            UnityEngine.Object.Destroy(go);
        }

        _players.Clear();
        yield break;
    }
}
