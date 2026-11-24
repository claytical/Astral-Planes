using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// =========================================================================
//  Record recovery (co-op / solo).
//
//  Maze generation (OnRecordVaultBuilt): before the maze is laid out, the generator plans a
//  key-shaped vault of Solid dust against the field's edge (CosmicDustGenerator.RecordDock.cs): a
//  ring of walls around a chamber, and a walled channel plugged by one gate per ring the motif
//  needs. The vault grows first and the record docks in its chamber (a little smaller than the
//  chamber, so it reads as trapped in the dust) with its placeholder rings; the maze then grows
//  in around them.
//
//  Motif start (IntroduceRecord, once the maze exists): the camera spotlights the record
//  (CameraZoomDirector: anchored zoom, so the record grows in place; everything but the record and
//  the note tethers fades out), holds while the innermost placeholder pulses, and pulls back.
//
//  Each recorded ring (FocusRecordForRing, from BinRingController.OnBinFilled): the camera
//  spotlights the record while the ring's notes land on it; as it pulls back, the outermost
//  standing gate takes that ring's colour and dissolves.
//
//  Motif complete (OnMotifHarvestComplete, from StarPool's bridge gate): RecordReady. Spotlight,
//  the final ring lands, pull back, any gates still standing open in a cascade, and the record is
//  collectable: a ship that drives down the channel to it starts the ordinary motif bridge, and
//  the vault's walls dissolve.
//
//  The camera zoom is presentation only; gameplay reads GameplayFrame.
//
//  Competitive modes skip all of this: the intro fades and the motif bridges immediately, as
//  before — those players compete over notes and stars, not over reaching the next motif.
//  If the record can't dock, the intro fades and completion falls back to revealing the record
//  and flying it to a dock spot (or bridging immediately), so a motif can never stall here.
// =========================================================================
public partial class GameFlowManager
{
    /// <summary>Fired when the motif is musically complete and its record is being readied for collection.</summary>
    public event System.Action OnRecordReady;

    /// <summary>True from the bridge gate passing until a ship collects the record (or the run resets).</summary>
    public bool RecordReady { get; private set; }

    /// <summary>True once the record can actually be collected (gates open, final ring landed).</summary>
    public bool RecordCollectable { get; private set; }

    private Coroutine _recordReadyRoutine;
    private Coroutine _ringFocusRoutine;
    private int       _ringFocusId;
    private float     _dockRadiusWorld;   // the chamber's radius (zoom framing, reaching the record)
    private Vector3   _dockWorldPos;
    private int       _recordRingsTotal;    // rings the motif needs (nodesPerStar)
    private int       _recordRingsRecorded; // rings recorded so far this motif
    private readonly List<Color> _pendingGateColors = new List<Color>();

    [Header("Record Recovery")]
    [Tooltip("Radius, in grid cells, of the chamber the docked record sits in (its walls are one cell outside it).")]
    [SerializeField] private int recordDockRadiusCells = 2;
    [Tooltip("The vault must be at least this many cells from every ship.")]
    [SerializeField] private int recordDockMinShipDistanceCells = 5;
    [Tooltip("Most gate cells the vault's channel may have. Motifs needing more rings share gates between rings.")]
    [SerializeField] private int recordMaxGates = 12;
    [Tooltip("Colour of a gate before a ring is recorded (no role colour until then).")]
    [SerializeField] private Color recordGateTint = new Color(0.62f, 0.62f, 0.66f, 1f);
    [Tooltip("Colour the remaining gates flash as they open when the motif completes.")]
    [SerializeField] private Color recordFinalGateTint = Color.white;
    [Tooltip("The record's radius as a fraction of its chamber's, so it sits inside the dust rather than against it.")]
    [SerializeField, Range(0.4f, 1f)] private float recordSizeInChamber = 0.8f;

    [Header("Record Camera")]
    [Tooltip("Zoomed-in framing: half the view height, as a multiple of the record's radius.")]
    [SerializeField] private float recordZoomFraming = 2.6f;
    [Tooltip("Seconds the camera takes to settle on the record before the intro hold starts.")]
    [SerializeField] private float recordZoomSettleSeconds = 0.9f;
    [Tooltip("Seconds the intro stays zoomed in on the record (after settling).")]
    [SerializeField] private float recordIntroHoldSeconds = 1.6f;
    [Tooltip("Seconds the camera stays on the record after a ring's notes have all landed.")]
    [SerializeField] private float recordRingHoldSeconds = 0.6f;
    [Tooltip("Longest a ring-completion zoom may last, in drum loops (notes land in time with playback).")]
    [SerializeField] private float recordRingMaxLoops = 1.25f;

