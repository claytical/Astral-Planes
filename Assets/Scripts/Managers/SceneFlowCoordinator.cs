using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MidiPlayerTK;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
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

    private const float IntroFadeInDuration  = 1.0f;
    private const float IntroHoldDuration    = 2.0f;
    private const float IntroFadeOutDuration = 0.8f;

    private CanvasGroup _screenFadeOverlay;

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
        if (GameFlowManager.VerboseLogging) Debug.Log($"[GFM] QuitToSelection: destroying players and resetting state (demoMode={_gameFlow.demoMode})");
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

    /// <summary>
    /// Lighter than QuitToSelection(): aborts the current session (tutorial back-out) by
    /// destroying all joined players and reloading TrackSelection directly, rather than Main.
    /// This preserves any pending PhaseLibraryStartConfig motif choice, since it's only
    /// consumed by HandleTrackSceneSetupAsync() on the GeneratedTrack path.
    /// </summary>
    public IEnumerator ReturnToJoinScreen()
    {
        yield return _session.DestroyPlayersForSelection();
        Hangar.Instance?.ResetForNewSession();
        yield return _gameFlow.StartCoroutine(TransitionToScene("TrackSelection"));
    }

    public IEnumerator TransitionToScene(string sceneName)
    {
        SceneManager.LoadScene(sceneName, LoadSceneMode.Single);
        yield return null;

        if (_sceneHandlers.TryGetValue(sceneName, out var handler))
            handler?.Invoke();
    }

    // Lives under GameFlowManager's DontDestroyOnLoad object, so it survives the
    // LoadSceneMode.Single swap and can cover both the fade-out and fade-in around it.
    private void EnsureScreenFadeOverlay()
    {
        if (_screenFadeOverlay != null) return;

        var go = new GameObject("ScreenFadeOverlay", typeof(RectTransform), typeof(Canvas), typeof(CanvasGroup), typeof(Image));
        go.transform.SetParent(_gameFlow.transform, worldPositionStays: false);

        var canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = short.MaxValue;

        var image = go.GetComponent<Image>();
        image.color = Color.black;
        image.raycastTarget = false;

        var rect = (RectTransform)go.transform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        _screenFadeOverlay = go.GetComponent<CanvasGroup>();
        _screenFadeOverlay.alpha = 0f;
        go.SetActive(false);
    }

    public IEnumerator FadeScreen(bool toBlack, float duration = 0.6f)
    {
        EnsureScreenFadeOverlay();
        if (toBlack) _screenFadeOverlay.gameObject.SetActive(true);

        float from = toBlack ? 0f : 1f;
        float to = toBlack ? 1f : 0f;
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            _screenFadeOverlay.alpha = Mathf.Lerp(from, to, Mathf.Clamp01(elapsed / duration));
            yield return null;
        }
        _screenFadeOverlay.alpha = to;
        if (!toBlack) _screenFadeOverlay.gameObject.SetActive(false);
    }

    public void RegisterPlayerStatsGrid(Transform grid)
    {
        if (!grid) return;
        _gameFlow.SetPlayerStatsGrid(grid);
        if (GameFlowManager.VerboseLogging) Debug.Log($"[GFM] Registered PlayerStatsGrid from scene '{grid.gameObject.scene.name}'.");
    }

    public void UnregisterPlayerStatsGrid(Transform grid)
    {
        if (_gameFlow.PlayerStatsGrid == grid)
        {
            _gameFlow.ClearPlayerStatsGrid();
            if (GameFlowManager.VerboseLogging) Debug.Log("[GFM] PlayerStatsGrid unregistered (scene unload?).");
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

        if (GameFlowManager.VerboseLogging) Debug.Log("[GFM] Tracks bundle registered from Generated Track scene.");
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

        yield return _gameFlow.StartCoroutine(PlayIntroScreenSequenceAsync());

        var shipProfiles = _session.Players
            .Select(p => ShipMusicalProfileLoader.GetProfile(p.GetSelectedShipName()))
            .ToList();

        _gameFlow.controller.ConfigureTracksFromShips(shipProfiles);

        if (PhaseLibraryStartConfig.HasPendingStart)
        {
            _gameFlow.phaseTransitionManager.StartChapter(PhaseLibraryStartConfig.PhaseIndex, "GFM/Setup/Library");
            _gameFlow.phaseTransitionManager.JumpToMotif(PhaseLibraryStartConfig.MotifId, PhaseLibraryStartConfig.MotifIndex, "GFM/Setup/Library");
            PhaseLibraryStartConfig.Consume();
        }
        else
        {
            _gameFlow.phaseTransitionManager.StartChapter(_gameFlow.phaseTransitionManager.FirstPhaseIndex, "GFM/Setup");
        }
        _gameFlow.noteViz.Initialize();
        _gameFlow.harmony.Initialize(_gameFlow.activeDrumTrack, _gameFlow.controller);
        _gameFlow.activeDrumTrack.ManualStart();
        _gameFlow.dustGenerator.ManualStart();
        _gameFlow.SetupBinRingController();

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

    private IEnumerator PlayIntroScreenSequenceAsync()
    {
        GameObject introGo = null;
        foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
        {
            var match = root.GetComponentsInChildren<Transform>(includeInactive: true)
                            .FirstOrDefault(t => t.name == "Intro Screen");
            if (match != null) { introGo = match.gameObject; break; }
        }

        if (introGo == null)
        {
            Debug.LogWarning("[SceneFlow] 'Intro Screen' not found — skipping intro sequence.");
            yield break;
        }

        var cg = introGo.GetComponent<CanvasGroup>() ?? introGo.AddComponent<CanvasGroup>();
        introGo.SetActive(true);
        cg.alpha = 0f;

        float elapsed = 0f;
        while (elapsed < IntroFadeInDuration)
        {
            elapsed += Time.deltaTime;
            cg.alpha = Mathf.Clamp01(elapsed / IntroFadeInDuration);
            yield return null;
        }
        cg.alpha = 1f;

        yield return new WaitForSeconds(IntroHoldDuration);

        elapsed = 0f;
        while (elapsed < IntroFadeOutDuration)
        {
            elapsed += Time.deltaTime;
            cg.alpha = 1f - Mathf.Clamp01(elapsed / IntroFadeOutDuration);
            yield return null;
        }
        cg.alpha = 0f;
        introGo.SetActive(false);
    }

    private void BindSceneVoicesToTimingAuthority()
    {
        if (_gameFlow.activeDrumTrack == null) return;

        var voices = UnityEngine.Object.FindObjectsByType<MidiVoice>(FindObjectsInactive.Include, FindObjectsSortMode.None);
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
            : UnityEngine.Object.FindAnyObjectByType<DrumTrack>(FindObjectsInactive.Include);
        _gameFlow.controller = _gameFlow.controller
            ? _gameFlow.controller
            : UnityEngine.Object.FindAnyObjectByType<InstrumentTrackController>(FindObjectsInactive.Include);
        _gameFlow.dustGenerator = _gameFlow.dustGenerator
            ? _gameFlow.dustGenerator
            : UnityEngine.Object.FindAnyObjectByType<CosmicDustGenerator>(FindObjectsInactive.Include);
        _gameFlow.noteViz = _gameFlow.noteViz
            ? _gameFlow.noteViz
            : UnityEngine.Object.FindAnyObjectByType<NoteVisualizer>(FindObjectsInactive.Include);
        _gameFlow.harmony = _gameFlow.harmony
            ? _gameFlow.harmony
            : UnityEngine.Object.FindAnyObjectByType<HarmonyDirector>(FindObjectsInactive.Include);
        _gameFlow.phaseTransitionManager = _gameFlow.phaseTransitionManager
            ? _gameFlow.phaseTransitionManager
            : UnityEngine.Object.FindAnyObjectByType<PhaseTransitionManager>(FindObjectsInactive.Include);
        _gameFlow.spawnGrid = _gameFlow.spawnGrid
            ? _gameFlow.spawnGrid
            : UnityEngine.Object.FindAnyObjectByType<SpawnGrid>(FindObjectsInactive.Include);

        if (!_gameFlow.PlayerStatsGrid)
        {
            var anchor = UnityEngine.Object.FindAnyObjectByType<PlayerStatsGridAnchor>(FindObjectsInactive.Include);
            if (anchor) RegisterPlayerStatsGrid(anchor.transform);
        }
    }

    private void ResetPhaseBinStateAndGrid()
    {
        if (_gameFlow.controller?.tracks != null)
            foreach (var t in _gameFlow.controller.tracks)
                if (t) t.ResetBinStateForNewPhase();

        if (_gameFlow.activeDrumTrack && _gameFlow.noteViz)
        {
            int leaderSteps = Mathf.Max(1, _gameFlow.activeDrumTrack.totalSteps);
            _gameFlow.noteViz.RequestLeaderGridChange(leaderSteps);
        }
    }

    /// <summary>
    /// Applies a path-choice interstitial's outcome: jumps PhaseTransitionManager to the chosen
    /// motif or phase, repositions every vehicle to the mirrored entry edge of wherever they
    /// exited the interstitial, then runs the ordinary maze/star regeneration. Mirrors
    /// StartNextMotifInPhase line-for-line except for the PTM call and the edge reposition.
    /// </summary>
    /// <param name="exitAnchorCell">
    /// The winning exit's grid cell in the interstitial maze. Its along-edge coordinate (row for
    /// Left/Right, column for Top/Bottom) carries over to the entry point on the mirrored edge of
    /// the new maze, so exiting bottom-right lands you bottom-left, not at a random spot on the
    /// left edge.
    /// </param>
    public IEnumerator StartChosenMotifOrPhase(int? motifIndex, int? phaseIndex, BoundaryWrap.BoundarySide entryEdge, Vector2Int exitAnchorCell)
    {
        if (_gameFlow.dustGenerator != null)
            _gameFlow.dustGenerator.ResumeRegrowthAfterBridge();

        _bridge.StampMotifStartTime();
        _gameFlow.controller?.BeginNewMotif("MotifBridge");

        if (_gameFlow.phaseTransitionManager == null)
        {
            Debug.LogWarning("[GFM] No PhaseTransitionManager; cannot start chosen motif/phase.");
            yield break;
        }

        if (phaseIndex.HasValue)
            _gameFlow.phaseTransitionManager.StartChapter(phaseIndex.Value, "GFM/PathChoice");
        else if (motifIndex.HasValue)
            _gameFlow.phaseTransitionManager.JumpToMotifIndex(motifIndex.Value, "GFM/PathChoice");

        var lockedOwners = new List<LocalPlayer>();
        var entryCells = StageVehiclesOffscreenAtEdge(entryEdge, exitAnchorCell, lockedOwners);

        yield return _gameFlow.StartCoroutine(StartNextPhaseMazeAndStar(doHardReset: false, vehicleCellOverrides: entryCells));

        yield return _gameFlow.StartCoroutine(DriveVehiclesInFromOffscreen(entryCells));

        foreach (var lp in lockedOwners)
            lp?.SetInputLocked(false);

        _gameFlow.PlayVehiclePhaseInFx();
        if (_gameFlow.GetVehiclePhaseInDelaySeconds() > 0f)
            yield return new WaitForSeconds(_gameFlow.GetVehiclePhaseInDelaySeconds());
    }

    // Mirror of the boundary side a vehicle exited from: right<->left, top<->bottom.
    public static BoundaryWrap.BoundarySide MirrorSide(BoundaryWrap.BoundarySide side) => side switch
    {
        BoundaryWrap.BoundarySide.Left  => BoundaryWrap.BoundarySide.Right,
        BoundaryWrap.BoundarySide.Right => BoundaryWrap.BoundarySide.Left,
        BoundaryWrap.BoundarySide.Top   => BoundaryWrap.BoundarySide.Bottom,
        _                                => BoundaryWrap.BoundarySide.Top,
    };

    // Disables/enables a vehicle's own collider so it can be pulled straight through a solid
    // boundary wall during the scripted exit/entry transition. Move()-driven physics otherwise
    // collides with the wall like any other obstacle and bounces the vehicle back onto the
    // screen instead of carrying it through — this is what actually lets the "grab and pull
    // off-screen" / "sucked into the new maze" motion cross the boundary at all.
    public static void SetVehicleSolid(Vehicle v, bool solid)
    {
        if (v == null) return;
        var col = v.GetComponent<Collider2D>();
        if (col != null) col.enabled = solid;
    }

    // World-space direction pointing away from the maze through `side` — the negation of
    // ConfigureExitParticleFlow's "inward" vector (PathChoiceCoordinator.cs), shared here so
    // exit-carry and entry-staging agree on which way is "out."
    public static Vector2 OutwardDirection(BoundaryWrap.BoundarySide side) => side switch
    {
        BoundaryWrap.BoundarySide.Left  => Vector2.left,
        BoundaryWrap.BoundarySide.Right => Vector2.right,
        BoundaryWrap.BoundarySide.Top   => Vector2.up,
        _ /* Bottom */                   => Vector2.down,
    };

    // The true screen-edge coordinate on the boundary-normal axis for `side`. Left/Right pull
    // from the real wall collider bounds (Boundaries.cs derives those straight from
    // orthographicSize*aspect with no UI adjustment, so they already equal the true edge);
    // Top/Bottom pull from the camera's orthographic size instead, since those walls are
    // deliberately pulled in below/above the UI bars (see PathChoiceCoordinator.
    // SnapToPhysicalBoundary). Null if no camera/Boundaries instance exists yet.
    private static float? TrueEdgeCoordinate(BoundaryWrap.BoundarySide side)
    {
        var cam = Camera.main;
        var boundaries = Boundaries.Instance;
        switch (side)
        {
            case BoundaryWrap.BoundarySide.Left:
                if (boundaries != null && boundaries.leftBoundary != null) return boundaries.leftBoundary.bounds.max.x;
                return cam != null ? -cam.orthographicSize * cam.aspect : (float?)null;
            case BoundaryWrap.BoundarySide.Right:
                if (boundaries != null && boundaries.rightBoundary != null) return boundaries.rightBoundary.bounds.min.x;
                return cam != null ? cam.orthographicSize * cam.aspect : (float?)null;
            case BoundaryWrap.BoundarySide.Top:
                return cam != null ? cam.orthographicSize : (float?)null;
            default: // Bottom
                return cam != null ? -cam.orthographicSize : (float?)null;
        }
    }

    // Pushes `worldPos` past `side`'s true edge by `margin` along the boundary-normal axis,
    // keeping the along-edge coordinate unchanged — the off-screen staging point for an entry,
    // and (by construction) exactly the threshold IsPastOffscreenThreshold checks against for
    // an exit, so both stages agree on what "off-screen" means.
    public static Vector2 OffscreenPointAlong(Vector2 worldPos, BoundaryWrap.BoundarySide side, float margin)
    {
        float? edge = TrueEdgeCoordinate(side);
        // Fail toward "definitely off-screen," never toward the caller's original (on-screen)
        // position — silently returning worldPos unchanged here would stage/carry a vehicle at
        // its visible target instead of hiding it, the exact "reappeared where I shouldn't have"
        // symptom this method exists to prevent.
        if (edge == null) return FallbackOffscreenPoint(worldPos, side, margin);

        float signedMargin = (side == BoundaryWrap.BoundarySide.Left || side == BoundaryWrap.BoundarySide.Bottom) ? -margin : margin;
        if (side == BoundaryWrap.BoundarySide.Left || side == BoundaryWrap.BoundarySide.Right)
            worldPos.x = edge.Value + signedMargin;
        else
            worldPos.y = edge.Value + signedMargin;
        return worldPos;
    }

    // Used only when TrueEdgeCoordinate can't determine the real screen edge (no Camera.main /
    // no Boundaries.Instance yet) — pushes far enough in the outward direction to guarantee
    // "off screen" for any reasonable camera setup, rather than silently handing back the
    // caller's original (on-screen) position.
    private static Vector2 FallbackOffscreenPoint(Vector2 worldPos, BoundaryWrap.BoundarySide side, float margin)
    {
        const float safeFallbackDistance = 100f;
        return worldPos + OutwardDirection(side) * Mathf.Max(margin, safeFallbackDistance);
    }

    // True once `worldPos` has cleared `side`'s true edge by at least `margin`.
    public static bool IsPastOffscreenThreshold(Vector2 worldPos, BoundaryWrap.BoundarySide side, float margin)
    {
        float? edge = TrueEdgeCoordinate(side);
        // Fail toward "keep driving it outward," not "already off-screen" — the latter would
        // let the exit-carry loop stop immediately while the vehicle is still fully visible.
        if (edge == null) return false;

        return side switch
        {
            BoundaryWrap.BoundarySide.Left  => worldPos.x <= edge.Value - margin,
            BoundaryWrap.BoundarySide.Right => worldPos.x >= edge.Value + margin,
            BoundaryWrap.BoundarySide.Top   => worldPos.y >= edge.Value + margin,
            _ /* Bottom */                   => worldPos.y <= edge.Value - margin,
        };
    }

    // Generalizes the bottom-row-only spawn placement LocalPlayer.SelectionLaunch.cs uses at
    // initial launch to an arbitrary edge, so a vehicle can enter a new maze from wherever the
    // mirrored exit of the interstitial it just left puts it. Rather than teleporting straight to
    // that entry cell, stages each vehicle off-screen along the same row/column and locks its
    // input; StartChosenMotifOrPhase drives it in from there once the maze — generated against
    // these same returned cells, not live position — is ready, so entry reads as a slide-in
    // instead of an instant pop. Deliberately does NOT carve a keep-clear pocket here:
    // StartNextPhaseMazeAndStar's own ClearMaze()+GenerateMazeForPhaseWithPaths call carves each
    // vehicle's landing disk itself (CarvePermanentDisk) using the cells returned here, and
    // Vehicle's own per-frame RefreshVehicleKeepClearIfNeeded() is intentionally a no-op for the
    // whole bridge sequence (gfm.BridgePending), so there's no window where dust could bury one.
    private Dictionary<Vehicle, Vector2Int> StageVehiclesOffscreenAtEdge(
        BoundaryWrap.BoundarySide edge, Vector2Int exitAnchorCell, List<LocalPlayer> lockedOwners)
    {
        var entryCells = new Dictionary<Vehicle, Vector2Int>();

        var drums = _gameFlow.activeDrumTrack;
        if (drums == null) return entryCells;

        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return entryCells;

        // Carry the exit's along-edge position over to the entry point instead of picking a
        // random spot on the mirrored edge: exiting bottom-right should land bottom-left, not
        // wherever chance puts it on the left edge.
        int row = Mathf.Clamp(exitAnchorCell.y, 1, Mathf.Max(1, h - 2));
        int col = Mathf.Clamp(exitAnchorCell.x, 0, Mathf.Max(0, w - 1));
        float margin = _gameFlow.PathTransitionOffscreenMargin;

        foreach (var v in _gameFlow.GetVehicles())
        {
            if (v == null) continue;

            var cell = edge switch
            {
                BoundaryWrap.BoundarySide.Left  => new Vector2Int(0, row),
                BoundaryWrap.BoundarySide.Right => new Vector2Int(w - 1, row),
                BoundaryWrap.BoundarySide.Top   => new Vector2Int(col, h - 1),
                _ /* Bottom */                  => new Vector2Int(col, 1),
            };
            entryCells[v] = cell;

            // Already false from PathChoiceCoordinator's exit-carry, but stay defensive here
            // too — this is the method actually responsible for a vehicle sitting off-screen,
            // and it must not collide with the boundary wall while staged there.
            SetVehicleSolid(v, false);

            Vector2 stagingPos = OffscreenPointAlong(drums.GridToWorldPosition(cell), edge, margin);
            if (v.rb != null)
            {
                v.rb.position = stagingPos;
                v.rb.linearVelocity = Vector2.zero;
            }
            else
            {
                v.transform.position = stagingPos;
            }

            foreach (var lp in _session.Players)
            {
                if (lp != null && lp.plane == v)
                {
                    lp.SetInputLocked(true);
                    lockedOwners.Add(lp);
                    break;
                }
            }
        }

        return entryCells;
    }

    // Drives each staged vehicle from its off-screen point (set by StageVehiclesOffscreenAtEdge)
    // in to its real entry cell, mirroring PathChoiceCoordinator's exit-carry loop so entry and
    // exit read as the same kind of scripted travel rather than two different mechanisms.
    private IEnumerator DriveVehiclesInFromOffscreen(Dictionary<Vehicle, Vector2Int> entryCells)
    {
        var drums = _gameFlow.activeDrumTrack;
        if (drums == null || entryCells.Count == 0) yield break;

        const float arriveDistanceSq = 0.75f * 0.75f;
        float timeout = _gameFlow.PathTransitionTravelTimeout;
        var pending = new List<Vehicle>(entryCells.Keys);
        float elapsed = 0f;

        while (elapsed < timeout && pending.Count > 0)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var v = pending[i];
                if (v == null) { pending.RemoveAt(i); continue; }

                Vector2 targetWorld = drums.GridToWorldPosition(entryCells[v]);
                Vector2 toTarget = targetWorld - (Vector2)v.transform.position;
                if (toTarget.sqrMagnitude <= arriveDistanceSq)
                {
                    SnapVehicleTo(v, targetWorld);
                    SetVehicleSolid(v, true);
                    pending.RemoveAt(i);
                    continue;
                }
                v.Move(toTarget.normalized);
            }
            elapsed += Time.deltaTime;
            yield return null;
        }

        // Timeout safety net: don't leave anyone stranded just outside the maze.
        foreach (var v in pending)
        {
            if (v == null) continue;
            SnapVehicleTo(v, drums.GridToWorldPosition(entryCells[v]));
            SetVehicleSolid(v, true);
        }
    }

    private static void SnapVehicleTo(Vehicle v, Vector2 worldPos)
    {
        if (v.rb != null)
        {
            v.rb.position = worldPos;
            v.rb.linearVelocity = Vector2.zero;
        }
        else
        {
            v.transform.position = worldPos;
        }
    }

    public IEnumerator StartNextPhaseMazeAndStar(
        bool doHardReset = true, IReadOnlyDictionary<Vehicle, Vector2Int> vehicleCellOverrides = null)
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

        // When called from the path-choice entry sequence, vehicles are deliberately staged
        // off-screen at this point (see StageVehiclesOffscreenAtEdge) — their live transform
        // position would carve/reserve the wrong cells, so prefer the intended entry cell.
        Vector2Int CellFor(Vehicle v) =>
            vehicleCellOverrides != null && vehicleCellOverrides.TryGetValue(v, out var overrideCell)
                ? overrideCell
                : drums.WorldToGridPosition(v.transform.position);

        _vehicleCellsScratch.Clear();
        foreach (var v in _gameFlow.GetVehicles())
            if (v != null && v.isActiveAndEnabled)
                _vehicleCellsScratch.Add(CellFor(v));

        dust.SetReservedVehicleCells(_vehicleCellsScratch);

        var profileForPhase = _gameFlow.phaseTransitionManager?.currentMotif?.starBehavior;
        if (profileForPhase != null) dust.ApplyProfile(profileForPhase);
        dust.ApplyActiveRoles(_gameFlow.phaseTransitionManager?.currentMotif);
        dust.ApplyMotifGeoConfig(_gameFlow.phaseTransitionManager?.currentMotif);

        var trapMotif = _gameFlow.phaseTransitionManager?.currentMotif;
        System.Action<List<(Vector2Int, Vector3)>> trapCallback = null;
        if (trapMotif != null && trapMotif.spawnVehicleTrap)
        {
            trapCallback = cellsToFill =>
            {
                var allVehicles = _gameFlow.GetVehicles();
                var vehicleList = allVehicles != null && allVehicles.Count > 0
                    ? new List<Vehicle>(allVehicles)
                    : new List<Vehicle>(UnityEngine.Object.FindObjectsByType<Vehicle>(FindObjectsSortMode.None));
                foreach (var v in vehicleList)
                {
                    if (v == null || !v.isActiveAndEnabled) continue;
                    Vector2Int center = CellFor(v);
                    if (trapMotif.trapShape == TrapShape.Circle)
                    {
                        int inner = Mathf.Max(0, trapMotif.trapRadius - 1);
                        dust.InjectTrapCellsIntoStagger(
                            cellsToFill, center, trapMotif.trapRadius, inner,
                            trapMotif.trapRole);
                    }
                    else
                    {
                        int r = trapMotif.trapRadius;
                        var perim = new List<Vector2Int>(r * 8);
                        for (int dx = -r; dx <= r; dx++)
                        {
                            perim.Add(new Vector2Int(center.x + dx, center.y + r));
                            perim.Add(new Vector2Int(center.x + dx, center.y - r));
                        }
                        for (int dy = -r + 1; dy <= r - 1; dy++)
                        {
                            perim.Add(new Vector2Int(center.x - r, center.y + dy));
                            perim.Add(new Vector2Int(center.x + r, center.y + dy));
                        }
                        dust.InjectTrapCellsFromList(cellsToFill, perim, trapMotif.trapRole);
                    }
                }
            };
        }

        yield return _gameFlow.StartCoroutine(dust.GenerateMazeForPhaseWithPaths(starCell, _vehicleCellsScratch, 1.0f, onBeforeGrowth: trapCallback));

        dust.SetTravelCorridors(_vehicleCellsScratch, starCell, radiusCells: 1);

        // When vehicleCellOverrides is set, vehicles are still staged off-screen (path-choice
        // entry) — StartChosenMotifOrPhase plays the phase-in FX itself once they've actually
        // slid into position, so firing it here would burst at the wrong (off-screen) spot.
        if (vehicleCellOverrides == null)
        {
            _gameFlow.PlayVehiclePhaseInFx();

            if (_gameFlow.GetVehiclePhaseInDelaySeconds() > 0f)
                yield return new WaitForSeconds(_gameFlow.GetVehiclePhaseInDelaySeconds());
        }

        drums.RequestPhaseStar(starCell);
        dust.ResetMazeGenerationFlag();
    }
}
