using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MidiPlayerTK;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

/// <summary>
/// Ownership: SceneFlowCoordinator may mutate only scene lifecycle concerns
/// (scene transitions, GeneratedTrack setup/teardown, and world rebuild orchestration).
/// </summary>
public sealed class SceneFlowCoordinator
{
    private readonly GameFlowManager _gameFlow;
    private readonly SessionStateCoordinator _session;
    private readonly BridgeCoordinator _bridge;
    private readonly List<Vector2Int> _vehicleCellsScratch = new(8);
    private readonly Dictionary<string, Action> _sceneHandlers;
    private bool _setupInFlight;
    private bool _setupDone;

    public SceneFlowCoordinator(GameFlowManager gameFlow, SessionStateCoordinator session, BridgeCoordinator bridge)
    {
        _gameFlow = gameFlow;
        _session = session;
        _bridge = bridge;

        _sceneHandlers = new()
        {
            { "TrackFinished", HandleTrackFinishedSceneSetup },
            { "TrackSelection", HandleTrackSelectionSceneSetup },
        };
    }

    public void BeginGeneratedTrackSetup()
    {
        if (_setupDone || _setupInFlight) return;
        _gameFlow.StartCoroutine(HandleTrackSceneSetupAsync());
    }

    public IEnumerator QuitToSelection()
    {
        Debug.Log($"[GFM] QuitToSelection: destroying players and resetting state (demoMode={_gameFlow.demoMode})");
        yield return _session.DestroyPlayersForSelection();

        _gameFlow.ClearVehicles();
        _gameFlow.ClearActiveTracks();
        _session.ResetForNewRun();

        _gameFlow.phaseTransitionManager = null;
        _gameFlow.activeDrumTrack = null;
        _gameFlow.controller = null;
        _gameFlow.dustGenerator = null;
        _gameFlow.spawnGrid = null;
        _gameFlow.ClearPlayerStatsGrid();

        _setupDone = false;
        _setupInFlight = false;

        yield return _gameFlow.StartCoroutine(TransitionToScene("Main"));
    }

    public IEnumerator TransitionToScene(string sceneName)
    {
        SceneManager.LoadScene(sceneName, LoadSceneMode.Single);
        yield return null;

        if (_sceneHandlers.TryGetValue(sceneName, out var handler))
            handler?.Invoke();
    }

    public void RegisterPlayerStatsGrid(Transform grid)
    {
        if (!grid) return;
        _gameFlow.SetPlayerStatsGrid(grid);
        Debug.Log($"[GFM] Registered PlayerStatsGrid from scene '{grid.gameObject.scene.name}'.");
    }

    public void UnregisterPlayerStatsGrid(Transform grid)
    {
        if (_gameFlow.PlayerStatsGrid == grid)
        {
            _gameFlow.ClearPlayerStatsGrid();
            Debug.Log("[GFM] PlayerStatsGrid unregistered (scene unload?).");
        }
    }

    public void RegisterTracksBundle(TracksBundleAnchor a)
    {
        if (!a) return;

        _gameFlow.phaseTransitionManager = a.phaseTransitionManager;
        _gameFlow.activeDrumTrack = a.drumTrack;
        _gameFlow.controller = a.controller;
        _gameFlow.dustGenerator = a.dustGenerator;
        _gameFlow.spawnGrid = a.spawnGrid;

        Debug.Log("[GFM] Tracks bundle registered from Generated Track scene.");
        BindSceneVoicesToTimingAuthority();
    }

    public void UnregisterTracksBundle(TracksBundleAnchor a)
    {
        if (!a) return;
        if (_gameFlow.phaseTransitionManager == a.phaseTransitionManager) _gameFlow.phaseTransitionManager = null;
        if (_gameFlow.activeDrumTrack == a.drumTrack) _gameFlow.activeDrumTrack = null;
        if (_gameFlow.controller == a.controller) _gameFlow.controller = null;
        if (_gameFlow.dustGenerator == a.dustGenerator) _gameFlow.dustGenerator = null;
        if (_gameFlow.spawnGrid == a.spawnGrid) _gameFlow.spawnGrid = null;
    }

