using System;
using UnityEngine;

// ============================================================
//  IExpansionHost
//  The narrow interface TrackExpansionController needs back
//  into InstrumentTrack. Keeps the controller from importing
//  the full track API.
// ============================================================
public interface IExpansionHost
{
    // Identity / display
    string TrackName { get; }

    // Loop geometry (read)
    int  LoopMultiplier     { get; }
    int  MaxLoopMultiplier  { get; }
    int  TotalSteps         { get; }
    int  BinSize            { get; }

    // Loop geometry (write)
    void SetLoopMultiplier(int v);
    void SetTotalSteps(int v);

    // Step-cursor reset (called after boundary commit)
    void ResetStepCursors();

    // Bin allocation / fill state
    void SetBinAllocated(int bin, bool v);
    void SetBinFilled(int bin, bool v);
    void EnsureBinList();

    // Density selection
    int  PickRandomExistingBinForDensity();

    // Deferred spawn
    void EnqueueNextFrame(Action a);

    // Controller back-calls
    void ResyncLeaderBinsNow();
    void EndGravityVoidForPendingExpand();
    void RecomputeAllTrackLayouts();
    void MarkGhostPaddingOnVisualizer(int oldTotal, int addedSteps);
    void CanonicalizeTrackMarkersOnVisualizer(int burstId);
    void UpdateControllerVisualizer();
    int  GetControllerMaxActiveLoopMultiplier();
    int  GetControllerMaxLoopMultiplier();

    // Spawn callback (fired after expand commits)
    void SpawnBurstNow(
        NoteSet noteSet,
        int maxToSpawn,
        int burstId,
        Vector3? originWorld,
        Vector3? repelFromWorld,
        float burstImpulse,
        float spreadAngleDeg,
        float spawnJitterRadius,
        InstrumentTrack.BurstPlacementMode placementMode,
        int trapSearchRadiusCells,
        int trapBufferCells,
        int forcedTargetBin);
}

// ============================================================
//  TrackExpansionController
//  Manages the "stage expand → wait for loop boundary →
//  commit expand → fire staged burst" lifecycle for one
//  InstrumentTrack.
//
//  Extracted from InstrumentTrack.SpawnCollectableBurst (staging
//  branch) and InstrumentTrack.OnDrumDownbeat_CommitExpand.
// ============================================================
public class TrackExpansionController
{
    // ----------------------------------------------------------
    // State (was scattered across InstrumentTrack fields)
    // ----------------------------------------------------------
    private bool  PendingExpandForBurst       { get; set; }
    private bool  HookedBoundaryForExpand     { get; set; }
    private bool  ExpandCommitted             { get; set; }
    private int   OldTotalAtExpand            { get; set; }
    private int   HalfOffsetAtExpand          { get; set; }
    private bool  MapIncomingCollectionsToSecondHalf { get; set; }
    private int   PendingMapIntoSecondHalfCount      { get; set; }
    private float PendingMapTimeout                  { get; set; }
    public int   OverrideNextSpawnBin               { get; private set; } = -1;

    private PendingBurstData? _pendingBurstAfterExpand;

    // ----------------------------------------------------------
    // Cached references
    // ----------------------------------------------------------
    private readonly IExpansionHost _host;
    private DrumTrack _drumTrack;   // set by Bind(); updated when drum changes

    // ----------------------------------------------------------
    // PendingBurstData: mirrors InstrumentTrack.PendingBurst
    // (internal struct; kept here so IT doesn't need to expose it)
    // ----------------------------------------------------------
    public struct PendingBurstData
    {
        public NoteSet  noteSet;
        public int      maxToSpawn;
        public int      burstId;
        public Vector3? originWorld;
        public Vector3? repelFromWorld;
        public float    burstImpulse;
        public float    spreadAngleDeg;
        public float    spawnJitterRadius;
        public InstrumentTrack.BurstPlacementMode placementMode;
        public int      trapSearchRadiusCells;
        public int      trapBufferCells;
        public int      intendedTargetBin;
    }