    private bool CanUseRecordRecovery => SessionState == null || !SessionState.IsCompetitive;

    // ── Motif start ──────────────────────────────────────────────────────────

    /// <summary>
    /// Motif start, after the maze exists. Co-op/solo: dock the record in its vault, show its
    /// placeholders there, spotlight it and pull back. Competitive (or no vault spot): show the record
    /// full-size, hold, and fade it out as before. Raises IntroRecordHidden once the intro is over.
    /// </summary>
    public IEnumerator IntroduceRecord(float? fadeDuration = null, bool alreadyShown = false)
    {
        var binRings = GetBinRingController();
        var rings    = GetMotifRingGlyphApplicator();

        bool canDock = CanUseRecordRecovery && rings != null && binRings != null
                       && dustGenerator != null && activeDrumTrack != null;

        // Normally the record already sits in its vault, built with the maze (OnRecordVaultBuilt).
        bool docked = canDock && dustGenerator.HasRecordVault && rings.IsDocked;
        Vector3 dockPos = _dockWorldPos;
        if (canDock && !docked)
        {
            // Fallback: the maze had no room for it up front; carve a vault into the grown maze.
            _recordRingsTotal    = Mathf.Max(1, phaseTransitionManager?.currentMotif?.nodesPerStar ?? 1);
            _recordRingsRecorded = 0;
            _pendingGateColors.Clear();
            int gates = Mathf.Clamp(_recordRingsTotal, 1, Mathf.Max(1, recordMaxGates));
            if (TryClaimDockSpot(gates, out dockPos, out float chamberRadius))
            {
                rings.PlaceDocked(dockPos, chamberRadius * recordSizeInChamber);
                binRings.ShowInitialRecord();
                docked = true;
            }
        }

        if (docked)
        {
            var zoom = CameraZoomDirector.GetOrCreate();
            int id = HoldOnRecord(zoom, rings, dockPos);
            yield return new WaitForSeconds(recordZoomSettleSeconds + recordIntroHoldSeconds);
            if (zoom != null) zoom.Release(id);
            yield return new WaitForSeconds(recordZoomSettleSeconds);
            TutorialEvents.RaiseIntroRecordHidden();
            yield break;
        }

        // Old flow: full-size record, hold, fade (fade raises IntroRecordHidden itself).
        if (binRings == null) { TutorialEvents.RaiseIntroRecordHidden(); yield break; }
        if (!alreadyShown)
        {
            binRings.ShowInitialRecord();
            yield return new WaitForSeconds(binRings.GetRecordHoldDuration());
        }
        binRings.FadeInitialRecordOut(fadeDuration);
    }

    // Picks the vault spot, clears the chamber (and builds the key with `gates` gates; 0 = a plain
    // clearing), converts to a world position and radius.
    private bool TryClaimDockSpot(int gates, out Vector3 dockPos, out float dockRadiusWorld)
    {
        dockPos = default; dockRadiusWorld = 0f;
        if (!dustGenerator.TryClearRecordDock(recordDockRadiusCells, recordDockMinShipDistanceCells, out var dockCell,
                                              gates, recordGateTint))
            return false;
        dockPos = activeDrumTrack.GridToWorldPosition(dockCell);
        float cellSize = Mathf.Max(0.01f, activeDrumTrack.GetCellWorldSize());
        dockRadiusWorld  = Mathf.Max(1, recordDockRadiusCells) * cellSize;
        _dockRadiusWorld = dockRadiusWorld;
        _dockWorldPos    = dockPos;
        Debug.Log($"[RECORD] Dock at cell {dockCell} (radius {dockRadiusWorld:F2}, gates={gates}).");
        return true;
    }

