using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// =========================================================================
//  Record recovery (co-op / solo).
//
//  Maze generation (OnRecordVaultBuilt): before the maze is laid out, the generator plans a
//  sealed vault against the field's edge (CosmicDustGenerator.RecordDock.cs): a chamber ringed by
//  a shell of bright Cosmic Block dust, ceil(nodesPerStar / 4) cells deep (1..4). There is no way
//  in. The vault grows first and the record docks in its chamber (smaller than the chamber, so it
//  can drift and lunge at the shell, trapped) with its placeholder rings; the maze then grows in
//  around them. The next-up placeholder lerps between the roles that could fill it right now
//  (RefreshNextUpRingPalette: active roles minus the one walled into Cosmic Blocks).
//
//  Motif start (IntroduceRecord, once the maze exists): the camera spotlights the record
//  (CameraZoomDirector: anchored zoom, so the record grows in place; everything but the record and
//  the note tethers fades out), holds while the innermost placeholder pulses, and pulls back.
//
//  Each recorded ring (FocusRecordForRing, from BinRingController.OnBinFilled): the camera
//  spotlights the record while the ring's notes land on it; as it pulls back, the whole shell
//  takes that ring's role colour, rippling outward.
//
//  Motif complete (OnMotifHarvestComplete, from StarPool's bridge gate): RecordReady. Spotlight,
//  the final ring lands, pull back, the shell explodes outward, and the record is collectable.
//  A ship touching it (TryCollectRecord) gets the ring spotlight again, and this time the record's
//  spin / tilt / scale-out exit plays under it; the camera pulls back once the interstitial maze
//  has grown around the players (ReleaseRecordExitZoom).
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

    /// <summary>True once the record can actually be collected (final ring landed, vault shell burst open).</summary>
    public bool RecordCollectable { get; private set; }

    private Coroutine _recordReadyRoutine;
    private Coroutine _ringFocusRoutine;
    private int       _ringFocusId;
    private float     _dockRadiusWorld;   // the chamber's radius (zoom framing, reaching the record)
    private Vector3   _dockWorldPos;
    private Color     _lastRingColor;       // colour of the most recently recorded ring (shell tint)
    private bool      _hasLastRingColor;
    private Vector2   _lastDockBumpDir;     // where the trapped record last struck its shell (ignites the vault's chain)
    private int       _recordExitZoomId;
    private Coroutine _recordExitRoutine;

    /// <summary>True from a ship collecting the record until the camera pulls back from its exit.</summary>
    public bool RecordExitZoomActive { get; private set; }

    [Header("Record Recovery")]
    [Tooltip("Radius, in grid cells, of the chamber the docked record sits in (its shell starts one cell outside it).")]
    [SerializeField] private int recordDockRadiusCells = 2;
    [Tooltip("The vault must be at least this many cells from every ship.")]
    [SerializeField] private int recordDockMinShipDistanceCells = 5;
    [Tooltip("The record's radius as a fraction of its chamber's; the rest is the room it drifts and lunges in.")]
    [SerializeField, Range(0.4f, 1f)] private float recordSizeInChamber = 0.65f;
    [Tooltip("Longest the camera stays on the collected record after its exit animation, waiting for the interstitial maze.")]
    [SerializeField] private float recordExitMaxHoldSeconds = 8f;

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
    public IEnumerator IntroduceRecord()
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
            ResetDockFeedback();
            if (TryClaimDockSpot(RecordVaultThickness(), out dockPos, out float chamberRadius))
            {
                rings.PlaceDocked(dockPos, chamberRadius * recordSizeInChamber, chamberRadius);
                binRings.ShowInitialRecord();
                BeginTrappedRecord(rings);
                docked = true;
            }
        }

        if (docked && NarratedTutorialController.WillDriveIntroZoom)
        {
            // The narrated tutorial zooms in and out on its own lines (BeginNarrationSpotlight).
            TutorialEvents.RaiseIntroRecordHidden();
            yield break;
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
        binRings.ShowInitialRecord();
        yield return new WaitForSeconds(binRings.GetRecordHoldDuration());
        binRings.FadeInitialRecordOut();
    }

    // Chamber radius in world units (shared by the up-front vault and the fallback dock).
    private float DockRadiusWorld() =>
        Mathf.Max(1, recordDockRadiusCells) * Mathf.Max(0.01f, activeDrumTrack.GetCellWorldSize());

    // Fresh dock: forget the previous record's ring colour and bump direction.
    private void ResetDockFeedback()
    {
        _hasLastRingColor = false;
        _lastDockBumpDir  = Vector2.zero;
    }

    // Shell depth from the motif's length: up to 4 rings → 1 cell, 8 → 2, 12 → 3, 16 → 4.
    private int RecordVaultThickness()
    {
        int rings = Mathf.Max(1, phaseTransitionManager?.currentMotif?.nodesPerStar ?? 1);
        return Mathf.Clamp(Mathf.CeilToInt(rings / 4f), 1, 4);
    }

    // Picks the vault spot, clears the chamber (and grows a shell `thickness` deep; 0 = a plain
    // clearing), converts to a world position and radius.
    private bool TryClaimDockSpot(int thickness, out Vector3 dockPos, out float dockRadiusWorld)
    {
        dockPos = default; dockRadiusWorld = 0f;
        if (!dustGenerator.TryClearRecordDock(recordDockRadiusCells, recordDockMinShipDistanceCells, out var dockCell,
                                              thickness))
            return false;
        dockPos = activeDrumTrack.GridToWorldPosition(dockCell);
        dockRadiusWorld  = DockRadiusWorld();
        _dockRadiusWorld = dockRadiusWorld;
        _dockWorldPos    = dockPos;
        Debug.Log($"[RECORD] Dock at cell {dockCell} (radius {dockRadiusWorld:F2}, thickness={thickness}).");
        return true;
    }

    /// <summary>
    /// Asked by the dust generator before it lays out a motif maze: whether to build the record's
    /// vault into it, and its size. False in competitive modes or without a record to dock.
    /// </summary>
    public bool TryGetRecordVaultSpec(out int chamberRadiusCells, out int minShipDistanceCells,
                                      out int thickness)
    {
        chamberRadiusCells   = recordDockRadiusCells;
        minShipDistanceCells = recordDockMinShipDistanceCells;
        thickness            = RecordVaultThickness();
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

        ResetDockFeedback();
        _dockWorldPos    = activeDrumTrack.GridToWorldPosition(centerCell);
        _dockRadiusWorld = DockRadiusWorld();

        rings.PlaceDocked(_dockWorldPos, _dockRadiusWorld * recordSizeInChamber, _dockRadiusWorld);
        binRings.ShowInitialRecord();
        BeginTrappedRecord(rings);
        Debug.Log($"[RECORD] Record docked in its vault at cell {centerCell} as the maze grows.");
    }

    // The record is sealed in: it drifts and lunges at its shell, and its next-up ring cycles the
    // roles that could fill it.
    private void BeginTrappedRecord(MotifRingGlyphApplicator rings)
    {
        rings.OnDockBump -= HandleRecordDockBump;
        rings.OnDockBump += HandleRecordDockBump;
        rings.StartDockDrift();
        RefreshNextUpRingPalette();
    }

    // The trapped record hit its shell: the shell cells it hit flash.
    private void HandleRecordDockBump(Vector2 dir)
    {
        if (dustGenerator == null || !dustGenerator.IsRecordVaultSealed) return;
        _lastDockBumpDir = dir;
        var shell  = dustGenerator.RecordVaultInnerShell;
        var center = (Vector2)dustGenerator.RecordVaultCenter;
        int flashed = 0;
        foreach (var c in shell)
        {
            var d = (Vector2)c - center;
            if (d.sqrMagnitude < 0.0001f || Vector2.Dot(d.normalized, dir) < 0.85f) continue;
            if (dustGenerator.TryFlashSolidCell(c, dustGenerator.RecordVaultFlashColor, 0.4f) && ++flashed >= 4) break;
        }
    }

    // ── Next-up ring palette ─────────────────────────────────────────────────

    /// <summary>
    /// True when <paramref name="t"/> is one of the motif's active roles and still has an unfilled
    /// bin (shared with CosmicBlockField's deadlock rule).
    /// </summary>
    public static bool TrackCanStillProgress(InstrumentTrack t, List<MusicalRole> activeRoles)
    {
        if (t == null || t.assignedRole == MusicalRole.None) return false;
        if (activeRoles != null && !activeRoles.Contains(t.assignedRole)) return false;
        return t.GetFilledBinCount() < Mathf.Max(1, t.maxLoopMultiplier);
    }

    private readonly List<Color>       _paletteScratch = new List<Color>(4);
    private readonly List<MusicalRole> _paletteRoles   = new List<MusicalRole>(4);

    /// <summary>
    /// The next-up placeholder ring lerps between the colours of the roles that could fill it now:
    /// the motif's active roles that still have an unfilled bin, minus the role walled into Cosmic
    /// Blocks. Called whenever the wall moves and whenever a harvest lands.
    /// </summary>
    public void RefreshNextUpRingPalette()
    {
        var rings = GetMotifRingGlyphApplicator();
        if (rings == null) return;
        var tracks = controller != null ? controller.tracks : null;
        var activeRoles = phaseTransitionManager?.currentMotif?.GetActiveRoles();
        var blocked = dustGenerator != null ? dustGenerator.BlockedRole : MusicalRole.None;

        _paletteScratch.Clear();
        _paletteRoles.Clear();
        if (tracks != null)
            foreach (var t in tracks)
            {
                if (!TrackCanStillProgress(t, activeRoles)) continue;
                if (t.assignedRole == blocked || _paletteRoles.Contains(t.assignedRole)) continue;
                _paletteRoles.Add(t.assignedRole);
                _paletteScratch.Add(t.DisplayColor);
            }
        rings.SetNextUpPalette(_paletteScratch);
        if (VerboseLogging)
            Debug.Log($"[RECORD] Next-up palette: {string.Join(",", _paletteRoles)} (walled={blocked})");
    }

    // Spotlight the record: anchored on it, framed to show all of it, the record above the dim.
    private int HoldOnRecord(CameraZoomDirector zoom, MotifRingGlyphApplicator rings, Vector3 center)
    {
        if (zoom == null) return 0;
        float r = Mathf.Max(0.5f, _dockRadiusWorld) * 1.15f;
        var bounds = new Bounds(new Vector3(center.x, center.y, 0f), new Vector3(2f * r, 2f * r, 1f));
        return zoom.Hold(center, RecordZoomHalfHeight(), bounds, rings != null ? rings.gameObject : null);
    }

    // The collected record's exit: pan it to the middle of the screen and zoom until it fills the
    // view the way the original full-screen record did (RefreshPlayAreaFit: the outer ring spans
    // 1 − 2·fitPaddingFraction of the view height), so it spins, tilts and floats off big and centred.
    private int HoldRecordCentered(CameraZoomDirector zoom, MotifRingGlyphApplicator rings)
    {
        if (zoom == null || rings == null) return 0;
        float pad  = rings.config != null ? Mathf.Clamp(rings.config.fitPaddingFraction, 0f, 0.4f) : 0.05f;
        float r    = Mathf.Max(0.25f, rings.DockWorldRadius);
        float half = r / Mathf.Max(0.2f, 1f - 2f * pad);
        return zoom.HoldCentered(rings.DockWorldPosition, half, rings.gameObject);
    }

    /// <summary>
    /// Narration-driven spotlight on the docked record (the tutorial's "zoom in on this line").
    /// Stacks with the ring spotlight on the same anchor, so it can outlast it. Returns 0 when
    /// there is no docked record to show (competitive, record ready, no vault).
    /// </summary>
    public int BeginNarrationSpotlight()
    {
        if (!CanUseRecordRecovery || RecordReady) return 0;
        var rings = GetMotifRingGlyphApplicator();
        if (rings == null || !rings.IsDocked) return 0;
        return HoldOnRecord(CameraZoomDirector.GetOrCreate(), rings, rings.DockWorldPosition);
    }

    public void EndNarrationSpotlight(int id)
    {
        if (id == 0) return;
        var zoom = CameraZoomDirector.Current;
        if (zoom != null) zoom.Release(id);
    }

    /// <summary>No spotlight held and the camera has eased back to its normal view.</summary>
    public bool IsCameraHome
    {
        get
        {
            var zoom = CameraZoomDirector.Current;
            return zoom == null || (!zoom.IsFocused && zoom.IsSettled);
        }
    }

    /// <summary>The camera has arrived on a held spotlight.</summary>
    public bool IsSpotlightSettled
    {
        get
        {
            var zoom = CameraZoomDirector.Current;
            return zoom == null || (zoom.IsFocused && zoom.IsSettled);
        }
    }

    private float RecordZoomHalfHeight() =>
        Mathf.Max(0.5f, _dockRadiusWorld) * Mathf.Max(1.2f, recordZoomFraming);

    // ── Each recorded ring ───────────────────────────────────────────────────

    /// <summary>
    /// A ring was recorded (BinRingController.OnBinFilled): spotlight the docked record while its
    /// notes land, then pull back and re-tint the vault's shell in the ring's colour. Rings
    /// recorded during a spotlight extend it; the shell takes the latest colour.
    /// </summary>
    public void FocusRecordForRing(Color ringColor)
    {
        if (!CanUseRecordRecovery || RecordReady) return;
        var rings = GetMotifRingGlyphApplicator();
        if (rings == null || !rings.IsDocked) return;

        _lastRingColor    = ringColor;
        _hasLastRingColor = true;
        if (_ringFocusRoutine == null)
            _ringFocusRoutine = StartCoroutine(RingFocusRoutine(rings));
    }

    /// <summary>
    /// StarPool counted a harvest (a burst that put notes in a loop): the one number the record's
    /// placeholders and the motif's completion read. <paramref name="remaining"/> is the harvest
    /// budget left after this one.
    /// </summary>
    public void OnMotifHarvestCounted(InstrumentTrack track, int remaining)
    {
        remaining = Mathf.Max(0, remaining);
        var rings = GetMotifRingGlyphApplicator();
        if (rings != null) rings.SetRemainingProgressRings(remaining);
        RefreshNextUpRingPalette();

        if (!CanUseRecordRecovery || RecordReady || rings == null || !rings.IsDocked) return;
        if (track != null) { _lastRingColor = track.DisplayColor; _hasLastRingColor = true; }
        // Normally the ring's spotlight (FocusRecordForRing) is already running and re-tints the
        // shell when it pulls back; a harvest without a new ring gets its own. The final harvest
        // gets none: the ready sequence plays in the normal view.
        if (remaining > 0 && _ringFocusRoutine == null)
            _ringFocusRoutine = StartCoroutine(RingFocusRoutine(rings));
    }

    private IEnumerator RingFocusRoutine(MotifRingGlyphApplicator rings)
    {
        // Wait a frame before zooming: the final ring's OnBinFilled fires just before StarPool's
        // bridge gate sets RecordReady in the same frame. The final ring gets no zoom — its notes
        // land, the vault recolours and explodes in the normal view (RecordReadyRoutine).
        yield return null; // (also: the ring's deformation registers this frame)
        if (RecordReady || rings == null) { _ringFocusRoutine = null; yield break; }

        // The narrated tutorial zooms on this ring from its own lines; keep the camera still and
        // just let the notes land and the shell re-tint on the usual schedule.
        bool zoomIn = !NarratedTutorialController.ClaimsRingSpotlight;
        var zoom = zoomIn ? CameraZoomDirector.GetOrCreate() : null;
        if (zoom != null) _ringFocusId = HoldOnRecord(zoom, rings, rings.DockWorldPosition);
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

        // Back in the full view, the shell takes the ring's colour.
        if (zoomIn) yield return new WaitForSeconds(recordZoomSettleSeconds);
        if (_hasLastRingColor && dustGenerator != null && !RecordReady)
            dustGenerator.RetintRecordVault(_lastRingColor);
        _ringFocusRoutine = null;
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

        // An earlier ring's spotlight still running: end it so the camera eases home before the
        // vault explodes (the ready sequence plays in the normal view).
        if (_ringFocusRoutine != null) { StopCoroutine(_ringFocusRoutine); _ringFocusRoutine = null; }
        if (_ringFocusId != 0)
        {
            var z = CameraZoomDirector.Current;
            if (z != null) z.Release(_ringFocusId);
            _ringFocusId = 0;
        }

        OnRecordReady?.Invoke();
        _recordReadyRoutine = StartCoroutine(RecordReadyRoutine(rings, who));
    }

    private IEnumerator RecordReadyRoutine(MotifRingGlyphApplicator rings, string who)
    {
        if (rings.IsDocked)
        {
            Debug.Log("[RECORD] Motif complete — readying the record in its vault.");

            // All in the normal view (no zoom): the final ring's notes land…
            yield return rings.DockForCollection(default, 0f);

            // …the shell takes the final ring's colour, like every ring before it…
            Color burst = _hasLastRingColor ? _lastRingColor : dustGenerator.InertBlockColor();
            if (_hasLastRingColor)
            {
                float ripple = dustGenerator.RetintRecordVault(_lastRingColor);
                if (ripple > 0f) yield return new WaitForSeconds(ripple);
            }

            // …and bursts open: a chain reaction that starts where the record last struck the shell.
            float cascade = dustGenerator.ExplodeRecordVault(burst, _lastDockBumpDir);
            yield return new WaitForSeconds(cascade);

            if (!RecordReady) { _recordReadyRoutine = null; yield break; }
            RecordCollectable = true;
            Debug.Log("[RECORD] Record ready to collect.");
            TutorialEvents.RaiseRecordAccessible();

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
        TutorialEvents.RaiseRecordAccessible();
    }

    // A ship's hull within half a cell of the record itself (a grace for ships that stop just short
    // of touching it). Measured from the record's live position (it drifts inside the chamber) to
    // its visible edge — not the chamber's edge: with a one-cell shell, a ship merely resting
    // against the vault's outside was already "at the record" the moment the shell exploded.
    private IEnumerator WatchForShipAtRecord(MotifRingGlyphApplicator rings)
    {
        float cell = activeDrumTrack != null ? Mathf.Max(0.01f, activeDrumTrack.GetCellWorldSize()) : 0.5f;
        while (RecordReady && RecordCollectable && rings != null)
        {
            Vector2 c = rings.transform.position;
            float reach = rings.DockWorldRadius + cell * 0.5f;
            var vehicles = GetVehicles();
            if (vehicles != null)
                foreach (var v in vehicles)
                {
                    if (v == null || !v.isActiveAndEnabled) continue;
                    float gap = Vector2.Distance(v.transform.position, c) - v.GetColliderBoundingRadiusWorld();
                    if (gap <= reach)
                    {
                        TryCollectRecord(v);
                        yield break;
                    }
                }
            yield return null;
        }
    }

    /// <summary>
    /// A ship reached the record. If it is collectable: the camera zooms in on it as it does for a
    /// ring, and once settled the motif bridge starts; the record's exit plays under the spotlight,
    /// which lifts once the interstitial maze has grown (<see cref="ReleaseRecordExitZoom"/>).
    /// </summary>
    public bool TryCollectRecord(Vehicle vehicle)
    {
        if (!RecordReady || !RecordCollectable || RecordExitZoomActive) return false;
        RecordCollectable = false; // RecordReady stays up (no walls, no regrowth) until the bridge starts
        Debug.Log($"[RECORD] Record collected by {(vehicle != null ? vehicle.name : "?")} — zooming in, then bridging.");
        TutorialEvents.RaiseRecordCollected();

        var rings = GetMotifRingGlyphApplicator();
        var zoom  = CameraZoomDirector.GetOrCreate();
        RecordExitZoomActive = true;
        _recordExitZoomId    = rings != null ? HoldRecordCentered(zoom, rings) : 0;
        if (_recordExitRoutine != null) StopCoroutine(_recordExitRoutine);
        _recordExitRoutine = StartCoroutine(RecordExitRoutine(rings));
        return true;
    }

    private IEnumerator RecordExitRoutine(MotifRingGlyphApplicator rings)
    {
        // Start the spin as soon as the pan and zoom have landed (the record big and centred).
        var zoom = CameraZoomDirector.GetOrCreate();
        float cap = recordZoomSettleSeconds + 0.5f;
        for (float t = 0f; t < cap; t += Time.deltaTime)
        {
            yield return null;
            if (t >= 0.3f && (zoom == null || zoom.IsSettled)) break;
        }
        BeginMotifBridge("RecordCollected");

        // Backstop: the interstitial maze normally releases the camera. Count from the end of the
        // record's exit so a long loop wait can't cut the pull-back short.
        for (float waited = 0f; rings != null && rings.IsDocked && waited < 20f; waited += Time.deltaTime)
            yield return null;
        yield return new WaitForSeconds(Mathf.Max(0.5f, recordExitMaxHoldSeconds));
        _recordExitRoutine = null;
        ReleaseRecordExitZoom("cap");
    }

    /// <summary>
    /// The collected record's exit is over and the players stand in the interstitial maze (or the
    /// run moved on another way): the camera pulls back and everything else fades back in.
    /// Idempotent.
    /// </summary>
    public void ReleaseRecordExitZoom(string reason)
    {
        if (!RecordExitZoomActive) return;
        RecordExitZoomActive = false;
        if (_recordExitRoutine != null) { StopCoroutine(_recordExitRoutine); _recordExitRoutine = null; }
        var zoom = CameraZoomDirector.Current;
        if (zoom != null && _recordExitZoomId != 0) zoom.Release(_recordExitZoomId);
        _recordExitZoomId = 0;
        Debug.Log($"[VAULT] Record exit zoom released ({reason}).");
    }

    /// <summary>
    /// Drops any pending record-recovery state, the vault and the camera zoom (bridge, game over,
    /// new session). <paramref name="keepExitZoom"/>: the bridge starting from a collected record
    /// keeps the exit spotlight up (it is released by the interstitial maze).
    /// </summary>
    public void ResetRecordRecovery(bool keepExitZoom = false)
    {
        if (_recordReadyRoutine != null) { StopCoroutine(_recordReadyRoutine); _recordReadyRoutine = null; }
        if (_ringFocusRoutine != null)   { StopCoroutine(_ringFocusRoutine);   _ringFocusRoutine = null; }
        RecordReady       = false;
        RecordCollectable = false;
        if (dustGenerator != null && dustGenerator.IsRecordVaultSealed) dustGenerator.OpenRecordVault();
        var rings = GetMotifRingGlyphApplicator();
        if (rings != null) rings.OnDockBump -= HandleRecordDockBump;

        var zoom = CameraZoomDirector.Current;
        if (keepExitZoom && RecordExitZoomActive)
        {
            if (zoom != null && _ringFocusId != 0) zoom.Release(_ringFocusId);
        }
        else
        {
            RecordExitZoomActive = false;
            if (_recordExitRoutine != null) { StopCoroutine(_recordExitRoutine); _recordExitRoutine = null; }
            _recordExitZoomId = 0;
            if (zoom != null) zoom.ReleaseAll();
        }
        _ringFocusId = 0;
    }
}
