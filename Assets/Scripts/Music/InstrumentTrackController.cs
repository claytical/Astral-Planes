using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using MidiPlayerTK;
using Random = UnityEngine.Random;
public static class ShipTrackAssigner
{
}
public struct TransportFrame
{
    public int barIndex;
    public int playheadBin;
}

public class InstrumentTrackController : MonoBehaviour
{
    public InstrumentTrack[] tracks;
    public NoteVisualizer noteVisualizer;
    private readonly Dictionary<InstrumentTrack, int> _loopHash = new();
    [SerializeField] private MusicalRole cohortTriggerRole = MusicalRole.Lead;
    [SerializeField] private float cohortWindowFraction = 0.5f; // e.g., lower half of the leader loop (0..16 when leader is 32)
    private bool _chordEventsSubscribed;
    private Vector2Int _gravityVoidCenterGP;
    private bool _gravityVoidHasCenterGP;
    public float lastCollectionTime { get; private set; } = -1f;
    private readonly HashSet<(InstrumentTrack track, int bin)> _binExtensionSignaled = new();
    private readonly Dictionary<InstrumentTrack, bool> _allowAdvanceNextBurst = new Dictionary<InstrumentTrack, bool>();
    [Header("Gravity Void (Expansion Waiting)")]
    [Tooltip("Spawned when this track stages an expansion (waiting for expansion). Despawned when expansion commits.")]
    [SerializeField] private GameObject gravityVoidPrefab;
    [Tooltip("Optional parent for the spawned gravity void instance.")]
    [SerializeField] private Transform gravityVoidParent;
    [Tooltip("Scale multiplier applied to the spawned void instance.")]
    [SerializeField] private float gravityVoidScale = 1f;
    private GameObject _gravityVoidInstance;
    private ParticleSystem[] _gravityVoidParticles;
    // Cache prefab alpha per particle system so alpha doesn't compound.
    private float[] _gravityVoidPrefabStartAlpha;

// Optionally track current outer radius for VFX scaling.
    private int _gravityVoidCurrentOuterR;

    [Header("Gravity Void → Dust Imprint")]
    [SerializeField] private float gravityVoidGrowSeconds = 1.25f;
    [SerializeField] private int gravityVoidMaxRadiusCells = 120;
    [SerializeField] private float gravityVoidImprintTickSeconds = 0.05f;
    [SerializeField] private int gravityVoidImprintBudgetPerTick = 80; // -1 = unlimited
    private Coroutine _gravityVoidRoutine;
    private InstrumentTrack _gravityVoidOwner;
    private Vector3 _gravityVoidCenterWorld;
    private Color _gravityVoidParticleTint;
    private Color _gravityVoidDustImprintTint;
    private float _gravityVoidDustHardness01;
    private float _gravityVoidGrowSecondsRuntime = -1f;
    private int   _gravityVoidMaxRadiusRuntime   = -1;

