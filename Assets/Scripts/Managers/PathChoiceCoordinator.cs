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
    private HashSet<(string phaseId, string motifId)> _playedKeys = new();

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
        // The bottom exit sits right on the NoteViz "Line of Ascent"; nothing ascends during the
        // interstitial, so hide it for the duration and always restore it (idempotent).
        var ascension = Object.FindFirstObjectByType<NoteAscensionDirector>();
        ascension?.SetAscentLineVisible(false);
        yield return _gameFlow.StartCoroutine(RunPathChoiceAndAdvanceInner());
        ascension?.SetAscentLineVisible(true);
    }

    private IEnumerator RunPathChoiceAndAdvanceInner()
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

        _playedKeys = new HashSet<(string, string)>();
        foreach (var r in RingSessionStore.LoadAllRingsFromDisk())
            _playedKeys.Add((r.PhaseId, r.MotifId));

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
            _allExits.Add(CreateExitTrigger(drums, exitInfos[infoIdx], target.Phase, target.Motif, ClassifyExit(target)));
        }

        SpawnWisp(drums, vehicleCells, ptm);

        var exitAnchors = new List<Vector2Int>(_allExits.Count);
        foreach (var e in _allExits) exitAnchors.Add(e.AnchorCell);
        _laneRadiusCells = Mathf.Max(1, Mathf.CeilToInt(widestVehicleRadiusWorld / cellWorld));
        dust.SetTravelCorridors(vehicleCells, exitAnchors, radiusCells: _laneRadiusCells);

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

        exit.BeginSwallow();
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

    private MazePathExit CreateExitTrigger(DrumTrack drums, CosmicDustGenerator.PathExitInfo info, int phaseIdx, int motifIdx, PathExitKind kind)
    {
        var geo = BuildGeometry(drums, info);

        var go = new GameObject($"PathExit_{info.Side}_{info.AnchorCell.x}_{info.AnchorCell.y}");
        go.transform.position = geo.Center;

        // Trigger, particle lane and carry-out line all come from `geo`: the trigger covers the
        // full carved opening (not just the span between the two gap-cell centers) and reaches
        // CaptureDepth inward from the wall face, exactly the rectangle the particles fill.
        var col = go.AddComponent<BoxCollider2D>();
        col.isTrigger = true;
        col.size = geo.HorizontalWall
            ? new Vector2(geo.OpeningWidth, geo.CaptureDepth)
            : new Vector2(geo.CaptureDepth, geo.OpeningWidth);
        col.offset = geo.Inward * (geo.CaptureDepth * 0.5f);

        var targetMotif = _gameFlow.phaseTransitionManager.PeekMotif(phaseIdx, motifIdx);
        var visualInstance = PathExitParticleBuilder.Build(
            geo, kind, targetMotif?.GetActiveRoleColors(), IsPlayed(phaseIdx, motifIdx),
            _gameFlow.PathExitParticleStyle, go.transform);

        Debug.Log($"[PathExit] {info.Side} -> phase {phaseIdx} motif {motifIdx} ('{targetMotif?.name}') kind={kind}");

        var exit = go.AddComponent<MazePathExit>();
        exit.Configure(this, info.AnchorCell, new List<Vector2Int>(info.GapCells), info.Side, phaseIdx, motifIdx,
            geo, visualInstance, _gameFlow.PathExitSealFadeSeconds, kind);
        return exit;
    }

    // One geometry for the whole exit. Center is the midpoint of the gap cells along the wall
    // (NOT info.AnchorCell, which is just group[0]) snapped onto the physical wall face; the
    // opening width is the gap span widened by the carve radius, i.e. what is actually passable.
    private PathExitGeometry BuildGeometry(DrumTrack drums, CosmicDustGenerator.PathExitInfo info)
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

        bool horizontalWall = info.Side == BoundaryWrap.BoundarySide.Top || info.Side == BoundaryWrap.BoundarySide.Bottom;
        int n = info.GapCells.Count;

        // Real tangential cell pitch, measured between gap cells (the grid's x/y tiles differ
        // slightly, so GetCellWorldSize() alone would be a few percent off along the wall).
        float pitch = cellSize;
        if (n > 1)
        {
            float span = horizontalWall ? max.x - min.x : max.y - min.y;
            pitch = Mathf.Max(cellSize * 0.5f, span / (n - 1));
        }

        int passRadius = Mathf.Max(1, info.PassRadiusCells);
        float opening = (n + 2 * passRadius) * pitch;
        float depth = Mathf.Max(cellSize, _gameFlow.PathExitCaptureDepthCells * cellSize);
        return PathExitGeometryFactory.Create(info.Side, (min + max) * 0.5f, opening, depth);
    }

    // ── Exit kind (revisit / new motif / new phase) ───────────────────────────

    private bool IsPlayed(int phaseIdx, int motifIdx)
    {
        var ptm = _gameFlow.phaseTransitionManager;
        var motif = ptm.PeekMotif(phaseIdx, motifIdx);
        var phaseId = ptm.PeekPhaseId(phaseIdx);
        return motif != null && phaseId != null && _playedKeys.Contains((phaseId, motif.motifId));
    }

    private PathExitKind ClassifyExit(MotifMazeGraph.Exit exit)
    {
        var ptm = _gameFlow.phaseTransitionManager;
        if (exit.Phase != ptm.CurrentPhaseIndex || exit.Dir == MazeArm.Up || exit.Dir == MazeArm.Down)
            return PathExitKind.NewPhase;
        return IsPlayed(exit.Phase, exit.Motif) ? PathExitKind.Revisit : PathExitKind.NewMotif;
    }

    // Travel-lane radius sized to the widest active hull; reused when redirecting losers.
    private int _laneRadiusCells = 1;

    private IEnumerator RedirectLosersAndAdvance(List<Vector2Int> vehicleCells, CosmicDustGenerator dust, DrumTrack drums)
    {
        var winnerExit = _winnerExit;
        var winnerVehicle = _winnerVehicle;

        // Releases every losing lane via the same diff-on-recompute logic SetTravelCorridors
        // already uses (cells that fall out of the new set get released and regrow requested).
        dust.SetTravelCorridors(vehicleCells, winnerExit.AnchorCell, radiusCells: Mathf.Max(1, _laneRadiusCells));

        foreach (var exit in _allExits)
        {
            if (exit == winnerExit || exit == null) continue;
            dust.SpawnDustAtCells(exit.GapCells, MusicalRole.None, dust.SolidDustColor(), energy01: 1f, growInSeconds: 0.5f, solid: true);
        }

        var geo = winnerExit.Geometry;
        var side = winnerExit.Side;
        Vector2 outward = geo.Outward;
        Vector2 tangentialAxis = geo.Tangent;
        float offscreenMargin = _gameFlow.PathTransitionOffscreenMargin;

        // Two stages, driven in the same loop. Every vehicle (winner included) first aligns to
        // the lit lane's center line (geo.Center — the SAME point the trigger and particles are
        // built around, not the first gap cell), then is carried outward through the wall until
        // it clears the true screen edge. During the carry the lateral error keeps being
        // corrected (proportional on offset, damped on lateral velocity), so momentum or a
        // slightly off-center hand-off can't bend the exit line away from the particles.
        // The winner isn't skipped straight to the carry stage: it may have touched the (now
        // opening-wide) trigger off-center or from an angle. All vehicles get input locked so the
        // pull-in reads as a scripted exit rather than fighting player input.
        var aligning = new List<Vehicle>();
        var carrying = new List<Vehicle>();
        foreach (var v in _gameFlow.GetVehicles())
        {
            if (v == null) continue;

            foreach (var lp in _session.Players)
            {
                if (lp != null && lp.plane == v)
                {
                    lp.SetInputLocked(true);
                    break;
                }
            }

            // Going non-solid immediately only helps the winner: it glides freely through
            // corridor-side wall cells while straightening, instead of waiting for "arrival"
            // like losers converging in from elsewhere in the maze.
            if (v == winnerVehicle)
                SceneFlowCoordinator.SetVehicleSolid(v, false);

            aligning.Add(v);
        }

        float timeout = _gameFlow.PathTransitionTravelTimeout;
        // Quarter of the opening each side of center — tight enough that the carry starts
        // visibly inside the lit lane, loose enough that bang-bang alignment settles quickly.
        float arriveTangentialOffset = Mathf.Max(0.12f, geo.OpeningWidth * 0.25f);
        const float lateralGain = 3f;
        const float lateralDamping = 0.15f;
        float elapsed = 0f;
        while (elapsed < timeout && (aligning.Count > 0 || carrying.Count > 0))
        {
            for (int i = aligning.Count - 1; i >= 0; i--)
            {
                var v = aligning[i];
                if (v == null) { aligning.RemoveAt(i); continue; }

                float tangentialOffset = geo.OffsetToCenter(v.transform.position);
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

                // Shrink into the mass as the vehicle nears the hole's center, so it reads as
                // being swallowed rather than just driving off-screen.
                float massR = Mathf.Max(0.1f, winnerExit.PortalRadius);
                float distToHole = Vector2.Distance(v.transform.position, geo.VisualCenter);
                float closeness = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(massR * 1.3f, massR * 0.2f, distToHole));
                VehicleSwallowScale.Apply(v, Mathf.Lerp(1f, 0.15f, closeness));

                float offset = geo.OffsetToCenter(v.transform.position);
                float lateralVel = v.rb != null ? Vector2.Dot(v.rb.linearVelocity, tangentialAxis) : 0f;
                float lateral = Mathf.Clamp(offset * lateralGain - lateralVel * lateralDamping, -1.5f, 1.5f);
                v.Move((outward + tangentialAxis * lateral).normalized);
            }

            elapsed += Time.deltaTime;
            yield return null;
        }

        // Timeout backstop: never leave a vehicle on screen beside the lit lane. Anything still
        // aligning/carrying is placed just off-screen along the lane's center line.
        foreach (var v in aligning) SnapOffscreenAlongLane(v, geo, offscreenMargin);
        foreach (var v in carrying) SnapOffscreenAlongLane(v, geo, offscreenMargin);

        // Anyone the timeout backstop teleported is still full-size/visible-scaled: hide it
        // inside the hole's scale too. (Input stays locked — staging for the next motif owns the unlock.)
        foreach (var v in _gameFlow.GetVehicles())
            if (v != null) VehicleSwallowScale.Apply(v, 0.15f);

        // The vehicles are gone: let the swallow land for a beat, then close the winning hole
        // and dissolve the old maze TOGETHER, so nothing on screen pops out in a single frame.
        // The hole is only destroyed after its close has fully played.
        float closeSeconds = _gameFlow.PathExitCloseSeconds;
        float dissolveSeconds = _gameFlow.PathExitDissolveSeconds;
        float beat = _gameFlow.PathExitPostSwallowBeatSeconds;
        if (beat > 0f) yield return new WaitForSeconds(beat);

        winnerExit.BeginClose(closeSeconds);
        dust.DissolveAllVisuals(dissolveSeconds);
        Debug.Log($"[PathExitFade] carry-out took {elapsed:F2}s; closing winner over {closeSeconds:F2}s, dissolving maze over {dissolveSeconds:F2}s");
        yield return new WaitForSeconds(Mathf.Max(closeSeconds, dissolveSeconds, _gameFlow.PathExitSealFadeSeconds));

        int chosenPhase = winnerExit.TargetPhaseIndex;
        int chosenMotif = winnerExit.TargetMotifIndex;
        var chosenKind = winnerExit.Kind;
        var mirroredEdge = SceneFlowCoordinator.MirrorSide(winnerExit.Side);
        var exitAnchorCell = winnerExit.AnchorCell;

        foreach (var exit in _allExits)
            if (exit != null) Object.Destroy(exit.gameObject);
        _allExits.Clear();
        _winnerExit = null;
        _winnerVehicle = null;

        // Bring the Line of Ascent back before the next motif starts (the wrapper also restores it).
        Object.FindFirstObjectByType<NoteAscensionDirector>()?.SetAscentLineVisible(true);

        yield return _gameFlow.StartCoroutine(_sceneFlow.StartChosenMotifOrPhase(chosenPhase, chosenMotif, mirroredEdge, exitAnchorCell, chosenKind));
    }

    private static void SnapOffscreenAlongLane(Vehicle v, PathExitGeometry geo, float margin)
    {
        if (v == null) return;
        Debug.LogWarning($"[PathExit] {v.name} didn't clear {geo.Side} exit in time; snapping off-screen along the lane.");

        Vector2 pos = v.transform.position;
        pos += geo.Tangent * geo.OffsetToCenter(pos);   // onto the lane center line
        pos = SceneFlowCoordinator.OffscreenPointAlong(pos, geo.Side, margin);
        SceneFlowCoordinator.SetVehicleSolid(v, false);
        if (v.rb != null)
        {
            v.rb.position = pos;
            v.rb.linearVelocity = Vector2.zero;
        }
        else
        {
            v.transform.position = pos;
        }
    }
}