    /// <summary>
    /// Asked by the dust generator before it lays out a motif maze: whether to build the record's
    /// vault into it, and its size. False in competitive modes or without a record to dock.
    /// </summary>
    public bool TryGetRecordVaultSpec(out int chamberRadiusCells, out int minShipDistanceCells,
                                      out int gates, out Color gateTint)
    {
        chamberRadiusCells   = recordDockRadiusCells;
        minShipDistanceCells = recordDockMinShipDistanceCells;
        gateTint             = recordGateTint;
        _recordRingsTotal    = Mathf.Max(1, phaseTransitionManager?.currentMotif?.nodesPerStar ?? 1);
        gates                = Mathf.Clamp(_recordRingsTotal, 1, Mathf.Max(1, recordMaxGates));
        return CanUseRecordRecovery && GetMotifRingGlyphApplicator() != null && GetBinRingController() != null
               && activeDrumTrack != null;
    }

    /// <summary>
    /// The dust generator built the vault (walls growing, maze about to grow around it): dock the
    /// record in its chamber with its placeholder rings, before the maze appears.
    /// </summary>
    public void OnRecordVaultBuilt(Vector2Int centerCell)
    {
        var rings    = GetMotifRingGlyphApplicator();
        var binRings = GetBinRingController();
        if (rings == null || binRings == null || activeDrumTrack == null) return;

        _recordRingsRecorded = 0;
        _pendingGateColors.Clear();
        _dockWorldPos    = activeDrumTrack.GridToWorldPosition(centerCell);
        _dockRadiusWorld = Mathf.Max(1, recordDockRadiusCells) * Mathf.Max(0.01f, activeDrumTrack.GetCellWorldSize());

        rings.PlaceDocked(_dockWorldPos, _dockRadiusWorld * recordSizeInChamber);
        binRings.ShowInitialRecord();
        Debug.Log($"[RECORD] Record docked in its vault at cell {centerCell} as the maze grows.");
    }

    // Spotlight the record: anchored on it, framed to show all of it, the record above the dim.
    private int HoldOnRecord(CameraZoomDirector zoom, MotifRingGlyphApplicator rings, Vector3 center)
    {
        if (zoom == null) return 0;
        float r = Mathf.Max(0.5f, _dockRadiusWorld) * 1.15f;
        var bounds = new Bounds(new Vector3(center.x, center.y, 0f), new Vector3(2f * r, 2f * r, 1f));
        return zoom.Hold(center, RecordZoomHalfHeight(), bounds, rings != null ? rings.gameObject : null);
    }

    private float RecordZoomHalfHeight() =>
        Mathf.Max(0.5f, _dockRadiusWorld) * Mathf.Max(1.2f, recordZoomFraming);

    // ── Each recorded ring ───────────────────────────────────────────────────

    /// <summary>
    /// A ring was recorded (BinRingController.OnBinFilled): spotlight the docked record while its
    /// notes land, then pull back and open whatever gates were earned meanwhile. Rings recorded
    /// during a spotlight extend it, and their gates open together at the end.
    /// Gates are counted by harvest, not here (<see cref="OnMotifHarvestCounted"/>).
    /// </summary>
    public void FocusRecordForRing(Color ringColor)
    {
        if (!CanUseRecordRecovery || RecordReady) return;
        var rings = GetMotifRingGlyphApplicator();
        if (rings == null || !rings.IsDocked) return;

        if (_ringFocusRoutine == null)
            _ringFocusRoutine = StartCoroutine(RingFocusRoutine(rings));
    }

    /// <summary>
    /// StarPool counted a harvest (a burst that put notes in a loop): the one number the record's
    /// placeholders, its gates and the motif's completion all read. <paramref name="remaining"/> is
    /// the harvest budget left after this one.
    /// </summary>
    public void OnMotifHarvestCounted(InstrumentTrack track, int remaining)
    {
        remaining = Mathf.Max(0, remaining);
        var rings = GetMotifRingGlyphApplicator();
        if (rings != null) rings.SetRemainingProgressRings(remaining);

        if (!CanUseRecordRecovery || RecordReady || rings == null || !rings.IsDocked) return;
        int total = Mathf.Max(1, phaseTransitionManager?.currentMotif?.nodesPerStar ?? _recordRingsTotal);
        QueueGatesForRing(track != null ? track.DisplayColor : recordGateTint, total - remaining, total);
        // Normally the ring's spotlight (FocusRecordForRing) is already running and opens these
        // when it pulls back; a harvest without a new ring gets its own.
        if (_ringFocusRoutine == null)
            _ringFocusRoutine = StartCoroutine(RingFocusRoutine(rings));
    }