    public void AllowAdvanceNextBurst(InstrumentTrack track)
    {
        if (track == null) return;
        _allowAdvanceNextBurst[track] = true;
    }
    private bool ConsumeAllowAdvanceNextBurst(InstrumentTrack track)
    {
        if (track == null) return false;

        if (_allowAdvanceNextBurst.TryGetValue(track, out bool allowed) && allowed)
        {
            _allowAdvanceNextBurst[track] = false;
            return true;
        }
        return false;
    }
    public void NotifyCollected() {
        lastCollectionTime = Time.time;
    }

public void BeginGravityVoidForPendingExpand(InstrumentTrack ownerTrack, Vector3 centerWorld, Vector2Int centerGP)
{
    if (ownerTrack == null) return;

    // Is this a repeat Begin for the same owner while already active?
    bool sameOwner = (_gravityVoidOwner != null && ownerTrack == _gravityVoidOwner);
    bool routineRunning = (_gravityVoidRoutine != null);
    bool instanceAlive = (_gravityVoidInstance != null);

    // Update owner + center every time (Begin acts as "refresh" too).
    _gravityVoidOwner = ownerTrack;
    _gravityVoidCenterWorld = centerWorld;
    _gravityVoidCenterGP = centerGP;
    _gravityVoidHasCenterGP = true;

    // Resolve tint/hardness from role profile (or fallback).
    var roleProfile = MusicalRoleProfileLibrary.GetProfile(ownerTrack.assignedRole);
    if (roleProfile != null)
    {
        _gravityVoidDustImprintTint = roleProfile.GetBaseColor();
        _gravityVoidDustHardness01 = roleProfile.GetDustHardness01();

        _gravityVoidParticleTint = roleProfile.GetBaseColor();
        _gravityVoidParticleTint.a = 1f; // prefab alpha is authoritative
    }
    else
    {
        _gravityVoidDustImprintTint = ownerTrack.trackColor;
        _gravityVoidDustHardness01 = 0.5f;

        _gravityVoidParticleTint = ownerTrack.trackColor;
        _gravityVoidParticleTint.a = 1f;
    }

    // Spawn if needed, otherwise update visuals/position (must not destroy/recreate).
    SpawnOrUpdateGravityVoid(_gravityVoidCenterWorld, _gravityVoidParticleTint);

    // Recompute runtime parameters; these can change while pending.
    _gravityVoidGrowSecondsRuntime = gravityVoidGrowSeconds;
    _gravityVoidMaxRadiusRuntime = gravityVoidMaxRadiusCells;

    if (_gravityVoidOwner != null)
    {
        // Drive dur by DSP time-to-commit, so the final radius happens at the commit point.
        float secsToCommit = _gravityVoidOwner.GetSecondsUntilNextLoopBoundaryDSP();
        if (secsToCommit > 0.01f)
            _gravityVoidGrowSecondsRuntime = secsToCommit;

        // Radius mapping: 1 bin = 1 radius cell (visualize incoming bin => current + 1).
        int targetRadius = Mathf.Max(1, _gravityVoidOwner.loopMultiplier + 1);
        _gravityVoidMaxRadiusRuntime = targetRadius;
    }

    // --- CRITICAL: don't restart the coroutine if it's already running for this owner. ---
    // Restarting here is what looks like "respawn at the boundary".
    if (sameOwner && routineRunning)
    {
        Debug.Log(
            $"[VOID] REFRESH (no-restart) track={ownerTrack.name} " +
            $"go={(_gravityVoidInstance ? _gravityVoidInstance.GetInstanceID() : -1)} " +
            $"pos={centerWorld} gp={centerGP} grow={_gravityVoidGrowSecondsRuntime:F2}s rMax={_gravityVoidMaxRadiusRuntime}"
        );
        return;
    }

    // If owner changed, or routine is missing, (re)start cleanly.
    if (_gravityVoidRoutine != null)
    {
        StopCoroutine(_gravityVoidRoutine);
        _gravityVoidRoutine = null;
    }

    Debug.Log(
        $"[VOID] BEGIN (start) track={ownerTrack.name} " +
        $"prevOwner={(sameOwner ? "same" : "changed")} routineWas={routineRunning} instWas={instanceAlive} " +
        $"go={(_gravityVoidInstance ? _gravityVoidInstance.GetInstanceID() : -1)} pos={centerWorld} gp={centerGP} " +
        $"grow={_gravityVoidGrowSecondsRuntime:F2}s rMax={_gravityVoidMaxRadiusRuntime}"
    );

    _gravityVoidRoutine = StartCoroutine(GravityVoidGrowAndImprintRoutine());
}

public void EndGravityVoidForPendingExpand(InstrumentTrack ownerTrack)
{
    // If caller provides an owner, only allow the owner to end it.
    if (ownerTrack != null && _gravityVoidOwner != null && ownerTrack != _gravityVoidOwner)
    {
        Debug.LogWarning(
            $"[VOID] END ignored (wrong owner) caller={ownerTrack.name} owner={_gravityVoidOwner.name} " +
            $"go={(_gravityVoidInstance ? _gravityVoidInstance.GetInstanceID() : -1)}"
        );
        return;
    }

    Debug.Log(
        $"[VOID] END track={(_gravityVoidOwner ? _gravityVoidOwner.name : "null")} " +
        $"caller={(ownerTrack ? ownerTrack.name : "null")} " +
        $"go={(_gravityVoidInstance ? _gravityVoidInstance.GetInstanceID() : -1)}"
    );

    _gravityVoidOwner = null;
    _gravityVoidHasCenterGP = false;

    if (_gravityVoidRoutine != null)
    {
        StopCoroutine(_gravityVoidRoutine);
        _gravityVoidRoutine = null;
    }

    DespawnGravityVoid();
}

private IEnumerator GravityVoidGrowAndImprintRoutine()
{
    int _lastGravityVoidChordBin = -1;
    var gfm = GameFlowManager.Instance;
    var dustGen = (gfm != null) ? gfm.dustGenerator : null;
    if (dustGen == null)
        yield break;

    float dur  = Mathf.Max(0.01f, (_gravityVoidGrowSecondsRuntime > 0f) ? _gravityVoidGrowSecondsRuntime : gravityVoidGrowSeconds);
    float tick = Mathf.Max(0.01f, gravityVoidImprintTickSeconds);
    int maxR   = Mathf.Max(0, (_gravityVoidMaxRadiusRuntime >= 0) ? _gravityVoidMaxRadiusRuntime : gravityVoidMaxRadiusCells);

    // We track the OUTER radius we've actually completed imprinting up to.
    int completedOuterR = 0;

    float startTime = Time.time;
    float nextTickTime = startTime; // immediate first tick

    // How long each radius step should take to feel like constant outward growth.
    float secsPerRadius = (maxR > 0) ? (dur / maxR) : dur;

    while (_gravityVoidOwner != null)
    {
        // Always keep VFX positioned (and potentially scaled in SpawnOrUpdate)
        SpawnOrUpdateGravityVoid(_gravityVoidCenterWorld, _gravityVoidParticleTint);

        float now = Time.time;
        float elapsed = now - startTime;
// ------------------------------------------------------------
// Gravity Void chord pulse: fire ONCE at each bin boundary
// ------------------------------------------------------------
        int playheadBin = GetTransportFrame().playheadBin;
        if (playheadBin != _lastGravityVoidChordBin)
        {
            _lastGravityVoidChordBin = playheadBin;

            int chordSize = Mathf.Clamp(2 + playheadBin, 2, 5);
            PlayGravityVoidChordPulse(_gravityVoidOwner, playheadBin, chordSize);
        }

        // --- STEADY GROWTH MAPPING ---
        // Radius increases by ~1 every secsPerRadius seconds, up to maxR.
        int targetOuterR = (maxR <= 0)
            ? 0
            : Mathf.Clamp(1 + Mathf.FloorToInt(elapsed / Mathf.Max(0.001f, secsPerRadius)), 1, maxR);
        _gravityVoidCurrentOuterR = targetOuterR;

        // If we haven't reached our target radius, do budgeted imprint work toward it.
        if (_gravityVoidHasCenterGP && dustGen != null && targetOuterR > completedOuterR)
        {
            int budget = gravityVoidImprintBudgetPerTick;

            int processed = dustGen.ApplyVoidImprintDiskFromGrid(
                _gravityVoidCenterGP,
                outerRadiusCells: targetOuterR,
                imprintColor: _gravityVoidDustImprintTint,
                imprintHardness01: _gravityVoidDustHardness01,
                maxCellsThisCall: budget,
                innerRadiusCellsExclusive: completedOuterR
            );

            // Budget semantics:
            // - If budget < 0 => unlimited, treat as fully completed.
            // - If processed < budget => likely finished the requested annulus this tick.
            // - If processed == budget => likely capped, keep working this annulus next tick.
            if (budget < 0)
            {
                completedOuterR = targetOuterR;
            }
            else if (processed > 0 && processed < budget)
            {
                completedOuterR = targetOuterR;
            }
            else
            {
                // processed == 0 or processed == budget: do not advance completedOuterR.
                // This keeps us filling the same annulus over multiple ticks rather than skipping ahead.
            }
        }

        // After we reach max radius, we keep running (VFX stays) until EndGravityVoid... clears owner.
        // That matches "keeps moving outward until the void disappears."
        nextTickTime += tick;
        float wait = Mathf.Max(0.001f, nextTickTime - Time.time);
        yield return new WaitForSeconds(wait);
    }

    _gravityVoidRoutine = null;
}
private void PlayGravityVoidChordPulse(
    InstrumentTrack track,
    int playheadBin,
    int chordSize)
{
    if (track == null) return;

    var harmony = GameFlowManager.Instance?.harmony;
    if (harmony == null) return;

    int chordIdx = track.Harmony_GetChordIndexForBin(playheadBin);
    if (chordIdx < 0) return;

    if (!harmony.TryGetChordAt(chordIdx, out var chord))
        return;

    var notes = BuildGravityVoidVoicing(
        track.assignedRole,
        chord,
        chordSize,
        track.lowestAllowedNote,
        track.highestAllowedNote
    );

    int durTicks = 480; 
    float vel127 = 80f;

    foreach (int midi in notes)
        track.PlayNote127(midi, durTicks, vel127);
}
private List<int> BuildGravityVoidVoicing(
    MusicalRole role,
    Chord chord,
    int targetCount,
    int low,
    int high)
{
    // Build pitch classes from chord
    var pcs = chord.intervals
        .Select(i => (chord.rootNote + i) % 12)
        .Distinct()
        .ToList();

    int rootPC  = chord.rootNote % 12;
    int thirdPC = pcs.FirstOrDefault(pc => (pc - rootPC + 12) % 12 is 3 or 4);
    int fifthPC = pcs.FirstOrDefault(pc => (pc - rootPC + 12) % 12 == 7);
    int seventhPC = pcs.FirstOrDefault(pc => (pc - rootPC + 12) % 12 is 10 or 11);

    List<int> priorityPCs = role switch
    {
        // --------------------------------------------------
        // Bass: guide tones first, avoid root dominance
        // --------------------------------------------------
        MusicalRole.Bass => new()
        {
            thirdPC,
            seventhPC,
            fifthPC,
            rootPC,
            thirdPC
        },

        // --------------------------------------------------
        // Harmony: classic shell → full stack
        // --------------------------------------------------
        MusicalRole.Harmony => new()
        {
            thirdPC,
            seventhPC,
            rootPC,
            fifthPC,
            seventhPC
        },

        // --------------------------------------------------
        // Lead: color tones, higher tension
        // --------------------------------------------------
        MusicalRole.Lead => new()
        {
            seventhPC,
            thirdPC,
            fifthPC,
            rootPC,
            seventhPC
        },

        // --------------------------------------------------
        // Groove / mid-perc tonal
        // --------------------------------------------------
        MusicalRole.Groove => new()
        {
            thirdPC,
            fifthPC,
            seventhPC,
            rootPC,
            thirdPC
        },

        _ => pcs
    };

    var result = new List<int>();
    int octaveAnchor = (low + high) / 2;

    foreach (int pc in priorityPCs)
    {
        if (result.Count >= targetCount) break;

        int note = FitPitchClassToRange(pc, octaveAnchor, low, high);
        if (!result.Contains(note))
            result.Add(note);
    }

    return result;
}
private int FitPitchClassToRange(int pc, int anchor, int low, int high)
{
    int note = anchor - ((anchor - pc + 120) % 12);
    while (note < low)  note += 12;
    while (note > high) note -= 12;
    return Mathf.Clamp(note, low, high);
}

public void DespawnGravityVoid()
{
    if (_gravityVoidInstance == null)
        return;

    Destroy(_gravityVoidInstance);

    _gravityVoidInstance = null;
    _gravityVoidParticles = null;

    // Clear cached prefab alpha so the next spawn re-captures it
    _gravityVoidPrefabStartAlpha = null;

    // Reset VFX growth state
    _gravityVoidCurrentOuterR = 0;

    // Clear center bookkeeping (defensive; owner is cleared elsewhere)
    _gravityVoidHasCenterGP = false;
}