    public IEnumerator StartNextMotifInPhase()
    {
        if (_gameFlow.dustGenerator != null)
            _gameFlow.dustGenerator.ResumeRegrowthAfterBridge();

        _bridge.StampMotifStartTime();
        _gameFlow.controller?.BeginNewMotif("MotifBridge");

        if (_gameFlow.phaseTransitionManager == null)
        {
            Debug.LogWarning("[GFM] No PhaseTransitionManager; cannot advance motif.");
            yield break;
        }

        var newMotif = _gameFlow.phaseTransitionManager.AdvanceMotif("GFM/StartNextMotifInPhase");

        if (newMotif == null)
        {
            _gameFlow.phaseTransitionManager.AdvancePhase("GFM/StartNextMotifInPhase(Exhausted)");
            yield return _gameFlow.StartCoroutine(StartNextPhaseMazeAndStar(doHardReset: false));
            yield break;
        }

        yield return _gameFlow.StartCoroutine(StartNextPhaseMazeAndStar(doHardReset: false));
    }

    public void HandleTrackFinishedSceneSetup()
    {
        _session.SetGameOverState();
        _gameFlow.harmony?.Initialize(null, null);
    }

    public void HandleTrackSelectionSceneSetup()
    {
        _session.ResetForTrackSelection();
    }

    public IEnumerator HandleTrackSceneSetupAsync()
    {
        _setupInFlight = true;
        _gameFlow.ClearVehicles();

        if (SceneManager.GetActiveScene().name != "GeneratedTrack")
        {
            _setupInFlight = false;
            yield break;
        }

        _session.CleanupDestroyedPlayers();
        if (_session.Players.Count == 0)
        {
            _setupInFlight = false;
            yield break;
        }

        float hardTimeout = 8f;
        float startTime = Time.time;

        TryFillTracksBundleIfMissing();
        yield return new WaitUntil(() => HaveAllCoreRefs() || Time.time - startTime > hardTimeout);
        TryFillTracksBundleIfMissing();
        if (!HaveAllCoreRefs())
        {
            _setupInFlight = false;
            yield break;
        }

        var shipProfiles = _session.Players
            .Select(p => ShipMusicalProfileLoader.GetProfile(p.GetSelectedShipName()))
            .ToList();

        _gameFlow.controller.ConfigureTracksFromShips(shipProfiles);
        _gameFlow.phaseTransitionManager.StartChapter(_gameFlow.phaseTransitionManager.FirstPhaseIndex, "GFM/Setup");
        _gameFlow.noteViz.Initialize();
        _gameFlow.harmony.Initialize(_gameFlow.activeDrumTrack, _gameFlow.controller);
        _gameFlow.activeDrumTrack.ManualStart();
        _gameFlow.dustGenerator.ManualStart();

        foreach (var lp in _session.Players)
            lp?.Launch();

        yield return null;

        if (_gameFlow.phaseTransitionManager.currentMotif != null)
            _gameFlow.harmony.SetActiveProfile(_gameFlow.phaseTransitionManager.currentMotif.chordProgression, true);

        yield return _gameFlow.StartCoroutine(StartNextPhaseMazeAndStar(doHardReset: false));
        yield return null;

        _session.SetCurrentState(GameState.Playing);
        _setupDone = true;
        _setupInFlight = false;
    }

    private void BindSceneVoicesToTimingAuthority()
    {
        if (_gameFlow.activeDrumTrack == null) return;

        var voices = UnityEngine.Object.FindObjectsOfType<MidiVoice>(includeInactive: true);
        foreach (var v in voices)
        {
            if (v == null) continue;
            if (v.GetComponentInParent<InstrumentTrack>() != null) continue;
            v.SetDrumTrack(_gameFlow.activeDrumTrack);
            var player = v.GetComponent<MidiStreamPlayer>() ?? v.GetComponentInParent<MidiStreamPlayer>();
            if (player != null) v.SetMidiStreamPlayer(player);
        }
    }

    private bool HaveAllCoreRefs() =>
        _gameFlow.activeDrumTrack && _gameFlow.controller && _gameFlow.dustGenerator &&
        _gameFlow.noteViz && _gameFlow.harmony && _gameFlow.phaseTransitionManager &&
        _gameFlow.spawnGrid && _gameFlow.PlayerStatsGrid;