    // One gate per harvest when the channel has a gate per harvest; otherwise harvests share gates evenly.
    private void QueueGatesForRing(Color ringColor, int harvested, int total)
    {
        if (dustGenerator == null) return;
        int gates = dustGenerator.RecordGateCount;
        if (gates <= 0) return;
        _recordRingsTotal    = total;
        _recordRingsRecorded = Mathf.Clamp(harvested, 0, total);
        int target = _recordRingsRecorded >= _recordRingsTotal
            ? gates
            : Mathf.FloorToInt((float)_recordRingsRecorded * gates / Mathf.Max(1, _recordRingsTotal));
        int due = target - dustGenerator.RecordGatesOpened - _pendingGateColors.Count;
        for (int i = 0; i < due; i++) _pendingGateColors.Add(ringColor);
    }

    private IEnumerator RingFocusRoutine(MotifRingGlyphApplicator rings)
    {
        var zoom = CameraZoomDirector.GetOrCreate();
        _ringFocusId = HoldOnRecord(zoom, rings, rings.DockWorldPosition);

        yield return null; // the ring's deformation registers this frame
        float loop    = activeDrumTrack != null ? activeDrumTrack.GetLoopLengthInSeconds() : 2f;
        float maxWait = Mathf.Max(recordZoomSettleSeconds, loop * Mathf.Max(0.25f, recordRingMaxLoops));
        float waited  = 0f;
        // At least long enough to arrive; then until every recorded note has landed.
        while (rings != null && waited < maxWait
               && (waited < recordZoomSettleSeconds || rings.PendingDeformationCount > 0))
        {
            waited += Time.deltaTime;
            yield return null;
        }
        yield return new WaitForSeconds(recordRingHoldSeconds);

        if (zoom != null) zoom.Release(_ringFocusId);
        _ringFocusId = 0;

        // Back in the full view, the gate(s) earned by this ring open in its colour.
        yield return new WaitForSeconds(recordZoomSettleSeconds);
        OpenPendingGates();
        _ringFocusRoutine = null;
    }

    private void OpenPendingGates()
    {
        if (dustGenerator != null)
            foreach (var c in _pendingGateColors)
                dustGenerator.OpenNextRecordGate(c);
        _pendingGateColors.Clear();
    }

    // ── Motif complete ───────────────────────────────────────────────────────

    /// <summary>
    /// Called by StarPool when the bridge gate passes. Co-op and solo: ready the record and wait
    /// for a ship to collect it. Competitive: bridge immediately.
    /// </summary>
    public void OnMotifHarvestComplete(string who)
    {
        if (GhostCycleInProgress || BridgePending || RecordReady) return;

        if (!CanUseRecordRecovery)
        {
            BeginMotifBridge(who);
            return;
        }

        var rings = GetMotifRingGlyphApplicator();
        if (rings == null || !rings.HasGameplayRings || dustGenerator == null || activeDrumTrack == null)
        {
            Debug.LogWarning($"[RECORD] Can't ready the record (rings={rings != null}, hasRings={rings != null && rings.HasGameplayRings}, " +
                             $"dust={dustGenerator != null}, drums={activeDrumTrack != null}); bridging immediately.");
            BeginMotifBridge(who);
            return;
        }

        RecordReady       = true;
        RecordCollectable = false;

        // The route to the record must be open: role walls stop here (CosmicBlockField also
        // refuses new walls while RecordReady).
        dustGenerator.BlockField?.ReleaseAll();
        // The motif is done: the field stops growing. Pending regrowth is dropped, half-grown
        // cells fade out, and nothing regrows behind the ships on the way to the record. The
        // next motif's start resumes it (SceneFlowCoordinator → ResumeRegrowthAfterBridge).
        dustGenerator.HardStopRegrowthForBridge(hideTransientDust: true);

        OnRecordReady?.Invoke();
        _recordReadyRoutine = StartCoroutine(RecordReadyRoutine(rings, who));
    }

