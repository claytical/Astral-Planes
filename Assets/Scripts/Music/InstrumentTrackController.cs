using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using MidiPlayerTK;

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
    [Header("Dynamic Track Spawning")]
    [Tooltip("Instantiated once per active voice (RoleVoiceKey) in the current motif by RebuildTracksForMotif.")]
    [SerializeField] private InstrumentTrack trackPrefab;
    [Tooltip("Instantiated once per spawned InstrumentTrack so each voice gets its own dedicated MIDI channel 0, avoiding channel-sharing bookkeeping.")]
    [SerializeField] private MidiStreamPlayer midiStreamPlayerPrefab;
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

    // Resolves the role profile the CURRENT motif's RoleMotifNoteSetConfig assigns to this
    // track's role, falling back to the library default only when no motif is active yet.
    // Callers must pass this into RefreshRoleColorsFromProfile() rather than calling it with
    // no argument — the no-argument fallback picks an arbitrary same-role profile via
    // MusicalRoleProfileLibrary and will silently clobber a correctly-configured motif profile
    // if it runs after PhaseTransitionManager has already applied one.
    private MusicalRoleProfile ResolveMotifRoleProfile(InstrumentTrack t)
    {
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var motif = _gfm?.phaseTransitionManager?.currentMotif;
        var cfg = motif?.GetConfigForRoleAtBin(t.assignedRole, 0, t.maxLoopMultiplier, t.voiceIndex);
        return cfg?.roleProfile;
    }

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

    // Per-track wiring applied both to tracks present at controller Start() and to tracks
    // freshly spawned by RebuildTracksForMotif — kept as one method so the two paths can't drift.
    private void WireTrack(InstrumentTrack t)
    {
        t.RefreshRoleColorsFromProfile(ResolveMotifRoleProfile(t));
        t.OnAscensionCohortCompleted -= HandleAscensionCohortCompleted; // avoid dupes
        t.OnAscensionCohortCompleted += HandleAscensionCohortCompleted;
    }

    // Tears down every existing InstrumentTrack (and its dedicated MidiStreamPlayer) and spawns
    // exactly one fresh InstrumentTrack per active voice in the given motif, replacing the old
    // fixed scene-wired tracks[] array. Called from PhaseTransitionManager at motif start, before
    // NoteSet bins are configured for the (now-existing) tracks.
    public void RebuildTracksForMotif(MotifProfile motif)
    {
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var drum = _gfm?.activeDrumTrack;

        UnsubscribeChordEvents();
        if (tracks != null)
        {
            foreach (var t in tracks)
            {
                if (t == null) continue;
                if (t.midiStreamPlayer != null) Destroy(t.midiStreamPlayer.gameObject);
                Destroy(t.gameObject);
            }
        }

        var voices = motif?.GetActiveVoices();
        if (voices == null || voices.Count == 0)
        {
            tracks = System.Array.Empty<InstrumentTrack>();
            return;
        }

        if (trackPrefab == null)
        {
            Debug.LogError("[ITC] RebuildTracksForMotif: trackPrefab not assigned; no tracks created.");
            tracks = System.Array.Empty<InstrumentTrack>();
            return;
        }

        var built = new List<InstrumentTrack>(voices.Count);
        foreach (var voice in voices)
        {
            var track = Instantiate(trackPrefab, transform);
            track.gameObject.SetActive(false);
            track.assignedRole = voice.role;
            track.controller = this;
            track.drumTrack = drum;

            if (midiStreamPlayerPrefab != null)
                track.midiStreamPlayer = Instantiate(midiStreamPlayerPrefab, track.transform);
            else
                Debug.LogError("[ITC] RebuildTracksForMotif: midiStreamPlayerPrefab not assigned; track will be silent.");

            track.gameObject.SetActive(true);
            built.Add(track);
        }

        tracks = built.ToArray();
        AssignVoiceIndices();

        foreach (var t in tracks)
            if (t != null) WireTrack(t);

        TrySubscribeChordEvents();
        noteVisualizer?.Initialize();
        UpdateVisualizer();
        _gfm?.SetupBinRingController();
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
                WireTrack(t);
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