    private void TryFillTracksBundleIfMissing()
    {
        _gameFlow.activeDrumTrack = _gameFlow.activeDrumTrack
            ? _gameFlow.activeDrumTrack
            : UnityEngine.Object.FindFirstObjectByType<DrumTrack>(FindObjectsInactive.Include);
        _gameFlow.controller = _gameFlow.controller
            ? _gameFlow.controller
            : UnityEngine.Object.FindFirstObjectByType<InstrumentTrackController>(FindObjectsInactive.Include);
        _gameFlow.dustGenerator = _gameFlow.dustGenerator
            ? _gameFlow.dustGenerator
            : UnityEngine.Object.FindFirstObjectByType<CosmicDustGenerator>(FindObjectsInactive.Include);
        _gameFlow.noteViz = _gameFlow.noteViz
            ? _gameFlow.noteViz
            : UnityEngine.Object.FindFirstObjectByType<NoteVisualizer>(FindObjectsInactive.Include);
        _gameFlow.harmony = _gameFlow.harmony
            ? _gameFlow.harmony
            : UnityEngine.Object.FindFirstObjectByType<HarmonyDirector>(FindObjectsInactive.Include);
        _gameFlow.phaseTransitionManager = _gameFlow.phaseTransitionManager
            ? _gameFlow.phaseTransitionManager
            : UnityEngine.Object.FindFirstObjectByType<PhaseTransitionManager>(FindObjectsInactive.Include);
        _gameFlow.spawnGrid = _gameFlow.spawnGrid
            ? _gameFlow.spawnGrid
            : UnityEngine.Object.FindFirstObjectByType<SpawnGrid>(FindObjectsInactive.Include);

        if (!_gameFlow.PlayerStatsGrid)
        {
            var anchor = UnityEngine.Object.FindFirstObjectByType<PlayerStatsGridAnchor>(FindObjectsInactive.Include);
            if (anchor) RegisterPlayerStatsGrid(anchor.transform);
        }
    }

    private void ResetPhaseBinStateAndGrid()
    {
        if (_gameFlow.controller?.tracks != null)
            foreach (var t in _gameFlow.controller.tracks)
                if (t) t.ResetBinStateForNewPhase();

        _gameFlow.controller?.ResetControllerBinGuards();

        if (_gameFlow.activeDrumTrack && _gameFlow.noteViz)
        {
            int leaderSteps = Mathf.Max(1, _gameFlow.activeDrumTrack.totalSteps);
            _gameFlow.noteViz.RequestLeaderGridChange(leaderSteps);
        }
    }

    public IEnumerator StartNextPhaseMazeAndStar(bool doHardReset = true)
    {
        var drums = _gameFlow.activeDrumTrack;
        var dust = _gameFlow.dustGenerator;

        if (doHardReset)
        {
            _gameFlow.controller?.BeginNewMotif("Next PhaseStart");
            _gameFlow.noteViz?.BeginNewMotif_ClearAll(true);
        }

        if (drums == null || dust == null)
            yield break;

        ResetPhaseBinStateAndGrid();

        yield return new WaitUntil(() =>
            drums.HasSpawnGrid() && drums.GetSpawnGridWidth() > 0 && drums.GetSpawnGridHeight() > 0 && Camera.main != null);

        var starCell = drums.GetRandomAvailableCell();
        if (starCell.x < 0) yield break;

        _vehicleCellsScratch.Clear();
        foreach (var v in _gameFlow.GetVehicles())
            if (v != null && v.isActiveAndEnabled)
                _vehicleCellsScratch.Add(drums.WorldToGridPosition(v.transform.position));

        dust.SetReservedVehicleCells(_vehicleCellsScratch);

        var profileForPhase = _gameFlow.phaseTransitionManager?.currentMotif?.starBehavior;
        if (profileForPhase != null) dust.ApplyProfile(profileForPhase);
        dust.ApplyActiveRoles(_gameFlow.phaseTransitionManager?.currentMotif?.GetActiveRoles());

        yield return _gameFlow.StartCoroutine(dust.GenerateMazeForPhaseWithPaths(starCell, _vehicleCellsScratch, 1.0f));

        _gameFlow.PlayVehiclePhaseInFx();

        if (_gameFlow.GetVehiclePhaseInDelaySeconds() > 0f)
            yield return new WaitForSeconds(_gameFlow.GetVehiclePhaseInDelaySeconds());

        drums.RequestPhaseStar(starCell);
        dust.ResetMazeGenerationFlag();
    }
}