    private IEnumerator RecordReadyRoutine(MotifRingGlyphApplicator rings, string who)
    {
        if (rings.IsDocked)
        {
            Debug.Log("[RECORD] Motif complete — readying the record in its vault.");

            // A ring spotlight may still be running: let it finish (its gates open with it).
            while (_ringFocusRoutine != null) yield return null;

            var zoom = CameraZoomDirector.GetOrCreate();
            int id = HoldOnRecord(zoom, rings, rings.DockWorldPosition);

            // Final ring lands (placeholders drop) under the spotlight…
            yield return rings.DockForCollection(default, 0f);
            yield return new WaitForSeconds(recordRingHoldSeconds);
            if (zoom != null) zoom.Release(id);

            // …then, back in the full view, every gate still standing opens.
            yield return new WaitForSeconds(recordZoomSettleSeconds);
            OpenPendingGates();
            float cascade = dustGenerator.OpenAllRecordGates(recordFinalGateTint);
            yield return new WaitForSeconds(cascade);

            if (!RecordReady) { _recordReadyRoutine = null; yield break; }
            RecordCollectable = true;
            Debug.Log("[RECORD] Record ready to collect.");

            // Reaching the record collects it, even if the ship stops just short of the ring itself.
            yield return WatchForShipAtRecord(rings);
            _recordReadyRoutine = null;
            yield break;
        }

        // Fallback: never docked — reveal the finished record and fly it to a (new, plain) dock.
        if (!TryClaimDockSpot(0, out var dockPos, out float dockRadiusWorld))
        {
            Debug.LogWarning("[RECORD] No dock spot found; bridging immediately.");
            RecordReady = false;
            _recordReadyRoutine = null;
            BeginMotifBridge(who);
            yield break;
        }

        // Nested (not StartCoroutine) so ResetRecordRecovery's StopCoroutine also stops the dock animation.
        yield return rings.DockForCollection(dockPos, dockRadiusWorld * recordSizeInChamber);

        _recordReadyRoutine = null;
        if (!RecordReady) yield break; // reset while docking
        RecordCollectable = true;
        Debug.Log("[RECORD] Record ready to collect.");
    }

    // A ship's hull within a cell of the record's edge: it is in the chamber or the channel's inner
    // end (the walls keep any ship outside the vault further away than that).
    private IEnumerator WatchForShipAtRecord(MotifRingGlyphApplicator rings)
    {
        float cell = activeDrumTrack != null ? Mathf.Max(0.01f, activeDrumTrack.GetCellWorldSize()) : 0.5f;
        while (RecordReady && RecordCollectable && rings != null)
        {
            Vector2 c = rings.DockWorldPosition;
            var vehicles = GetVehicles();
            if (vehicles != null)
                foreach (var v in vehicles)
                {
                    if (v == null || !v.isActiveAndEnabled) continue;
                    float gap = Vector2.Distance(v.transform.position, c) - v.GetColliderBoundingRadiusWorld();
                    if (gap <= _dockRadiusWorld + cell)
                    {
                        TryCollectRecord(v);
                        yield break;
                    }
                }
            yield return null;
        }
    }

    /// <summary>A ship reached the record. Starts the motif bridge if the record is collectable.</summary>
    public bool TryCollectRecord(Vehicle vehicle)
    {
        if (!RecordReady || !RecordCollectable) return false;
        RecordReady       = false;
        RecordCollectable = false;
        Debug.Log($"[RECORD] Record collected by {(vehicle != null ? vehicle.name : "?")} — starting bridge.");
        BeginMotifBridge("RecordCollected");
        return true;
    }

    /// <summary>Drops any pending record-recovery state, the vault and the camera zoom (bridge, game over, new session).</summary>
    public void ResetRecordRecovery()
    {
        if (_recordReadyRoutine != null) { StopCoroutine(_recordReadyRoutine); _recordReadyRoutine = null; }
        if (_ringFocusRoutine != null)   { StopCoroutine(_ringFocusRoutine);   _ringFocusRoutine = null; }
        _ringFocusId         = 0;
        _recordRingsRecorded = 0;
        _pendingGateColors.Clear();
        RecordReady       = false;
        RecordCollectable = false;
        if (dustGenerator != null && dustGenerator.IsRecordVaultSealed) dustGenerator.OpenRecordVault();
        var cam = Camera.main;
        var zoom = cam != null ? cam.GetComponent<CameraZoomDirector>() : null;
        if (zoom != null) zoom.ReleaseAll();
    }
}