    public void SpawnOrUpdateGravityVoid(Vector3 worldPos, Color tint)
{
    if (gravityVoidPrefab == null) return;

    if (_gravityVoidInstance == null)
    {
        var parent = (gravityVoidParent != null) ? gravityVoidParent : transform;
        _gravityVoidInstance = Instantiate(gravityVoidPrefab, worldPos, Quaternion.identity, parent);

        if (gravityVoidScale != 1f)
            _gravityVoidInstance.transform.localScale *= gravityVoidScale;

        _gravityVoidParticles = _gravityVoidInstance.GetComponentsInChildren<ParticleSystem>(true);

        // Cache each particle system's prefab alpha ONCE so we can preserve it.
        if (_gravityVoidParticles != null && _gravityVoidParticles.Length > 0)
        {
            _gravityVoidPrefabStartAlpha = new float[_gravityVoidParticles.Length];
            for (int i = 0; i < _gravityVoidParticles.Length; i++)
            {
                var ps = _gravityVoidParticles[i];
                if (ps == null) { _gravityVoidPrefabStartAlpha[i] = 1f; continue; }

                var main = ps.main;

                // startColor can be a gradient; this grabs a representative color.
                // If your prefab uses gradients heavily and you need exactness, we can upgrade this,
                // but for your use (white low-alpha ring), this is correct.
                _gravityVoidPrefabStartAlpha[i] = main.startColor.color.a;
            }
        }
    }
    else
    {
        _gravityVoidInstance.transform.position = worldPos;
    }

    if (_gravityVoidParticles == null) return;

    // ----- OPTIONAL: scale VFX outward to match current radius -----
    // This assumes your prefab ring looks correct at "max" scale = gravityVoidScale.
    // We scale from a small minimum up to full based on current outer radius.
    if (_gravityVoidInstance != null && _gravityVoidMaxRadiusRuntime > 0)
    {
        float frac = Mathf.Clamp01((float)_gravityVoidCurrentOuterR / (float)_gravityVoidMaxRadiusRuntime);

        // Keep a visible presence even at the start (so it doesn't look like "nothing happens").
        const float minFrac = 0.15f;
        float s = Mathf.Lerp(minFrac, 1f, frac);

        // Preserve your authored prefab scale + gravityVoidScale multiplier.
        // We apply a uniform multiplier on top.
        Vector3 baseScale = Vector3.one * gravityVoidScale;
        _gravityVoidInstance.transform.localScale = baseScale * s;
    }

    // ----- Tint particles without compounding alpha -----
    for (int i = 0; i < _gravityVoidParticles.Length; i++)
    {
        var ps = _gravityVoidParticles[i];
        if (ps == null) continue;

        var main = ps.main;

        float prefabA = 1f;
        if (_gravityVoidPrefabStartAlpha != null && i >= 0 && i < _gravityVoidPrefabStartAlpha.Length)
            prefabA = _gravityVoidPrefabStartAlpha[i];

        Color outC = tint;
        outC.a = prefabA * tint.a;      // preserve prefab alpha; tint.a is your multiplier

        main.startColor = outC;
    }
}

