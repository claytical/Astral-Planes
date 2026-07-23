using UnityEngine;
using System.Collections.Generic;
using System.Linq;

public struct TransportFrame
{
    public int barIndex;
    public int playheadBin;
    public int boundarySerial;
}

public partial class InstrumentTrackController : MonoBehaviour
{
    [Header("Config")]
    [SerializeField] public InstrumentTrackControllerConfig config;
    public InstrumentTrack[] tracks;
    public NoteVisualizer noteVisualizer;
    private readonly Dictionary<InstrumentTrack, int> _loopHash = new();
    private bool _chordEventsSubscribed;
    [Header("Gravity Void (Expansion Waiting)")]
    [Tooltip("Spawned when this track stages an expansion (waiting for expansion). Despawned when expansion commits.")]
    [SerializeField] private GameObject gravityVoidPrefab;
    [Tooltip("Optional parent for the spawned gravity void instance.")]
    [SerializeField] private Transform gravityVoidParent;
// ---------------------------------------------------------------------
// SFX: Collection "Pickup Tick" (A2)
// ---------------------------------------------------------------------
    [SerializeField] private AudioSource pickupSfxSource;

    private double _lastTransportDsp;
// ------------------------------------------------------------
// Transport cache + guard against transient future-dated anchors
// ------------------------------------------------------------
    private bool _hasLastTransport;
    private TransportFrame _lastTransport;
    private double _lastTransportLeaderDsp; // leaderStartDspTime when the cache was written
    private GameFlowManager _gfm;

    private GravityVoidController _gravityVoid;
    private BinFrontierAllocator _binAllocator;

    private void EnsureGravityVoid()
    {
        if (_gravityVoid != null) return;
        _gravityVoid = new GravityVoidController(
            host: this,
            getGfm: () => { if (_gfm == null) _gfm = GameFlowManager.Instance; return _gfm; },
            getPrefab: () => gravityVoidPrefab,
            getParent: () => gravityVoidParent,
            getGravityVoidScale: () => config.gravityVoidScale,
            getVoidRingWidthCells: () => config.voidRingWidthCells,
            getGravityVoidImprintTickSeconds: () => config.gravityVoidImprintTickSeconds,
            getPlayheadBin: () => GetTransportFrame().playheadBin);
    }

    private void EnsureBinAllocator()
    {
        if (_binAllocator != null) return;
        _binAllocator = new BinFrontierAllocator(getTracks: () => tracks);
    }

    public void BeginGravityVoidForPendingExpand(InstrumentTrack ownerTrack, Vector3 centerWorld, Vector2Int centerGP)
    {
        EnsureGravityVoid();
        _gravityVoid.BeginGravityVoidForPendingExpand(ownerTrack, centerWorld, centerGP);
    }

    public void EndGravityVoidForPendingExpand(InstrumentTrack ownerTrack)
    {
        EnsureGravityVoid();
        _gravityVoid.EndGravityVoidForPendingExpand(ownerTrack);
    }

    public void AllowAdvanceNextBurst(InstrumentTrack track)
    {
        EnsureBinAllocator();
        _binAllocator.AllowAdvanceNextBurst(track);
    }

    public int GetBinForNextSpawn(InstrumentTrack track)
    {
        EnsureBinAllocator();
        return _binAllocator.GetBinForNextSpawn(track);
    }

    public bool IsChordGroupComplete(MusicalRole role, int binIndex)
    {
        EnsureBinAllocator();
        return _binAllocator.IsChordGroupComplete(role, binIndex);
    }

    public void NotifyBinFilled(InstrumentTrack track, int binIndex)
    {
        EnsureBinAllocator();
        _binAllocator.NotifyBinFilled(track, binIndex);
    }

    void Start()
    {
        _gfm = GameFlowManager.Instance;
        if (_gfm == null || !_gfm.ReadyToPlay()) return;
        EnsureGravityVoid();
        EnsureBinAllocator();
        noteVisualizer?.Initialize(); // ← ensures playhead + mapping are active
        ResetAllCursorsAndGuards();
        EnsurePickupSfxSource();
        UpdateVisualizer();
        // Subscribe to ascension-complete events
        foreach (var t in tracks)
            if (t != null)
            {
                t.RefreshRoleColorsFromProfile();
                t.OnAscensionCohortCompleted -= HandleAscensionCohortCompleted; // avoid dupes
                t.OnAscensionCohortCompleted += HandleAscensionCohortCompleted;
            }
        // Subscribe to the drum’s loop boundary so we (re)arm each loop
        var drum = _gfm.activeDrumTrack;
        TrySubscribeChordEvents();
        if (drum != null)
            drum.OnLoopBoundary += ArmCohortsOnLoopBoundary;
        ArmCohortsOnLoopBoundary();
    }

