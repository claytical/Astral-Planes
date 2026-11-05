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
    /// Called between the ring spin-off and the next motif. If the motif maze offers no move at
    /// all, falls straight through to the existing linear path unchanged. Otherwise grows a
    /// blind interstitial maze with one exit per open maze direction (a single exit is just a
    /// one-way corridor) and waits for a vehicle to reach one before resuming.
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

        // Exits come from the motif maze: inward and/or outward along the current motif's arm
        // (or all four arms from the hub), each on the screen edge matching its direction. Only
        // a library with nothing to move to (a lone hub) skips the interstitial.
        var mazeExits = ptm.GetMazeExits();
        if (mazeExits.Count == 0)
        {
            yield return _gameFlow.StartCoroutine(_sceneFlow.StartNextMotifInPhase());
            yield break;
        }

        bool prevWrap = Boundaries.WrapEnabled;
        Boundaries.SetWrapEnabled(false); // no accidental screen-wrap through a tunnel mouth

        var vehicleCells = new List<Vector2Int>();
        float widestVehicleRadiusWorld = 0f;
        foreach (var v in _gameFlow.GetVehicles())
        {
            if (v == null || !v.isActiveAndEnabled) continue;
            vehicleCells.Add(drums.WorldToGridPosition(v.transform.position));
            widestVehicleRadiusWorld = Mathf.Max(widestVehicleRadiusWorld, v.GetColliderBoundingRadiusWorld());
        }

        // Corridor width is authored in grid cells, but a cell's world size is
        // resolution-dependent (DrumTrackGridMapper.GetTileDiameterWorld()) while ship size is
        // fixed in world units — so the width has to be derived from the widest active ship at
        // generation time, not a flat cell count, or it can end up narrower than a ship's hull
        // at some grid resolutions. See Vehicle.Plow.cs's identical collider-to-cells pattern.
        float cellWorld = Mathf.Max(0.01f, drums.GetCellWorldSize());
        float clearanceWorld = widestVehicleRadiusWorld * 2f + cellWorld * 0.3f; // ship diameter + margin
        int minCorridorWidthCells = Mathf.Max(3, Mathf.CeilToInt(clearanceWorld / cellWorld));

        // GenerateInterstitialMaze always modulates at a fixed busyness01=0.5 (see its
        // busyness01Override below), so grow the authored width until the modulated result
        // still clears the widest active ship, instead of assuming the ~0.95x factor away.
        int corridorWidth = minCorridorWidthCells;
        while (MazeDensityModulation.TunnelsCorridorWidth(corridorWidth, 0.5f) < minCorridorWidthCells)
            corridorWidth++;

        var pattern = ScriptableObject.CreateInstance<MazePatternConfig>();
        pattern.patternType = MazePatternType.Tunnels;
        pattern.tunnels.corridorWidth = corridorWidth;
        pattern.tunnels.corridorStep = corridorWidth + 3; // thick-dust walls around the vehicle
        pattern.porousBorder.exitCount = mazeExits.Count;
        foreach (var e in mazeExits)
            pattern.porousBorder.exitSides.Add(MotifMaze.SideOf(e.Dir));

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

        // Match by edge, not list order: the generator reports one exit per requested side.
        foreach (var target in mazeExits)
        {
            var side = MotifMaze.SideOf(target.Dir);
            int infoIdx = exitInfos.FindIndex(i => i.Side == side);
            if (infoIdx < 0) continue;
            _allExits.Add(CreateExitTrigger(drums, exitInfos[infoIdx], target.Phase, target.Motif));
        }

        SpawnWisp(drums, vehicleCells, ptm);

        var exitAnchors = new List<Vector2Int>(_allExits.Count);
        foreach (var e in _allExits) exitAnchors.Add(e.AnchorCell);
        dust.SetTravelCorridors(vehicleCells, exitAnchors, radiusCells: 1);

        yield return new WaitUntil(() => _winnerExit != null || _session.AllPlayersOutOfEnergy());

        if (_winnerExit == null)
        {
            // Every player ran out of energy before reaching an exit — bail out of the path
            // choice instead of hanging forever, so the deferred game-over check (suppressed
            // while GhostCycleInProgress is true) can fire once BridgeCoordinator clears it.
            foreach (var exit in _allExits)
                if (exit != null) Object.Destroy(exit.gameObject);
            _allExits.Clear();
            Boundaries.SetWrapEnabled(prevWrap);
            yield break;
        }

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

    // Spawns one wisp (not one per exit) targeting a randomly chosen exit among those just
    // created. Purely additive/decorative to the path-choice mechanic: touching the wisp
    // never resolves the choice itself, only OnVehicleReachedExit does that — see Wisp.cs.
    private void SpawnWisp(DrumTrack drums, List<Vector2Int> vehicleCells, PhaseTransitionManager ptm)
    {
        var prefab = _gameFlow.WispPrefab;
        if (prefab == null || _allExits.Count == 0 || vehicleCells.Count == 0) return;

        var targetExit = _allExits[UnityEngine.Random.Range(0, _allExits.Count)];

        // Same peek pattern as CreateExitTrigger's role-color preview: resolve the wisp's
        // bridging sample from the motif its assigned exit actually leads into, not the
        // outgoing motif. Falls back to the current motif if the peek misses, rather than
        // leaving the bridge sample null.
        MotifProfile targetMotif = ptm.PeekMotif(targetExit.TargetPhaseIndex, targetExit.TargetMotifIndex) ?? ptm.currentMotif;

        var go = Object.Instantiate(prefab, Vector3.zero, Quaternion.identity);
        bool placed = go.TryGetComponent<Wisp>(out var wisp) &&
                      wisp.Initialize(drums, vehicleCells[0], targetExit.AnchorCell, targetMotif);
        if (!placed)
            Object.Destroy(go);
    }

    private MazePathExit CreateExitTrigger(DrumTrack drums, CosmicDustGenerator.PathExitInfo info, int phaseIdx, int motifIdx)
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

        // Only the boundary-normal axis gets the extra depth (so the capture zone reaches back
        // into the corridor — see PathExitCaptureDepthCells below). Padding the tangential axis
        // too would reach past the actual gap cells into the solid wall on either side of the
        // opening, where the particle visual isn't shown — letting a vehicle sliding along the
        // wall trip the trigger.
        bool horizontalWall = info.Side == BoundaryWrap.BoundarySide.Top || info.Side == BoundaryWrap.BoundarySide.Bottom;
        float captureDepthWorld = Mathf.Max(cellSize, _gameFlow.PathExitCaptureDepthCells * cellSize);
        Vector2 inward = InwardDirection(info.Side);

        var col = go.AddComponent<BoxCollider2D>();
        col.isTrigger = true;
        col.size = new Vector2(
            horizontalWall ? Mathf.Max(cellSize, max.x - min.x) : captureDepthWorld,
            horizontalWall ? captureDepthWorld : Mathf.Max(cellSize, max.y - min.y));
        // Grow the capture zone inward from the wall face rather than centering it on the wall,
        // so detection starts well before the vehicle reaches the edge instead of only once it's
        // already touching the wall — see PathExitCaptureDepthCells.
        col.offset = inward * (captureDepthWorld * 0.5f);

        GameObject visualInstance = null;
        if (_gameFlow.PathExitVisualPrefab != null)
        {
            Vector2 visualCenter = OffsetVisualToScreenEdge(triggerCenter, info.Side);
            visualInstance = Object.Instantiate(_gameFlow.PathExitVisualPrefab, visualCenter, Quaternion.identity, go.transform);
            ConfigureExitParticleFlow(visualInstance, info.Side);
            var targetMotif = _gameFlow.phaseTransitionManager.PeekMotif(phaseIdx, motifIdx);
            ConfigureExitParticleRoleColors(visualInstance, targetMotif?.GetActiveRoleColors());
            // Play explicitly rather than relying on the prefab's "Play On Awake" setting, so a
            // burst-style particle effect fires reliably no matter how the prefab is authored.
            foreach (var ps in visualInstance.GetComponentsInChildren<ParticleSystem>())
                ps.Play();
            // This coordinator already owns the visual's whole lifecycle (destroyed alongside
            // the rest of the exit once a winner is chosen), so the prefab's own "destroy self
            // once not playing" behavior is never needed here — and calling Stop() on the
            // ParticleSystem for the seal-fade must not let it self-destroy mid-fade.
            foreach (var explosion in visualInstance.GetComponentsInChildren<Explosion>(true))
                explosion.enabled = false;
        }

        var exit = go.AddComponent<MazePathExit>();
        exit.Configure(this, info.AnchorCell, new List<Vector2Int>(info.GapCells), info.Side, phaseIdx, motifIdx, visualInstance, _gameFlow.PathExitSealFadeSeconds);
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

    // The direction from a boundary side's wall toward the maze interior — e.g. a Right exit
    // needs negative X to point inward, a Left exit needs positive X for the same visual intent.
    // Shared by the capture trigger's inward-growing collider and the particle flow direction.
    private static Vector2 InwardDirection(BoundaryWrap.BoundarySide side) => side switch
    {
        BoundaryWrap.BoundarySide.Left  => Vector2.right,
        BoundaryWrap.BoundarySide.Right => Vector2.left,
        BoundaryWrap.BoundarySide.Top   => Vector2.down,
        _ /* Bottom */                  => Vector2.up,
    };

    // The axis a vehicle straightens along before being carried out through a Left/Right/Top/Bottom
    // exit — perpendicular to InwardDirection/OutwardDirection. A Left or Right wall's opening
    // runs vertically, so the vehicle aligns its world Y to the exit anchor's; a Top wall's
    // opening runs horizontally (Top or Bottom), so it aligns X instead.
    private static Vector2 TangentialAxis(BoundaryWrap.BoundarySide side) =>
        side == BoundaryWrap.BoundarySide.Left || side == BoundaryWrap.BoundarySide.Right
            ? Vector2.up
            : Vector2.right;

    // Points the particle effect's Velocity Over Lifetime module inward from whichever border
    // side the exit sits on — toward wherever the player currently is — instead of a single
    // baked-in direction that only reads correctly for one side (e.g. a Right exit needs
    // negative X to flow inward; a Left exit needs positive X for the same visual intent).
    private void ConfigureExitParticleFlow(GameObject visualInstance, BoundaryWrap.BoundarySide side)
    {
        if (visualInstance == null) return;

        Vector2 inward = InwardDirection(side);
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

    // Replaces the prefab's authored rainbow with a gradient built from the target motif's
    // active role colors, so a path-choice exit hints at what's behind it. Falls back to the
    // prefab's own rainbow (no-op) when roleColors is null/empty — a peek miss or a motif with
    // no active roles shouldn't leave the exit visually blank. Alpha keys are read off the
    // existing gradient first and kept as-is, so only hue/color content changes — the authored
    // fade-in/fade-out timing survives untouched.
    private static void ConfigureExitParticleRoleColors(GameObject visualInstance, List<Color> roleColors)
    {
        if (visualInstance == null || roleColors == null || roleColors.Count == 0) return;

        var colorKeys = BuildEvenlySpacedColorKeys(roleColors);

        foreach (var ps in visualInstance.GetComponentsInChildren<ParticleSystem>(true))
        {
            var col = ps.colorOverLifetime;
            if (col.enabled)
            {
                var grad = new Gradient();
                grad.SetKeys(colorKeys, ExtractAlphaKeys(col.color));
                col.color = new ParticleSystem.MinMaxGradient(grad);
            }

            var trails = ps.trails;
            if (trails.enabled)
            {
                var tGrad = new Gradient();
                tGrad.SetKeys(colorKeys, ExtractAlphaKeys(trails.colorOverLifetime));
                trails.colorOverLifetime = new ParticleSystem.MinMaxGradient(tGrad);
            }
        }
    }

    private static GradientAlphaKey[] ExtractAlphaKeys(ParticleSystem.MinMaxGradient g)
    {
        switch (g.mode)
        {
            case ParticleSystemGradientMode.TwoGradients: return g.gradientMax.alphaKeys;
            case ParticleSystemGradientMode.Gradient:
            case ParticleSystemGradientMode.RandomColor:  return g.gradient.alphaKeys;
            default: return new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) };
        }
    }

    private static GradientColorKey[] BuildEvenlySpacedColorKeys(List<Color> colors)
    {
        if (colors.Count == 1)
            return new[] { new GradientColorKey(colors[0], 0f), new GradientColorKey(colors[0], 1f) };

        var keys = new GradientColorKey[colors.Count];
        for (int i = 0; i < colors.Count; i++)
            keys[i] = new GradientColorKey(colors[i], i / (float)(colors.Count - 1));
        return keys;
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
        Vector2 tangentialAxis = TangentialAxis(side);
        float offscreenMargin = _gameFlow.PathTransitionOffscreenMargin;

        // Two stages, driven in the same loop: every vehicle (winner included) first aligns
        // itself to the winner exit's tangential coordinate (straightens onto the corridor's
        // center line — vertically for a Left/Right exit, horizontally for a Top exit) without
        // yet moving toward the wall, then gets carried straight outward through the boundary
        // until it clears the true screen edge — so it reads as being drawn into the center of
        // the particle aisle rather than cutting a diagonal line toward a point. The winner isn't
        // skipped straight to the carry-out stage: the capture trigger is deliberately wider than
        // the corridor's visual opening (a forgiving capture zone, and now several cells deep —
        // see PathExitCaptureDepthCells — so capture fires well before the vehicle is near the
        // edge), so the winner may have touched it off-center or from an angle and still needs to
        // straighten out first. All vehicles get input locked, including the winner, so the
        // pull-in reads as a scripted exit rather than fighting player input.
        var aligning = new List<Vehicle>();
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

            // Capture now fires several cells before the wall (PathExitCaptureDepthCells), so the
            // winner is no longer necessarily right up against the solid boundary — but going
            // non-solid immediately still only helps: it lets the vehicle glide freely through
            // corridor-side wall cells while straightening out, instead of waiting for "arrival"
            // like losers converging in from elsewhere in the maze.
            if (v == winnerVehicle)
                SceneFlowCoordinator.SetVehicleSolid(v, false);

            aligning.Add(v);
        }

        float timeout = _gameFlow.PathTransitionTravelTimeout;
        const float arriveTangentialOffset = 0.75f;
        float elapsed = 0f;
        while (elapsed < timeout && (aligning.Count > 0 || carrying.Count > 0))
        {
            for (int i = aligning.Count - 1; i >= 0; i--)
            {
                var v = aligning[i];
                if (v == null) { aligning.RemoveAt(i); continue; }

                float tangentialOffset = Vector2.Dot(targetWorld - (Vector2)v.transform.position, tangentialAxis);
                if (Mathf.Abs(tangentialOffset) <= arriveTangentialOffset)
                {
                    aligning.RemoveAt(i);
                    carrying.Add(v);
                    SceneFlowCoordinator.SetVehicleSolid(v, false);
                    continue;
                }
                v.Move(tangentialAxis * Mathf.Sign(tangentialOffset));
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

        // Losing exits' particle visuals are still fading (SetSealed kicked off a coroutine on
        // each MazePathExit) — top up to the fade's full duration before hard-destroying the exit
        // GameObjects below, so a fast carry-out (e.g. no losers, or one already close to the
        // winner) can't cut a losing exit's dissolve short.
        float remainingFade = _gameFlow.PathExitSealFadeSeconds - elapsed;
        Debug.Log($"[PathExitFade] RedirectLosersAndAdvance: carry-out took {elapsed:F2}s, topping up {Mathf.Max(0f, remainingFade):F2}s before destroying {_allExits.Count} exit(s)");
        if (remainingFade > 0f)
            yield return new WaitForSeconds(remainingFade);

        int chosenPhase = winnerExit.TargetPhaseIndex;
        int chosenMotif = winnerExit.TargetMotifIndex;
        var mirroredEdge = SceneFlowCoordinator.MirrorSide(winnerExit.Side);
        var exitAnchorCell = winnerExit.AnchorCell;

        foreach (var exit in _allExits)
            if (exit != null) Object.Destroy(exit.gameObject);
        _allExits.Clear();
        _winnerExit = null;
        _winnerVehicle = null;

        yield return _gameFlow.StartCoroutine(_sceneFlow.StartChosenMotifOrPhase(chosenPhase, chosenMotif, mirroredEdge, exitAnchorCell));
    }
}