    public void ResetControllerBinGuards()
    {
        _binExtensionSignaled?.Clear();
    }
    void Start()
    {
        if (!GameFlowManager.Instance.ReadyToPlay()) return;
        noteVisualizer?.Initialize(); // ← ensures playhead + mapping are active
        ResetAllCursorsAndGuards(clearLoops:false);
        UpdateVisualizer();
        // Subscribe to ascension-complete events
        foreach (var t in tracks)
            if (t != null)
            {
                t.RefreshRoleColorsFromProfile();
                t.OnAscensionCohortCompleted -= HandleAscensionCohortCompleted; // avoid dupes
                t.OnAscensionCohortCompleted += HandleAscensionCohortCompleted;
                Debug.Log($"[CHORD][SUB] Controller subscribed to CohortCompleted for track={t.name} role={t.assignedRole} id={t.GetInstanceID()}");
            }
        // Subscribe to the drum’s loop boundary so we (re)arm each loop
        var drum = GameFlowManager.Instance.activeDrumTrack;
        TrySubscribeChordEvents(); 
        if (drum != null)
            drum.OnLoopBoundary += ArmCohortsOnLoopBoundary;
        ArmCohortsOnLoopBoundary();
    }
    /// <summary>
    /// Single source of truth for which bin is currently audible,
    /// derived ONLY from DSP time and the drum clip length.
    /// </summary>
    public TransportFrame GetTransportFrame()
    {
        var drum = GameFlowManager.Instance?.activeDrumTrack;
        if (drum == null)
            return default;

        double dspNow = AudioSettings.dspTime;
        // IMPORTANT: use the *leader-loop* transport anchor when available.
        // startDspTime is the clip schedule anchor; leaderStartDspTime is rebased when
        // the effective leader loop length changes (expand/collapse).
        double start  = (drum.leaderStartDspTime > 0.0) ? drum.leaderStartDspTime : drum.startDspTime;
        if (start <= 0.0)
            return default;

        float clipLen = drum.GetClipLengthInSeconds();
        if (clipLen <= 0f)
            return default;

        // Each “bin” is one base drum clip.
        int barIndex = Mathf.FloorToInt((float)((dspNow - start) / clipLen));

        // Transport must be derived from *committed/audible* leader bins, not visual bins.
        int leaderBins = Mathf.Max(1, drum.GetCommittedBinCount());
        int playheadBin = ((barIndex % leaderBins) + leaderBins) % leaderBins;

        return new TransportFrame
        {
            barIndex   = barIndex,
            playheadBin = playheadBin
        };
    }

