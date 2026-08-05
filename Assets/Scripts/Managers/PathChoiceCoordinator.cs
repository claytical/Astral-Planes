using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Ownership: PathChoiceCoordinator may mutate only the path-choice interstitial (growing the
/// choice maze, tracking exit triggers, picking a winner, redirecting other vehicles, and
/// handing off to PhaseTransitionManager/SceneFlowCoordinator once a path is chosen).
/// </summary>
public sealed class PathChoiceCoordinator
{
    private readonly GameFlowManager _gameFlow;
    private readonly SessionStateCoordinator _session;
    private readonly SceneFlowCoordinator _sceneFlow;

    private readonly List<MazePathExit> _allExits = new();
    private MazePathExit _winnerExit;
    private Vehicle _winnerVehicle;

    public PathChoiceCoordinator(GameFlowManager gameFlow, SessionStateCoordinator session, SceneFlowCoordinator sceneFlow)
    {
        _gameFlow = gameFlow;
        _session = session;
        _sceneFlow = sceneFlow;
    }

    /// <summary>
    /// Called between the ring spin-off and the ordinary linear motif/phase advance. If there's
    /// nothing real to choose between (0 or 1 option), falls straight through to the existing
    /// linear path unchanged. Otherwise grows a blind, multi-exit interstitial maze and waits
    /// for a vehicle to reach one of the exits before resuming.
    /// </summary>
    public IEnumerator RunPathChoiceAndAdvance()
    {
        var ptm = _gameFlow.phaseTransitionManager;
        var dust = _gameFlow.dustGenerator;
        var drums = _gameFlow.activeDrumTrack;

        if (ptm == null || dust == null || drums == null)
        {
            yield return _gameFlow.StartCoroutine(_sceneFlow.StartNextMotifInPhase());
            yield break;
        }

        var remainingMotifs = ptm.GetRemainingMotifIndices();
        bool crossPhase = remainingMotifs.Count == 0;
        IReadOnlyList<int> targets = crossPhase ? ptm.GetRemainingPhaseIndices() : remainingMotifs;

        if (targets.Count <= 1)
        {
            yield return _gameFlow.StartCoroutine(_sceneFlow.StartNextMotifInPhase());
            yield break;
        }

        bool prevWrap = Boundaries.WrapEnabled;
        Boundaries.SetWrapEnabled(false); // no accidental screen-wrap through a tunnel mouth

        var vehicleCells = new List<Vector2Int>();
        foreach (var v in _gameFlow.GetVehicles())
            if (v != null && v.isActiveAndEnabled)
                vehicleCells.Add(drums.WorldToGridPosition(v.transform.position));

        var pattern = ScriptableObject.CreateInstance<MazePatternConfig>();
        pattern.patternType = MazePatternType.Tunnels;
        pattern.tunnels.corridorWidth = 3;
        pattern.tunnels.corridorStep = 6; // 3-cell-thick walls: "thick dust" around the vehicle
        pattern.porousBorder.exitCount = targets.Count;

        _allExits.Clear();
        _winnerExit = null;
        _winnerVehicle = null;

        List<CosmicDustGenerator.PathExitInfo> exitInfos = null;
        yield return _gameFlow.StartCoroutine(dust.GenerateInterstitialMaze(
            vehicleCells, pattern, totalSpawnDuration: 1.0f,
            onExitsComputed: infos => exitInfos = infos));

        Object.Destroy(pattern);

        if (exitInfos == null || exitInfos.Count == 0)
        {
            Boundaries.SetWrapEnabled(prevWrap);
            yield return _gameFlow.StartCoroutine(_sceneFlow.StartNextMotifInPhase());
            yield break;
        }

        int exitCountUsed = Mathf.Min(exitInfos.Count, targets.Count);
        for (int i = 0; i < exitCountUsed; i++)
        {
            var info = exitInfos[i];
            int? motifIdx = crossPhase ? (int?)null : targets[i];
            int? phaseIdx = crossPhase ? (int?)targets[i] : (int?)null;
            _allExits.Add(CreateExitTrigger(drums, info, motifIdx, phaseIdx));
        }

        var exitAnchors = new List<Vector2Int>(_allExits.Count);
        foreach (var e in _allExits) exitAnchors.Add(e.AnchorCell);
        dust.SetTravelCorridors(vehicleCells, exitAnchors, radiusCells: 1);

        yield return new WaitUntil(() => _winnerExit != null);

        yield return _gameFlow.StartCoroutine(RedirectLosersAndAdvance(vehicleCells, dust, drums));

        Boundaries.SetWrapEnabled(prevWrap);
    }

    // Called by MazePathExit.OnTriggerEnter2D. First vehicle to reach any exit wins; every other
    // exit's collider is disabled in the same call so no later same-frame trigger can race it.
    public void OnVehicleReachedExit(MazePathExit exit, Vehicle vehicle)
    {
        if (_winnerExit != null) return;
        _winnerExit = exit;
        _winnerVehicle = vehicle;

        foreach (var e in _allExits)
            if (e != exit) e.SetSealed(true);
    }

    private MazePathExit CreateExitTrigger(DrumTrack drums, CosmicDustGenerator.PathExitInfo info, int? motifIdx, int? phaseIdx)
    {
        float cellSize = Mathf.Max(0.01f, drums.GetCellWorldSize());

        Vector2 min = drums.GridToWorldPosition(info.GapCells[0]);
        Vector2 max = min;
        foreach (var gc in info.GapCells)
        {
            Vector2 wp = drums.GridToWorldPosition(gc);
            min = Vector2.Min(min, wp);
            max = Vector2.Max(max, wp);
        }

        var go = new GameObject($"PathExit_{info.Side}_{info.AnchorCell.x}_{info.AnchorCell.y}");
        Vector2 triggerCenter = SnapToPhysicalBoundary((min + max) * 0.5f, info.Side);
        go.transform.position = triggerCenter;

        var col = go.AddComponent<BoxCollider2D>();
        col.isTrigger = true;
        col.size = new Vector2(
            Mathf.Max(cellSize, (max.x - min.x) + cellSize),
            Mathf.Max(cellSize, (max.y - min.y) + cellSize));

        GameObject visualInstance = null;
        if (_gameFlow.PathExitVisualPrefab != null)
        {
            Vector2 visualCenter = OffsetVisualToScreenEdge(triggerCenter, info.Side);
            visualInstance = Object.Instantiate(_gameFlow.PathExitVisualPrefab, visualCenter, Quaternion.identity, go.transform);
            ConfigureExitParticleFlow(visualInstance, info.Side);
            // Play explicitly rather than relying on the prefab's "Play On Awake" setting, so a
            // burst-style particle effect fires reliably no matter how the prefab is authored.
            foreach (var ps in visualInstance.GetComponentsInChildren<ParticleSystem>())
                ps.Play();
        }

        var exit = go.AddComponent<MazePathExit>();
        exit.Configure(this, info.AnchorCell, new List<Vector2Int>(info.GapCells), info.Side, motifIdx, phaseIdx, visualInstance);
        return exit;
    }

    // The gap-cell average centers correctly along the boundary edge, but the boundary-normal
    // coordinate (the axis perpendicular to the edge) comes from DrumTrack's grid→world mapping,
    // which insets top/bottom for UI deadspace (see BoundaryWrap's own warning: "the bottom
    // boundary may be aligned to UI deadspace rather than world origin"). Snap that one axis to
    // the real wall collider's inner face instead, so the trigger sits exactly where a vehicle
    // can actually reach it — this MUST stay the true collision wall (not the further-out screen
    // edge) or the trigger becomes unreachable, sealed off behind solid boundary collision.
    private static Vector2 SnapToPhysicalBoundary(Vector2 center, BoundaryWrap.BoundarySide side)
    {
        var boundaries = Boundaries.Instance;
        if (boundaries == null) return center;

        BoxCollider2D wall = side switch
        {
            BoundaryWrap.BoundarySide.Left  => boundaries.leftBoundary,
            BoundaryWrap.BoundarySide.Right => boundaries.rightBoundary,
            BoundaryWrap.BoundarySide.Top   => boundaries.topBoundary,
            _ /* Bottom */                  => boundaries.bottomBoundary,
        };
        if (wall == null) return center;

        Bounds b = wall.bounds;
        switch (side)
        {
            case BoundaryWrap.BoundarySide.Left:  center.x = b.max.x; break;
            case BoundaryWrap.BoundarySide.Right: center.x = b.min.x; break;
            case BoundaryWrap.BoundarySide.Top:   center.y = b.min.y; break;
            default /* Bottom */:                 center.y = b.max.y; break;
        }
        return center;
    }

    // Left/Right walls aren't UI-adjusted (Boundaries.cs sets them straight from
    // orthographicSize*aspect with no anchor override), so their collider bounds already equal
    // the true screen edge — the visual can sit right on the trigger with no further offset.
    // Top/Bottom walls ARE deliberately pulled in below/above the UI bars
    // (AlignTopToPlayerStatsGrid / AlignBottomToVisualizer) so vehicles collide well short of
    // the true edge; the trigger has to stay there to remain reachable, but the particle effect
    // alone can read past that solid wall, so push just its world position out to the camera's
    // true edge for Top/Bottom.
    private static Vector2 OffsetVisualToScreenEdge(Vector2 triggerCenter, BoundaryWrap.BoundarySide side)
    {
        if (side != BoundaryWrap.BoundarySide.Top && side != BoundaryWrap.BoundarySide.Bottom)
            return triggerCenter;

        var cam = Camera.main;
        if (cam == null) return triggerCenter;

        triggerCenter.y = side == BoundaryWrap.BoundarySide.Top ? cam.orthographicSize : -cam.orthographicSize;
        return triggerCenter;
    }

    // Points the particle effect's Velocity Over Lifetime module inward from whichever border
    // side the exit sits on — toward wherever the player currently is — instead of a single
    // baked-in direction that only reads correctly for one side (e.g. a Right exit needs
    // negative X to flow inward; a Left exit needs positive X for the same visual intent).
    private void ConfigureExitParticleFlow(GameObject visualInstance, BoundaryWrap.BoundarySide side)
    {
        if (visualInstance == null) return;

        Vector2 inward = side switch
        {
            BoundaryWrap.BoundarySide.Left  => Vector2.right,
            BoundaryWrap.BoundarySide.Right => Vector2.left,
            BoundaryWrap.BoundarySide.Top   => Vector2.down,
            _ /* Bottom */                  => Vector2.up,
        };
        float speed = _gameFlow.PathExitParticleSpeed;

        // The prefab's Shape module is authored so its "front" sits centered on a Right-edge
        // boundary line (inward == Vector2.left, i.e. 180°) with zero shape rotation. Deriving
        // the shape's Z rotation from the same inward vector (rather than a second hand-tuned
        // per-side table) keeps shape orientation and flow direction from ever drifting apart.
        float inwardAngleDeg = Mathf.Atan2(inward.y, inward.x) * Mathf.Rad2Deg;
        float shapeZ = inwardAngleDeg - 180f;

        foreach (var ps in visualInstance.GetComponentsInChildren<ParticleSystem>())
        {
            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.x = new ParticleSystem.MinMaxCurve(inward.x * speed);
            vel.y = new ParticleSystem.MinMaxCurve(inward.y * speed);

            var shape = ps.shape;
            shape.rotation = new Vector3(0f, 0f, shapeZ);
        }
    }

    private IEnumerator RedirectLosersAndAdvance(List<Vector2Int> vehicleCells, CosmicDustGenerator dust, DrumTrack drums)
    {
        var winnerExit = _winnerExit;
        var winnerVehicle = _winnerVehicle;

        // Releases every losing lane via the same diff-on-recompute logic SetTravelCorridors
        // already uses (cells that fall out of the new set get released and regrow requested).
        dust.SetTravelCorridors(vehicleCells, winnerExit.AnchorCell, radiusCells: 1);

        foreach (var exit in _allExits)
        {
            if (exit == winnerExit || exit == null) continue;
            dust.SpawnDustAtCells(exit.GapCells, MusicalRole.None, dust.SolidDustColor(), energy01: 1f, growInSeconds: 0.5f, solid: true);
        }

        Vector2 targetWorld = drums.GridToWorldPosition(winnerExit.AnchorCell);
        var side = winnerExit.Side;
        Vector2 outward = SceneFlowCoordinator.OutwardDirection(side);
        float offscreenMargin = _gameFlow.PathTransitionOffscreenMargin;

        // Two stages, driven in the same loop: losers converge on the winner's exit anchor, then
        // (like the winner, who's already there) get carried outward through the boundary until
        // they clear the true screen edge — so the maze swap never fires while a vehicle is
        // still visibly mid-tunnel. All vehicles get input locked, including the winner, so the
        // carry-through reads as a scripted exit rather than fighting player input.
        var losers = new List<Vehicle>();
        var carrying = new List<Vehicle>();
        var lockedOwners = new List<LocalPlayer>();
        foreach (var v in _gameFlow.GetVehicles())
        {
            if (v == null) continue;

            foreach (var lp in _session.Players)
            {
                if (lp != null && lp.plane == v)
                {
                    lp.SetInputLocked(true);
                    lockedOwners.Add(lp);
                    break;
                }
            }

            if (v == winnerVehicle)
            {
                carrying.Add(v);
                // Solid collision with the boundary wall would otherwise bounce the vehicle back
                // onto the screen instead of letting it carry through — see SetVehicleSolid.
                SceneFlowCoordinator.SetVehicleSolid(v, false);
            }
            else losers.Add(v);
        }

        float timeout = _gameFlow.PathTransitionTravelTimeout;
        const float arriveDistanceSq = 0.75f * 0.75f;
        float elapsed = 0f;
        while (elapsed < timeout && (losers.Count > 0 || carrying.Count > 0))
        {
            for (int i = losers.Count - 1; i >= 0; i--)
            {
                var v = losers[i];
                if (v == null) { losers.RemoveAt(i); continue; }

                Vector2 toTarget = targetWorld - (Vector2)v.transform.position;
                if (toTarget.sqrMagnitude <= arriveDistanceSq)
                {
                    losers.RemoveAt(i);
                    carrying.Add(v);
                    SceneFlowCoordinator.SetVehicleSolid(v, false);
                    continue;
                }
                v.Move(toTarget.normalized);
            }

            for (int i = carrying.Count - 1; i >= 0; i--)
            {
                var v = carrying[i];
                if (v == null) { carrying.RemoveAt(i); continue; }

                if (SceneFlowCoordinator.IsPastOffscreenThreshold(v.transform.position, side, offscreenMargin))
                {
                    carrying.RemoveAt(i);
                    continue;
                }
                v.Move(outward);
            }

            elapsed += Time.deltaTime;
            yield return null;
        }

        foreach (var lp in lockedOwners)
            lp?.SetInputLocked(false);

        int? chosenMotif = winnerExit.TargetMotifIndex;
        int? chosenPhase = winnerExit.TargetPhaseIndex;
        var mirroredEdge = SceneFlowCoordinator.MirrorSide(winnerExit.Side);
        var exitAnchorCell = winnerExit.AnchorCell;

        foreach (var exit in _allExits)
            if (exit != null) Object.Destroy(exit.gameObject);
        _allExits.Clear();
        _winnerExit = null;
        _winnerVehicle = null;

        yield return _gameFlow.StartCoroutine(_sceneFlow.StartChosenMotifOrPhase(chosenMotif, chosenPhase, mirroredEdge, exitAnchorCell));
    }
}