    void Update()
    {
        // Self-heal: if something reassigns tracks later, we’ll latch subscriptions once.
        if (!_chordEventsSubscribed)
            TrySubscribeChordEvents();
    }
    void OnEnable()
    {
        _chordEventsSubscribed = false;
        TrySubscribeChordEvents();   // first attempt
    }

    void OnDisable()
    {
        UnsubscribeChordEvents();
    }
    private void OnDestroy()
    {
        // tidy subscriptions
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var drum = _gfm ? _gfm.activeDrumTrack : null;
        if (drum != null) drum.OnLoopBoundary -= ArmCohortsOnLoopBoundary;
        foreach (var t in tracks)
            if (t != null)
                t.OnAscensionCohortCompleted -= HandleAscensionCohortCompleted;
    }
    private void TrySubscribeChordEvents()
    {
        // Prefer our own tracks array; if it isn't set, try to pull from the active controller
        var src = tracks;
        if (src == null || src.Length == 0)
        {
            if (_gfm == null) _gfm = GameFlowManager.Instance;
            var ctrl = _gfm ? _gfm.controller : null;
            if (ctrl != null && ctrl.tracks != null && ctrl.tracks.Length > 0)
                src = ctrl.tracks;
        }
        if (src == null || src.Length == 0) return; // not ready yet

        int count = 0;
        foreach (var t in src)
        {
            if (!t) continue;
            // De-dupe to avoid multiple adds if TrySubscribe runs more than once
            t.OnAscensionCohortCompleted -= HandleAscensionCohortCompleted;
            t.OnAscensionCohortCompleted += HandleAscensionCohortCompleted;
            t.OnCollectableBurstCleared -= HandleCollectableBurstCleared;
            t.OnCollectableBurstCleared += HandleCollectableBurstCleared;

            count++;
        }
        if (count > 0)
        {
            tracks = src; // keep the exact instances we subscribed to
            _chordEventsSubscribed = true;
        }
    }
    private void HandleCollectableBurstCleared(InstrumentTrack track, int burstId, bool hadNotes)
    {
        // StarPool subscribes directly to each track's OnCollectableBurstCleared event and
        // handles re-arming and bridge-gate logic. Nothing to do here.
    }
    private void UnsubscribeChordEvents()
    {
        if (tracks == null) return;
        foreach (var t in tracks)
        {
            if (!t) continue;
            t.OnAscensionCohortCompleted -= HandleAscensionCohortCompleted;
            t.OnCollectableBurstCleared -= HandleCollectableBurstCleared;
        }
        _chordEventsSubscribed = false;
    }
    private void ArmCohortsOnLoopBoundary()
    {
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var drum = _gfm?.activeDrumTrack;

        // Keep the drum's binning logic and the UI timebase aligned to the *committed* leader bins.
        // This is the primary place we re-sync, because it is guaranteed to run on loop boundaries.
        int committedBins = Mathf.Max(1, GetMaxActiveLoopMultiplier());
        if (drum != null)
            drum.SetBinCount(committedBins);
        if (GameFlowManager.VerboseLogging) Debug.Log($"[ITC:ARM_COHORTS] committedBins={committedBins} " +
                  $"trackMuls=[{string.Join(",", tracks.Where(t => t != null).Select(t => $"{t.name}:{t.loopMultiplier}"))}]");

        // Force the note grid to match the committed leader steps immediately.
        // Without this, the UI may remain at 1 bin even when the transport is already wider.
        if (noteVisualizer != null && drum != null)
        {
            int baseSteps = Mathf.Max(1, drum.totalSteps);
            noteVisualizer.RequestLeaderGridChange(committedBins * baseSteps);
        }

        foreach (var t in tracks)
        {
            if (t == null) continue;

            // Safety: if a bin has notes in the persistent loop but the burst-remaining
            // counter drifted (e.g. a collectable was collected via a secondary path),
            // resolve the fill now so audio unblocks within 1 boundary instead of never.
            t.ResolveStrandedBursts();
        }
    }
}