    /// <summary>
    /// Immediate re-sync of drum binning + note grid to the committed leader bins.
    /// Call this when a track commits an expand/collapse mid-frame so the UI/audio
    /// cannot spend a whole loop visually desynchronized.
    /// </summary>
    public void ResyncLeaderBinsNow()
    {
        var drum = GameFlowManager.Instance?.activeDrumTrack;
        if (drum == null) return;

        int bins = Mathf.Max(1, GetMaxActiveLoopMultiplier());
        drum.SetBinCount(bins);

        if (noteVisualizer != null)
        {
            int baseSteps = Mathf.Max(1, drum.totalSteps);
            noteVisualizer.RequestLeaderGridChange(bins * baseSteps);
        }
    }
    public int GetCommittedLeaderBins()
    {
        var drum = GameFlowManager.Instance != null ? GameFlowManager.Instance.activeDrumTrack : null;
        if (drum == null) return 1;
        return Mathf.Max(1, drum.GetCommittedBinCount());
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
        var drum = GameFlowManager.Instance ? GameFlowManager.Instance.activeDrumTrack : null;
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
            var ctrl = GameFlowManager.Instance ? GameFlowManager.Instance.controller : null;
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
            Debug.Log($"[CHORD][SUB] Subscribed to CohortCompleted on {count} tracks");
        }
    }
    private void HandleCollectableBurstCleared(InstrumentTrack track, int burstId) {
    Debug.Log($"[CURSOR] track={(track != null ? track.name : "null")} burstId={burstId} " +
              $"globalCIF={AnyCollectablesInFlight()} globalEP={AnyExpansionPending()}");

    // We only want to advance when ALL collectables are gone (across tracks).
    if (AnyCollectablesInFlight()) return;

    var gfm = GameFlowManager.Instance;
    if (gfm == null || gfm.activeDrumTrack == null) return;

    var star = gfm.activeDrumTrack._star;
    if (star == null) return;

    // During/after bridge start we must not re-arm or spawn new directives.
    // PhaseStar.NotifyCollectableBurstCleared() is also bridge-safe, but keep this guard to reduce noise.
    if (gfm.GhostCycleInProgress)
    {
        Debug.Log("[CTRL:BURST_CLEARED] IGNORE (GhostCycleInProgress)");
        return;
    }

    Debug.Log($"[CTRL:BURST_CLEARED] Notify PhaseStar: track={(track != null ? track.name : "null")} burstId={burstId}");
    star.NotifyCollectableBurstCleared();
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
    public float GetEffectiveLoopLengthInSeconds()
    {
        var gfm  = GameFlowManager.Instance;
        var drum = gfm != null ? gfm.activeDrumTrack : null;
        if (drum == null)
            return 0f;

        // IMPORTANT: use the *clip* length, not DrumTrack.GetLoopLengthInSeconds()
        float clipLen = drum.GetClipLengthInSeconds();
        int   totalSteps = drum.totalSteps;
        if (clipLen <= 0f || totalSteps <= 0)
            return clipLen;

        // LeaderSteps already looks at track loopMultipliers
        int leaderSteps = drum.GetLeaderSteps();
        if (leaderSteps <= 0)
            return clipLen;

        float stepDuration = clipLen / totalSteps;
        return stepDuration * leaderSteps;
    }
    private void ArmCohortsOnLoopBoundary()
    {
        var drum = GameFlowManager.Instance.activeDrumTrack;

        // Keep the drum's binning logic and the UI timebase aligned to the *committed* leader bins.
        // This is the primary place we re-sync, because it is guaranteed to run on loop boundaries.
        int committedBins = Mathf.Max(1, GetMaxActiveLoopMultiplier());
        if (drum != null)
            drum.SetBinCount(committedBins);

        int leaderSteps = (drum != null) ? drum.GetLeaderSteps() : 0;
        if (leaderSteps <= 0)
        {
            // Fallback: use max of actual tracks if drum not ready yet
            leaderSteps = tracks.Where(t => t != null).Select(t => t.GetTotalSteps()).DefaultIfEmpty(32).Max();
        }

        // Force the note grid to match the committed leader steps immediately.
        // Without this, the UI may remain at 1 bin even when the transport is already wider.
        if (noteVisualizer != null && drum != null)
        {
            int baseSteps = Mathf.Max(1, drum.totalSteps);
            noteVisualizer.RequestLeaderGridChange(committedBins * baseSteps);
        }

        int start = 0;
        int endLeader = Mathf.Max(1, Mathf.RoundToInt(leaderSteps * Mathf.Clamp01(cohortWindowFraction)));

        foreach (var t in tracks)
        {
            if (t == null) continue;

            int trackSteps = Mathf.Max(1, t.GetTotalSteps()); // <- NO extra * loopMultiplier
            // Map [0..endLeader) from leader-space into this track’s local modulus
            int endTrack = Mathf.Clamp(endLeader, 1, trackSteps);

            t.ArmAscensionCohort(0, endTrack);

            Debug.Log($"[CHORD][ARM] {t.name} role={t.assignedRole} window=[0,{endTrack}) " +
                      $"trackSteps={trackSteps} leaderSteps={leaderSteps} " +
                      $"armed={t.ascensionCohort.armed} remaining={(t.ascensionCohort.stepsRemaining!=null?t.ascensionCohort.stepsRemaining.Count:0)}");
        }
    }
public int GetBinForNextSpawn(InstrumentTrack track)
{
    if (track == null)
        return 0;

    int trackMaxBinIndex = Mathf.Max(0, track.maxLoopMultiplier - 1);

    // Consume exactly once (IMPORTANT).
    bool allowAdvance = ConsumeAllowAdvanceNextBurst(track);

    // Helper: frontier = furthest bin that is either allocated OR filled,
    // plus a cursor-based view, but CLAMPED to track capacity.
    int FrontierFor(InstrumentTrack t)
    {
        if (t == null) return -1;

        int highest = Mathf.Max(t.GetHighestFilledBin(), t.GetHighestAllocatedBin());

        // Cursor points to NEXT bin to write. Convert to "last touched" estimate.
        int cursorBased = t.GetBinCursor() - 1;

        // Clamp cursorBased into valid bin range; do NOT let it grow unbounded.
        cursorBased = Mathf.Clamp(cursorBased, -1, Mathf.Max(0, t.maxLoopMultiplier - 1));

        return Mathf.Max(highest, cursorBased);
    }

    // 1) Compute global frontier across all tracks.
    int globalFrontier = -1;
    if (tracks != null)
    {
        for (int i = 0; i < tracks.Length; i++)
        {
            var t = tracks[i];
            if (!t) continue;
            globalFrontier = Mathf.Max(globalFrontier, FrontierFor(t));
        }
    }

    if (globalFrontier < 0)
        return 0;

    int clampedGlobalFrontier = Mathf.Clamp(globalFrontier, 0, trackMaxBinIndex);

    // 2) Track-local frontier
    int trackFrontier = FrontierFor(track);

    // 3) If this track is behind the global frontier, fill holes up to the frontier.
    if (trackFrontier < clampedGlobalFrontier)
    {
        for (int b = 0; b <= clampedGlobalFrontier; b++)
        {
            if (!track.IsBinAllocated(b))
                return b;
        }
        return clampedGlobalFrontier;
    }

    // 4) If we are NOT allowed to advance frontier, choose next local bin deterministically.
    // This is where you want bin0 again when all bins are filled/allocated.
    if (!allowAdvance)
    {
        int local = Mathf.Clamp(track.GetNextBinForSpawn(), 0, trackMaxBinIndex);
        return local;
    }

    // 5) Allowed to advance: normally use cursor, but WRAP it into capacity.
    int cursorTarget = track.GetBinCursor();
    if (track.maxLoopMultiplier > 0)
        cursorTarget = cursorTarget % track.maxLoopMultiplier;
    cursorTarget = Mathf.Clamp(cursorTarget, 0, trackMaxBinIndex);

    // If cursorTarget is beyond global frontier, it is an attempt to push frontier.
    if (cursorTarget > clampedGlobalFrontier)
    {
        // If pushing would exceed capacity, inject density deterministically.
        if (cursorTarget > trackMaxBinIndex)
        {
            // round-robin into a filled bin (prefer content already in loop)
            return Mathf.Clamp(track.GetNextFilledBinForDensity(), 0, trackMaxBinIndex);
        }
        return cursorTarget;
    }

    // Otherwise, decide whether to advance based on whether cursor bin is filled.
    bool cursorFilled = track.IsBinFilled(cursorTarget);
    int proposed = (cursorFilled) ? (clampedGlobalFrontier + 1) : cursorTarget;

    // If advancing would exceed capacity, inject density deterministically.
    if (proposed > trackMaxBinIndex)
        return Mathf.Clamp(track.GetNextFilledBinForDensity(), 0, trackMaxBinIndex);

    return proposed;
}

    private void ResetAllCursorsAndGuards(bool clearLoops=false)
        {
            ResetControllerBinGuards();
            if (tracks == null) return;
            foreach (var t in tracks)
                if (t) t.ResetBinsForPhase();
        } 
    public bool AnyExpansionPending() {
        var offenders = tracks.Where(t => t != null && t.IsExpansionPending).Select(t => t.name).ToArray();
        Debug.Log($"[CTRLDBG] AnyExpansionPending={offenders.Length>0} offenders=[{string.Join(", ", offenders)}]");

        if (tracks == null || tracks.Length == 0) return false; 
        foreach (var t in tracks) {
            if (t.IsExpansionPending) {
                Debug.Log($"[CTRL:EP] {t.name} pendExpand={t._pendingExpandForBurst}  hooked={t._hookedBoundaryForExpand}");
            }
            if (!t) continue;
            if (t.IsExpansionPending) return true; 
        } 
        return false;
    }
    public bool AnyCollectablesInFlight()
    {
        if (tracks == null) return false;

        bool any = false;

        foreach (var t in tracks)
        {
            if (t == null) continue;

            // prune stale refs (null/inactive) so we don't get stuck on ghosts
            t.PruneSpawnedCollectables();

            if (t.spawnedCollectables == null) continue;

            // only count truly alive + active objects
            for (int i = 0; i < t.spawnedCollectables.Count; i++)
            {
                var go = t.spawnedCollectables[i];
                if (go != null && go.activeInHierarchy)
                {
                    any = true;
                    break;
                }
            }

            if (any) break;
        }

        return any;
    }

    public int ForceDestroyAllCollectablesInFlight(string reason)
    {
        int destroyed = 0;
        if (tracks == null) return destroyed;

        foreach (var t in tracks)
        {
            if (t == null) continue;
            destroyed += t.ForceDestroyCollectablesInFlight(reason);
        }

        return destroyed;
    }

    /// <summary>
    /// Single authority entry point for motif boundaries.
    /// This should be called exactly once when a new motif begins (after any bridge/ghost cycle),
    /// and before any new note spawning occurs.
    /// </summary>
    public void BeginNewMotif(string reason = "BeginNewMotif") {
        Debug.Log($"[CTRL] BeginNewMotif reason={reason}");
        GameFlowManager.Instance?.activeDrumTrack?.ResetMotifDrumSequencing();
        // Ensure no in-flight collectables from the prior motif can write late into tracks/visuals.
        ForceDestroyAllCollectablesInFlight(reason);

        // Reset controller-level guards/caches.
        _binExtensionSignaled.Clear();
        _allowAdvanceNextBurst.Clear();
        _loopHash.Clear();

        ResetControllerBinGuards();

        // Hard reset all tracks (loop content, bins, allocation, burst state).
        if (tracks != null)
        {
            foreach (var t in tracks)
            {
                if (!t) continue;
                t.BeginNewMotifHardClear(reason);
            }
        }

        // Hard reset visuals last (they mirror track state).
        if (noteVisualizer != null)
            noteVisualizer.BeginNewMotif_ClearAll(destroyMarkerGameObjects: true);
    }

    public void AdvanceOtherTrackCursors(InstrumentTrack leaderTrack, int by = 1)
    {
        if (tracks == null) return;
        for (int i = 0; i < tracks.Length; i++)
        {
            var t = tracks[i];
            if (!t || t == leaderTrack) continue;
            t.AdvanceBinCursor(by); // silent bin reserved; visuals omitted by design
        }
    }
    private void HandleAscensionCohortCompleted(InstrumentTrack track, int start, int end)
    {
        Debug.Log($"[CHORD][CTRLR] CohortCompleted received from track={track.name} role={track.assignedRole} window=[{start},{end})");

        // If you intend to restrict to a specific role, keep this. Otherwise remove it.
        // if (track.assignedRole != cohortTriggerRole) { Debug.Log("[CHORD][CTRLR] Ignored: not trigger role"); return; }

        var h = GameFlowManager.Instance ? GameFlowManager.Instance.harmony : null;
        if (h == null) { Debug.LogWarning("[CHORD][CTRLR] HarmonyDirector is NULL"); return; }

        // This is your “tick”: the armed cohort finished ascending on 'track'
        // 1) Optionally: small flourish / feedback hook could go here
        
        // 2) Ask HarmonyDirector to advance one chord and retune everyone
        GameFlowManager.Instance?.harmony?.AdvanceChordAndRetuneAll(1);
    }
    public void ConfigureTracksFromShips(List<ShipMusicalProfile> selectedShips)
    {
//        ShipTrackAssigner.AssignShipsToTracks(selectedShips, tracks.ToList());
        UpdateVisualizer();
    } 
    public InstrumentTrack GetAmbientContextTrack() {
        if (tracks == null || tracks.Length == 0) return null; 
        // Prefer Harmony → Groove → Bass → Lead (falls back to first that has a NoteSet)
        MusicalRole[] pref = { MusicalRole.Harmony, MusicalRole.Groove, MusicalRole.Bass, MusicalRole.Lead }; 
        foreach (var role in pref) {
            var t = tracks.FirstOrDefault(x => x != null && x.assignedRole == role && x.HasNoteSet()); if (t != null) return t;
        } 
        return tracks.FirstOrDefault(x => x != null && x.HasNoteSet()) ?? tracks[0];
    }
    public NoteSet GetGlobalContextNoteSet(){ var t = GetAmbientContextTrack(); 
        return t != null ? t.GetActiveNoteSet() : null;
        }
    public InstrumentTrack FindTrackByRole(MusicalRole role)
    {
        Debug.Log("Configured roles: " + 
                  string.Join(", ", tracks.Select(t => t.assignedRole)));
        return tracks.FirstOrDefault(t => t.assignedRole == role);
    }
    public void ApplySeedVisibility(List<InstrumentTrack> seeds)
    {
        var seedSet = new HashSet<InstrumentTrack>(seeds ?? new List<InstrumentTrack>());
        foreach (var t in tracks)
        {
            bool on = seedSet.Contains(t);
        }
        // Optionally fade unmuted ones back in over ~0.5s
    }
    private IEnumerator FadeOutMidi(MidiStreamPlayer player, float duration)
    {
        float startVolume = player.MPTK_Volume;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            player.MPTK_Volume = Mathf.Lerp(startVolume, 0f, elapsed / duration);
            yield return null;
        }

        player.MPTK_Volume = 0f;
    }
    public void ApplyPhaseSeedOutcome(MazeArchetype nextPhase, List<InstrumentTrack> seeds)
    {
        var seedSet = new HashSet<InstrumentTrack>(seeds ?? new List<InstrumentTrack>());
        foreach (var t in tracks)
        {
            if (t == null) continue;
            if (seedSet.Contains(t))
                RemixSeedForPhase(t, nextPhase);     // keep + lightly remix for the new phase
            else
                ClearTrackForNewPhase(t);            // silence + clear objects so the new star can rebuild
        }
        ResetAllCursorsAndGuards(false);
    }
    public InstrumentTrack GetTrackByRole(MusicalRole role)
    {
        foreach (var t in tracks)
            if (t.assignedRole == role) return t;
        return null;
    }
    public void UpdateVisualizer()
    {
        if (noteVisualizer == null || tracks == null) return;

        foreach (var track in tracks)
        {
            if (track == null) continue;

            int h = ComputeLoopHash(track);
            if (_loopHash.TryGetValue(track, out var prev) && prev == h)
                continue; // no loop change → no work this frame

            _loopHash[track] = h;

            // Subtractive-safe: removes stale markers (steps no longer in persistent loop),
            // then ensures all remaining loop steps are represented.
            noteVisualizer.ForceSyncMarkersToPersistentLoop(track);
        }
    }

    private static int ComputeLoopHash(InstrumentTrack t)
    {
        if (t == null) return 0;

        // Order-independent hash of loop steps (cheap + stable).
        // This only considers (stepIndex), which is enough to detect shrink/expand membership changes.
        unchecked
        {
            int h = 17;

            var loop = t.GetPersistentLoopNotes();
            if (loop == null) return h;

            foreach (var (step, _, _, _, _) in loop.OrderBy(n => n.Item1))
                h = h * 31 + step;

            return h;
        }
    }

    public int GetMaxActiveLoopMultiplier()
    {
        if (tracks == null || tracks.Length == 0) return 1;

        int maxMul = 1;
        foreach (var t in tracks)
        {
            if (t == null) continue;

            // “Committed” means “this track’s authoritative loop span,” regardless of whether
            // a particular bin is currently silent.
            maxMul = Mathf.Max(maxMul, Mathf.Max(1, t.loopMultiplier));
        }
        return maxMul;
    }
    public int GetMaxLoopMultiplier()
    {
        return tracks.Max(track => track.loopMultiplier);
    }

    /// <summary>
    /// Returns the bin-count that the UI should use for consistent, cross-track visualization.
    ///
    /// <summary>
    /// Global visual bin count used by NoteVisualizer layout.
    ///
    /// Rationale:
    /// - The UI must remain phase-aligned across tracks even when a specific track is
    ///   temporarily empty (e.g., subtractive bin expiration creating silence).
    /// - Using only "active" loop multipliers (based on persistentLoopNotes) causes the
    ///   visual width to collapse during those moments, which produces overlap and
    ///   desync between tracks.
    ///
    /// Definition:
    /// - The maximum number of bins any track has advanced to (binCursor), with
    ///   fallbacks to declared total steps and loopMultiplier.
    ///
    /// This should be stable across subtractive changes: bins can become silent, but
    /// the visual timebase should not shrink.
    /// </summary>
    public int GetGlobalVisualBins()
    {
        if (tracks == null || tracks.Length == 0) return 1;

        int maxBins = 1;
        foreach (var t in tracks)
        {
            if (t == null) continue;

            int binSize = Mathf.Max(1, t.BinSize());
            int fromSteps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(1, t.GetTotalSteps()) / (float)binSize));
            int fromMul   = Mathf.Max(1, t.loopMultiplier);
            int fromCursor = Mathf.Max(1, t.GetBinCursor());

            maxBins = Mathf.Max(maxBins, fromSteps);
            maxBins = Mathf.Max(maxBins, fromMul);
            maxBins = Mathf.Max(maxBins, fromCursor);
        }

        return maxBins;
    }
    
    public void BeginGameOverFade()
    {
        foreach (var track in tracks)
        {
            if (track == null) continue;

            var loopNotes = track.GetPersistentLoopNotes();
            for (int i = 0; i < loopNotes.Count; i++)
            {
                var (step, note, _, velocity, authoredRootMidi) = loopNotes[i];
                int longDuration = 1920; // ≈4 beats (1 bar) at 480 ticks per beat
                loopNotes[i] = (step, note, longDuration, velocity, authoredRootMidi);            }
            // Start fading out this track's MIDI stream
            if (track.midiStreamPlayer != null)
            {
                track.StartCoroutine(FadeOutMidi(track.midiStreamPlayer, 2f));
            }
        }

        
    }
    private void ClearTrackForNewPhase(InstrumentTrack t)
{
    
    // 2) Despawn/clear any lingering collectables/mined objects on this track
    //    (prevents "already perfect" and stale rot artifacts)
    if (t.spawnedCollectables != null)
    {
        for (int i = t.spawnedCollectables.Count - 1; i >= 0; i--)
        {
            var go = t.spawnedCollectables[i];
            if (go) Destroy(go);
            t.spawnedCollectables.RemoveAt(i);
        }
    }
    
    // 4) Optional: visual nudge (e.g., dim ribbon color for this track)
    // NoteVisualizer can read t.IsMuted to dim rows, if you want.
}
    private void RemixSeedForPhase(InstrumentTrack t, MazeArchetype phase) {

    // Nudge its pattern/behavior so the new phase has a recognizable seed
    var ns = t.GetActiveNoteSet();
    if (ns != null)
    {
        var (behavior, rhythm) = GetDefaultStyleForPhaseAndRole(phase, t.assignedRole);
        ns.ChangeNoteBehavior(t, behavior);
        ns.rhythmStyle = rhythm;

        // Optional: quick spice if you’re re-integrating “remix boost”
        // ns.RandomizeArpOrder();
        // ns.ShiftOctave(phase == MazeArchetype.Intensify ? +1 : 0);
    }

    // Also clear old collectables to avoid stale rot on seeds
    if (t.spawnedCollectables != null)
    {
        for (int i = t.spawnedCollectables.Count - 1; i >= 0; i--)
        {
            var go = t.spawnedCollectables[i];
            if (go) Destroy(go);
            t.spawnedCollectables.RemoveAt(i);
        }
    }
}
    private (NoteBehavior behavior, RhythmStyle rhythm) GetDefaultStyleForPhaseAndRole(MazeArchetype phase, MusicalRole role) {
    switch (phase)
    {
        case MazeArchetype.Intensify:
            return role == MusicalRole.Groove
                ? (NoteBehavior.Percussion, RhythmStyle.Dense)
                : (NoteBehavior.Lead, RhythmStyle.Syncopated);

        case MazeArchetype.Release:
            return (NoteBehavior.Drone, RhythmStyle.Sparse);

        case MazeArchetype.Evolve:
            return (NoteBehavior.Lead, RhythmStyle.Steady);

        case MazeArchetype.Wildcard:
            return (NoteBehavior.Glitch, RhythmStyle.Triplet);

        case MazeArchetype.Pop:
            return (NoteBehavior.Harmony, RhythmStyle.Steady);

        default: // Establish
            return (NoteBehavior.Lead, RhythmStyle.Steady);
    }
}
}