    // ----------------------------------------------------------
    // Construction
    // ----------------------------------------------------------
    public TrackExpansionController(IExpansionHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>Call whenever the track's DrumTrack reference changes.</summary>
    public void Bind(DrumTrack drum)
    {
        if (_drumTrack == drum) return;

        // Unhook from old drum before re-binding
        UnhookExpandBoundary();
        _drumTrack = drum;
    }

    // ----------------------------------------------------------
    // Public query
    // ----------------------------------------------------------
    public bool IsExpansionPending =>
        PendingExpandForBurst || _pendingBurstAfterExpand.HasValue || HookedBoundaryForExpand;
    
    // ----------------------------------------------------------
    // Staging (called from SpawnCollectableBurst when targetBin >= loopMultiplier)
    // ----------------------------------------------------------

    /// <summary>
    /// Attempts to stage an expansion for the given burst.
    /// Returns false if an expansion is already staged (caller should drop the new burst).
    /// Returns true if staging succeeded OR if density-injection was enqueued instead.
    /// </summary>
    public bool TryStageExpand(PendingBurstData burst, int targetBin, Vector3 voidPos)
    {
        // Guard: only one staged expand at a time.
        if (PendingExpandForBurst || _pendingBurstAfterExpand.HasValue || HookedBoundaryForExpand)
        {
            int existingBid = _pendingBurstAfterExpand.HasValue ? _pendingBurstAfterExpand.Value.burstId : -1;
            Debug.LogWarning(
                $"[TEC:STAGE_EXPAND] IGNORE new stage (already pending) track={_host.TrackName} " +
                $"incomingBurstId={burst.burstId} existingBurstId={existingBid} " +
                $"pendExpand={PendingExpandForBurst} pendReq={_pendingBurstAfterExpand.HasValue} hooked={HookedBoundaryForExpand}"
            );
            return false;
        }

        // Defensive reset.
        if (ExpandCommitted)
            ExpandCommitted = false;

        // Already at max bins → density injection rather than expand.
        if (_host.LoopMultiplier >= Mathf.Max(1, _host.MaxLoopMultiplier))
        {
            _host.EnsureBinList();
            _host.SetBinAllocated(targetBin, true);

            Debug.Log($"[TEC:STAGE] LOOP MULTIPLIER MAXED. {_host.TrackName}");

            OverrideNextSpawnBin = _host.PickRandomExistingBinForDensity();
            int forcedBin = OverrideNextSpawnBin;
            var capBurst  = burst; // capture

            _host.EnqueueNextFrame(() =>
                _host.SpawnBurstNow(
                    capBurst.noteSet,
                    capBurst.maxToSpawn,
                    capBurst.burstId,
                    capBurst.originWorld,
                    capBurst.repelFromWorld,
                    capBurst.burstImpulse,
                    capBurst.spreadAngleDeg,
                    capBurst.spawnJitterRadius,
                    capBurst.placementMode,
                    capBurst.trapSearchRadiusCells,
                    capBurst.trapBufferCells,
                    forcedTargetBin: forcedBin));

            return true;
        }

        // Normal expand staging.
        PendingExpandForBurst   = true;
        _pendingBurstAfterExpand = burst;

        HookExpandBoundary();

        if (!HookedBoundaryForExpand)
        {
            Debug.LogError(
                $"[TEC:STAGE_EXPAND] HookExpandBoundary FAILED track={_host.TrackName} burstId={burst.burstId} " +
                "— ending void + clearing staged flags");
            _host.EndGravityVoidForPendingExpand();
            PendingExpandForBurst    = false;
            _pendingBurstAfterExpand = null;
            return false;
        }

        return true;
    }

    // ----------------------------------------------------------
    // Consume the override bin (called by SpawnCollectableBurst
    // at the top of its bin-selection logic)
    // ----------------------------------------------------------
    public int ConsumeOverrideNextSpawnBin()
    {
        int v = OverrideNextSpawnBin;
        OverrideNextSpawnBin = -1;
        return v;
    }

    // ----------------------------------------------------------
    // Update (call from InstrumentTrack.Update for timeout)
    // ----------------------------------------------------------
    public void Tick(float deltaTime)
    {
        if (MapIncomingCollectionsToSecondHalf && PendingMapTimeout > 0f)
        {
            PendingMapTimeout -= deltaTime;
            if (PendingMapTimeout <= 0f)
            {
                MapIncomingCollectionsToSecondHalf = false;
                PendingMapIntoSecondHalfCount      = 0;
            }
        }
    }

    // ----------------------------------------------------------
    // Reset (called by ResetBinStateForNewPhase / BeginNewMotifHardClear)
    // ----------------------------------------------------------
    public void ResetForNewPhase()
    {
        UnhookExpandBoundary();
        PendingExpandForBurst            = false;
        _pendingBurstAfterExpand         = null;
        ExpandCommitted                  = false;
        OldTotalAtExpand                 = 0;
        HalfOffsetAtExpand               = 0;
        MapIncomingCollectionsToSecondHalf = false;
        PendingMapIntoSecondHalfCount    = 0;
        PendingMapTimeout                = 0f;
        OverrideNextSpawnBin             = -1;
    }

    // ----------------------------------------------------------
    // EffectiveLoopBins helper (was InstrumentTrack.EffectiveLoopBins)
    // Called by InstrumentTrack.EffectiveLoopBins — forward here so
    // the expansion-aware span logic stays in one place.
    // ----------------------------------------------------------
    public bool IsExpandingAndMapping =>
        (ExpandCommitted || PendingExpandForBurst) &&
        (MapIncomingCollectionsToSecondHalf || PendingMapIntoSecondHalfCount > 0);

    // ----------------------------------------------------------
    // Hook / Unhook
    // ----------------------------------------------------------
    private void HookExpandBoundary()
    {
        if (HookedBoundaryForExpand || _drumTrack == null) return;
        _drumTrack.OnLoopBoundary += OnDrumDownbeat_CommitExpand;
        HookedBoundaryForExpand = true;
    }

    public void UnhookExpandBoundary()
    {
        if (!HookedBoundaryForExpand || _drumTrack == null) return;
        _drumTrack.OnLoopBoundary -= OnDrumDownbeat_CommitExpand;
        HookedBoundaryForExpand = false;
    }
    
    private void OnDrumDownbeat_CommitExpand()
    {
        bool hadAnyPending = PendingExpandForBurst || _pendingBurstAfterExpand.HasValue;

        try
        {
            bool   hasReq    = _pendingBurstAfterExpand.HasValue;
            var    req0      = hasReq ? _pendingBurstAfterExpand.Value : default;
            int    binSize0  = _host.BinSize;
            int    loopMul0  = _host.LoopMultiplier;
            int    total0    = _host.TotalSteps;
            int    oldTotal0 = OldTotalAtExpand;
            bool   exp0      = ExpandCommitted;
            bool   pendExp0  = PendingExpandForBurst;

            Debug.Log(
                $"[TEC:COMMIT_EXPAND] track={_host.TrackName} ENTER " +
                $"loopMul={loopMul0} totalSteps={total0} binSize={binSize0} " +
                $"pendExpand={pendExp0} pendReq={hasReq} " +
                $"expandCommitted={exp0} oldTotalAtExpand={oldTotal0} " +
                $"req.noteSet={(hasReq ? req0.noteSet?.ToString() : "null")} req.max={(hasReq ? req0.maxToSpawn : -1)}");

            if (!PendingExpandForBurst && !_pendingBurstAfterExpand.HasValue)
            {
                Debug.Log($"[TEC:COMMIT_EXPAND] track={_host.TrackName} EXIT(noop) reason=no_pending_flags");
                UnhookExpandBoundary();
                _host.EndGravityVoidForPendingExpand();
                return;
            }

            // ---- Path: already expanded (e.g. pre-widen) ----
            if (ExpandCommitted && !PendingExpandForBurst &&
                _host.TotalSteps >= OldTotalAtExpand + _host.BinSize)
            {
                Debug.Log(
                    $"[TEC:COMMIT_EXPAND] track={_host.TrackName} PATH=ALREADY_EXPANDED " +
                    $"totalSteps={_host.TotalSteps} oldTotalAtExpand={OldTotalAtExpand} binSize={_host.BinSize}");

                PendingExpandForBurst = false;

                if (_pendingBurstAfterExpand.HasValue)
                {
                    _host.EndGravityVoidForPendingExpand();
                    var req = _pendingBurstAfterExpand.Value;
                    _pendingBurstAfterExpand = null;

                    int forcedBin = req.intendedTargetBin;
                    _host.EnqueueNextFrame(() =>
                        _host.SpawnBurstNow(
                            req.noteSet, req.maxToSpawn, req.burstId,
                            req.originWorld, req.repelFromWorld,
                            req.burstImpulse, req.spreadAngleDeg,
                            spawnJitterRadius: 0.25f,
                            placementMode: InstrumentTrack.BurstPlacementMode.Free,
                            trapSearchRadiusCells: 10,
                            trapBufferCells: 1,
                            forcedTargetBin: forcedBin));
                }

                UnhookExpandBoundary();
                return;
            }

            // ---- A) Snapshot old width ----
            OldTotalAtExpand = _host.TotalSteps;

            int oldLeaderSteps = _host.GetControllerMaxActiveLoopMultiplier() *
                                 (_drumTrack != null ? _drumTrack.totalSteps : _host.BinSize);

            int maxBins = Mathf.Max(1, _host.MaxLoopMultiplier);
            int newBins = Mathf.Clamp(_host.LoopMultiplier + 1, 1, maxBins);

            // ---- Path: maxed density ----
            if (newBins == _host.LoopMultiplier)
            {
                Debug.LogWarning(
                    $"[TEC:COMMIT_EXPAND] track={_host.TrackName} PATH=MAXED_DENSITY " +
                    $"loopMul={_host.LoopMultiplier} maxBins={maxBins} pendReq={_pendingBurstAfterExpand.HasValue}");

                PendingExpandForBurst            = false;
                MapIncomingCollectionsToSecondHalf = false;
                ExpandCommitted                  = false;
                _host.EndGravityVoidForPendingExpand();

                if (_pendingBurstAfterExpand.HasValue)
                {
                    var req = _pendingBurstAfterExpand.Value;
                    _pendingBurstAfterExpand = null;
                    OverrideNextSpawnBin = _host.PickRandomExistingBinForDensity();
                    int forcedBin = req.intendedTargetBin;

                    _host.EnqueueNextFrame(() =>
                        _host.SpawnBurstNow(
                            req.noteSet, req.maxToSpawn, req.burstId,
                            req.originWorld, req.repelFromWorld,
                            req.burstImpulse, req.spreadAngleDeg,
                            spawnJitterRadius: 0.25f,
                            placementMode: InstrumentTrack.BurstPlacementMode.Free,
                            trapSearchRadiusCells: 10,
                            trapBufferCells: 1,
                            forcedTargetBin: forcedBin));
                }

                _host.RecomputeAllTrackLayouts();
                UnhookExpandBoundary();
                return;
            }

            // ---- B) Arm mapping / expand flags ----
            HalfOffsetAtExpand             = OldTotalAtExpand;
            MapIncomingCollectionsToSecondHalf = true;
            ExpandCommitted                = true;
            PendingExpandForBurst          = false;

            // ---- C) Apply new width ----
            _host.SetLoopMultiplier(newBins);
            _host.SetTotalSteps(_host.BinSize * newBins);
            _host.EnsureBinList();
            _host.ResyncLeaderBinsNow();

            Debug.Log(
                $"[TEC:COMMIT_EXPAND] track={_host.TrackName} PATH=WIDEN_APPLIED " +
                $"newBins={newBins} loopMulNow={_host.LoopMultiplier} totalStepsNow={_host.TotalSteps} " +
                $"halfOffset={HalfOffsetAtExpand} mapSecondHalf={MapIncomingCollectionsToSecondHalf}");

            _host.EndGravityVoidForPendingExpand();

            // ---- D) Mark new bin ----
            _host.SetBinAllocated(_host.LoopMultiplier - 1, true);
            _host.SetBinFilled(_host.LoopMultiplier - 1, false);

            // ---- E) Spawn staged burst ----
            if (_pendingBurstAfterExpand.HasValue)
            {
                var req = _pendingBurstAfterExpand.Value;
                _pendingBurstAfterExpand = null;

                _host.EnqueueNextFrame(() =>
                    _host.SpawnBurstNow(
                        req.noteSet, req.maxToSpawn, req.burstId,
                        req.originWorld, req.repelFromWorld,
                        req.burstImpulse, req.spreadAngleDeg,
                        req.spawnJitterRadius,
                        req.placementMode,
                        req.trapSearchRadiusCells,
                        req.trapBufferCells,
                        forcedTargetBin: req.intendedTargetBin));
            }

            // ---- G) Visual refresh for this track ----
            _host.UpdateControllerVisualizer();
            _host.CanonicalizeTrackMarkersOnVisualizer(0 /* currentBurstId resolved by host */);
            _host.MarkGhostPaddingOnVisualizer(OldTotalAtExpand, _host.TotalSteps - OldTotalAtExpand);

            // ---- H) If leader width changed, relayout all tracks ----
            int newLeaderSteps = _host.GetControllerMaxLoopMultiplier() *
                                 (_drumTrack != null ? _drumTrack.totalSteps : _host.BinSize);
            if (newLeaderSteps != oldLeaderSteps)
                _host.RecomputeAllTrackLayouts();

            // ---- I) Reset edge detector and unhook ----
            _host.ResetStepCursors();
            UnhookExpandBoundary();

            Debug.Log(
                $"[TEC:COMMIT_EXPAND] track={_host.TrackName} EXIT " +
                $"loopMulNow={_host.LoopMultiplier} totalStepsNow={_host.TotalSteps} " +
                $"mapSecondHalf={MapIncomingCollectionsToSecondHalf} expandCommitted={ExpandCommitted}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[TEC:COMMIT_EXPAND] EXCEPTION track={_host.TrackName} ex={ex}");
            throw;
        }
    }
}
