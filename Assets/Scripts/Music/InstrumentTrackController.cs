using System;
using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using MidiPlayerTK;
using Random = UnityEngine.Random;
public struct TransportFrame
{
    public int barIndex;
    public int playheadBin;
    public int boundarySerial;
}
public enum NoteCommitMode { Performance, Composition }

public class InstrumentTrackController : MonoBehaviour
{
    [Header("Note Commit Mode")]
    [Tooltip("Performance: all collectables spawn at once; root-note fallback on mistimed release. Composition: collectables spawn step-by-step; collected note always placed at the release step.")]
    public NoteCommitMode noteCommitMode = NoteCommitMode.Performance;
    public InstrumentTrack[] tracks;
    public NoteVisualizer noteVisualizer;
    private readonly Dictionary<InstrumentTrack, int> _loopHash = new();
    [SerializeField] private float cohortWindowFraction = 0.5f; // e.g., lower half of the leader loop (0..16 when leader is 32)
    private bool _chordEventsSubscribed;
    private Vector2Int _gravityVoidCenterGP;
    private bool _gravityVoidHasCenterGP;
    public float lastCollectionTime { get; private set; } = -1f;
    private readonly HashSet<(InstrumentTrack track, int bin)> _binExtensionSignaled = new();
    private readonly List<Vector2Int> _vehicleCellsScratch = new();
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
// ---------------------------------------------------------------------
// SFX: Collection "Pickup Tick" (A2)
// ---------------------------------------------------------------------
    [Header("SFX: Collection (Pickup Tick)")]
    [SerializeField] private AudioSource pickupSfxSource;

    [SerializeField, Range(0f, 2f)]
    private float pickupTickVolume = 1.15f;

    [SerializeField, Range(0f, 0.15f)]
    private float pickupTickPitchJitter = 0.03f;

    [Tooltip("Fallback clip if role-specific clips are not set.")]
    [SerializeField] private AudioClip pickupTickDefault;

    [SerializeField] private AudioClip pickupTickBass;
    [SerializeField] private AudioClip pickupTickLead;
    [SerializeField] private AudioClip pickupTickHarmony;
    [SerializeField] private AudioClip pickupTickGroove;

// Optionally track current outer radius for VFX scaling.
    private int _gravityVoidCurrentOuterR;

    [Header("Gravity Void Rings")]
    [SerializeField, Min(1)] private int voidRingWidthCells = 2;
    [Header("Gravity Void → Dust Imprint")]
    [SerializeField] private float gravityVoidImprintTickSeconds = 0.05f;
    private Coroutine _gravityVoidRoutine;
    private InstrumentTrack _gravityVoidOwner;
    private Vector3 _gravityVoidCenterWorld;
    private Color _gravityVoidParticleTint;
    private Color _gravityVoidDustImprintTint;
    private float _gravityVoidDustHardness01;
    private float _gravityVoidGrowSecondsRuntime = -1f;
    private int   _gravityVoidMaxRadiusRuntime   = -1;
    // Inner radius (cells) where void ring growth begins — set to safety bubble radius so rings grow outward from the bubble perimeter.
    private int   _gravityVoidBubbleInnerR       = 0;
    private double _lastTransportDsp;
    private int _lastPlayheadBin;
// ------------------------------------------------------------
// Transport cache + guard against transient future-dated anchors
// ------------------------------------------------------------
    private bool _hasLastTransport;
    private TransportFrame _lastTransport;
    private double _lastTransportLeaderDsp; // leaderStartDspTime when the cache was written
    private GameFlowManager _gfm;
    // ---------------------------------------------------------------------
    // SFX: Commit "Placement Stinger" (A3)
    // Fired when the carried note visually lands on the loop / marker lights.
    // ---------------------------------------------------------------------
    [Header("SFX: Commit (Placement Stinger)")]
    [SerializeField] private AudioSource commitSfxSource;

    [SerializeField, Range(0f, 2f)]
    private float commitStingerVolume = 1.25f;

    [SerializeField, Range(0f, 0.15f)]
    private float commitStingerPitchJitter = 0.02f;

    [Tooltip("Fallback clip if role-specific commit clips are not set.")]
    [SerializeField] private AudioClip commitStingerDefault;

    [SerializeField] private AudioClip commitStingerBass;
    [SerializeField] private AudioClip commitStingerLead;
    [SerializeField] private AudioClip commitStingerHarmony;
    [SerializeField] private AudioClip commitStingerGroove;
    [SerializeField] private AudioClip commitStingerDrums;
    
    private float GetSecondsRemainingInCurrentBin() {
        var drum = GameFlowManager.Instance?.activeDrumTrack;
        if (drum == null) return 0f; 
        
        double start = (drum.leaderStartDspTime > 0.0) ? drum.leaderStartDspTime : drum.startDspTime; 
        
        if (start <= 0.0) return 0f; 
        
        float clipLen = drum.GetClipLengthInSeconds();
        
        if (clipLen <= 0f) return 0f;
        
        double delta = AudioSettings.dspTime - start;
        
        if (delta < 0.0) return clipLen; 
        
        double into = delta % clipLen;
        float rem = (float)(clipLen - into);
        
        return Mathf.Clamp(rem, 0f, clipLen);
    }
    /// <summary>
    /// Call this at the *visual deposit / marker light* moment, not at pickup.
    /// stepIndex is optional but useful later if you want per-step UI flashes.
    /// </summary>
    public void NotifyCommitted(InstrumentTrack track, int stepIndex)
{
    
}
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

    private void EnsurePickupSfxSource()
{
    if (pickupSfxSource != null) return;

    // Dedicated 2D one-shot source (never touches drum loop sources).
    var go = new GameObject("TrackPickupSFX");
    go.transform.SetParent(transform, worldPositionStays: false);

    pickupSfxSource = go.AddComponent<AudioSource>();
    pickupSfxSource.playOnAwake = false;
    pickupSfxSource.loop = false;
    pickupSfxSource.spatialBlend = 0f; // 2D
    pickupSfxSource.volume = 1f;       // volume scaled per PlayOneShot call
}

    private AudioClip GetPickupTickClip(MusicalRole role)
{
    switch (role)
    {
        case MusicalRole.Bass:    return pickupTickBass    != null ? pickupTickBass    : pickupTickDefault;
        case MusicalRole.Lead:    return pickupTickLead    != null ? pickupTickLead    : pickupTickDefault;
        case MusicalRole.Harmony: return pickupTickHarmony != null ? pickupTickHarmony : pickupTickDefault;
        case MusicalRole.Groove:  return pickupTickGroove  != null ? pickupTickGroove  : pickupTickDefault;
        default:                  return pickupTickDefault;
    }
}

    private void PlayPickupTick(InstrumentTrack track)
    {
        if (track == null) return;

        EnsurePickupSfxSource();
        if (pickupSfxSource == null) return;

        var clip = GetPickupTickClip(track.assignedRole);
        if (clip == null) return; // nothing configured yet

        float prevPitch = pickupSfxSource.pitch;
        if (pickupTickPitchJitter > 0f)
            pickupSfxSource.pitch = 1f + Random.Range(-pickupTickPitchJitter, pickupTickPitchJitter);

        pickupSfxSource.PlayOneShot(clip, pickupTickVolume);
        pickupSfxSource.pitch = prevPitch;
    }
    public void NotifyCollected(InstrumentTrack track)
    {
        lastCollectionTime = Time.time;
        PlayPickupTick(track);
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
        _gravityVoidGrowSecondsRuntime = -1f;
        _gravityVoidMaxRadiusRuntime   = -1;

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
            return;
        }

        // If owner changed, or routine is missing, (re)start cleanly.
        if (_gravityVoidRoutine != null)
        {
            StopCoroutine(_gravityVoidRoutine);
            _gravityVoidRoutine = null;
        }

        // Safety bubble: activate at the MineNode capture position while the void is expanding.
        // The bubble is the refuge zone; the void will grow outward from its perimeter.
        {
            var gfm = GameFlowManager.Instance;
            var star = gfm != null && gfm.activeDrumTrack != null ? gfm.activeDrumTrack._star : null;
            if (star != null)
            {
                Debug.Log($"[BUBBLE] BeginGravityVoid → activating bubble at MineNode pos={_gravityVoidCenterWorld} star={star.name}");
                star.SetGravityVoidSafetyBubbleActive(true, _gravityVoidCenterWorld);
                _gravityVoidBubbleInnerR = star.GetSafetyBubbleRadiusCells();
            }
            else
            {
                Debug.Log("[BUBBLE] BeginGravityVoid → no active PhaseStar found; bubble skipped");
                _gravityVoidBubbleInnerR = 0;
            }

            // Clear any existing dust inside the bubble refuge zone.
            if (_gravityVoidHasCenterGP && _gravityVoidBubbleInnerR > 0)
                gfm?.dustGenerator?.ClearBubbleZone(_gravityVoidCenterGP, _gravityVoidBubbleInnerR);

            // Compute max outer radius from ring formula so VFX sizes correctly.
            var motifRoles = gfm?.activeDrumTrack?._star?.GetMotifActiveRoles();
            int roleCount = (motifRoles != null && motifRoles.Count > 0) ? motifRoles.Count : 4;
            int ringsPerSubseq = Mathf.Max(1, roleCount / 2);
            int totalRings = roleCount + (Mathf.Max(1, _gravityVoidOwner.loopMultiplier) - 1) * ringsPerSubseq;
            _gravityVoidMaxRadiusRuntime = _gravityVoidBubbleInnerR + totalRings * voidRingWidthCells;
        }

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
        _gravityVoidOwner = null;
        _gravityVoidHasCenterGP = false;

        if (_gravityVoidRoutine != null)
        {
            StopCoroutine(_gravityVoidRoutine);
            _gravityVoidRoutine = null;
        }
        // Safety bubble: deactivate when the void resolves.
        // ArmNext will also call DeactivateSafetyBubble, but this ensures
        // it clears even if the star never re-arms (e.g. bridge follows immediately).
        {
            var gfm = GameFlowManager.Instance;
            var star = gfm != null && gfm.activeDrumTrack != null ? gfm.activeDrumTrack._star : null;
            if (star != null)
            {
                star.SetGravityVoidSafetyBubbleActive(false);
            }
        }
        DespawnGravityVoid();
    }
    // Canonical role order for rainbow ring cycling.
    private static readonly MusicalRole[] kVoidRoleOrder =
        { MusicalRole.Bass, MusicalRole.Harmony, MusicalRole.Lead, MusicalRole.Groove };
    
    private IEnumerator GravityVoidGrowAndImprintRoutine()
    {
        var gfm = GameFlowManager.Instance;
        var dustGen = gfm?.dustGenerator;
        if (dustGen == null) yield break;

        // Get motif-active roles (falls back to all 4 if no motif).
        var star = gfm?.activeDrumTrack?._star;
        IReadOnlyList<MusicalRole> activeRoles = star?.GetMotifActiveRoles();
        if (activeRoles == null || activeRoles.Count == 0)
            activeRoles = new[] { MusicalRole.Bass, MusicalRole.Harmony, MusicalRole.Lead, MusicalRole.Groove };

        int N = activeRoles.Count;
        int W = voidRingWidthCells;
        int ringsPerSubseq = Mathf.Max(1, N / 2);
        int totalBins = (_gravityVoidOwner != null) ? Mathf.Max(1, _gravityVoidOwner.loopMultiplier) : 1;

        int completedRings = 0;
        int lastBurstBin = -1;
        float tick = Mathf.Max(0.01f, gravityVoidImprintTickSeconds);

        while (_gravityVoidOwner != null)
        {
            SpawnOrUpdateGravityVoid(_gravityVoidCenterWorld, _gravityVoidParticleTint);

            int playheadBin = GetTransportFrame().playheadBin;

            if (playheadBin != lastBurstBin && _gravityVoidHasCenterGP)
            {
                lastBurstBin = playheadBin;

                int ringsThisBin = (playheadBin == 0) ? N : ringsPerSubseq;
                float growIn = Mathf.Max(0.05f, GetSecondsRemainingInCurrentBin());

                _vehicleCellsScratch.Clear();
                var vehicles = FindObjectsOfType<Vehicle>();
                for (int i = 0; i < vehicles.Length; i++)
                {
                    var v = vehicles[i];
                    if (v != null && v.isActiveAndEnabled && gfm?.activeDrumTrack != null)
                        _vehicleCellsScratch.Add(gfm.activeDrumTrack.WorldToGridPosition(v.transform.position));
                }

                for (int r = 0; r < ringsThisBin; r++)
                {
                    int globalRingIdx = completedRings + r;
                    int innerR = _gravityVoidBubbleInnerR + globalRingIdx * W;
                    int outerR = innerR + W;
                    var ringRole    = activeRoles[globalRingIdx % N];
                    var roleProfile = MusicalRoleProfileLibrary.GetProfile(ringRole);

                    dustGen.GrowVoidDustDiskFromGrid(
                        centerGP:                  _gravityVoidCenterGP,
                        outerRadiusCells:          outerR,
                        imprintRole:               ringRole,
                        hueRgb:                    roleProfile?.GetBaseColor()      ?? _gravityVoidDustImprintTint,
                        imprintHardness01:         roleProfile?.GetDustHardness01() ?? _gravityVoidDustHardness01,
                        energyAtCenter01:          1f,
                        falloffExp:                0.3f,
                        growInSeconds:             growIn,
                        fillWedges01To4:           4,
                        vehicleCells:              _vehicleCellsScratch,
                        vehicleNoSpawnRadiusCells: 1,
                        maxCellsThisCall:          -1,
                        innerRadiusCellsExclusive: innerR
                    );
                }

                completedRings += ringsThisBin;
                _gravityVoidCurrentOuterR = _gravityVoidBubbleInnerR + completedRings * W;
            }

            yield return new WaitForSeconds(tick);
        }

        _gravityVoidRoutine = null;
    }

    private InstrumentTrack GetTrackForRole(MusicalRole role)
    {
        if (role == MusicalRole.None) return null;
        if (tracks == null || tracks.Length == 0) return null;

        for (int i = 0; i < tracks.Length; i++)
        {
            var t = tracks[i];
            if (t != null && t.assignedRole == role)
                return t;
        }

        return null;
    }
    public void PlayDustChordPluck(
        MusicalRole role,
        float phrase01 = 0f,
        int chordSize = 4,
        int durTicks = 180,
        float vel127 = 24f)
    {
        try
        {
            if (role == MusicalRole.None)
                return;

            var track = GetTrackForRole(role);
            if (track == null)
                return;

            var harmony = GameFlowManager.Instance?.harmony;
            if (harmony == null)
                return;

            int playheadBin = GetTransportFrame().playheadBin;
            int chordIdx = track.Harmony_GetChordIndexForBin(playheadBin);
            if (chordIdx < 0)
                return;

            if (!harmony.TryGetChordAt(chordIdx, out var chord))
                return;

            if (chord.intervals == null || chord.intervals.Count == 0)
                return;

            var notes = BuildGravityVoidVoicing(
                track.assignedRole,
                chord,
                Mathf.Max(1, chordSize),
                track.lowestAllowedNote,
                track.highestAllowedNote
            );

            if (notes == null || notes.Count == 0)
                return;

            phrase01 = Mathf.Clamp01(phrase01);

            int noteIndex = Mathf.Clamp(
                Mathf.RoundToInt(phrase01 * (notes.Count - 1)),
                0,
                notes.Count - 1
            );

            int midi = notes[noteIndex];

            track.PlayNote127(midi, durTicks, vel127);
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[DUST] PlayDustChordPluck EXCEPTION: {ex}");
        }
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

    private void DespawnGravityVoid()
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

    private void SpawnOrUpdateGravityVoid(Vector3 worldPos, Color tint)
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
        if (drum == null) return default;

        double dspNow = AudioSettings.dspTime;

        // Short-circuit: if called multiple times within the same DSP sample (multiple tracks per frame),
        // return the cached result instead of recalculating.
        // Also invalidate if leaderStartDspTime advanced mid-frame (loop boundary fired after some
        // InstrumentTracks already ran Update), which would cause stale barIndex + fresh start mismatch.
        double leaderDspNow = (drum.leaderStartDspTime > 0.0) ? drum.leaderStartDspTime : drum.startDspTime;
        if (_hasLastTransport && dspNow == _lastTransportDsp && leaderDspNow == _lastTransportLeaderDsp)
            return _lastTransport;

        double start = leaderDspNow;
        if (start <= 0.0) return default;

        float clipLen = drum.GetClipLengthInSeconds();
        if (clipLen <= 0f) return default;

        // --- tolerate "start is slightly in the future" due to PlayScheduled lead time ---
        const double kFutureStartEpsilon = 0.050; // 50ms
        double delta = dspNow - start;

        if (delta < 0.0)
        {
            if (delta > -kFutureStartEpsilon)
            {
                dspNow = start;
                delta = 0.0;
            }
            else
            {
                return new TransportFrame
                {
                    barIndex = 0,
                    playheadBin = 0,
                    boundarySerial = drum.GetBoundarySerial()
                };
            }
        }

        int barIndex = Mathf.FloorToInt((float)(delta / clipLen));

        // IMPORTANT: use committed/audible bins, not visual bins.
        int leaderBins = Mathf.Max(1, drum.GetCommittedBinCount());
        int playheadBin = (leaderBins <= 1) ? 0 : (barIndex % leaderBins);

        var tf = new TransportFrame
        {
            barIndex = barIndex,
            playheadBin = playheadBin,
            boundarySerial = drum.GetBoundarySerial()
        };

        _lastTransport = tf;
        _lastTransportDsp = dspNow;
        _lastTransportLeaderDsp = leaderDspNow;
        _hasLastTransport = true;
        return tf;
    }
    /// <summary>
    /// Returns the current playhead position as an absolute step within the current leader loop.
    /// This is a continuous value (rawAbsStep) plus its floor (floorAbsStep) and total length (totalAbsSteps).
    /// Safe to call during transient transport re-wiring; returns false if timing can't be resolved.
    /// </summary>
    public bool TryGetRawPlayheadAbsStep(out double rawAbsStep, out int floorAbsStep, out int totalAbsSteps)
    {
        rawAbsStep = 0;
        floorAbsStep = 0;
        totalAbsSteps = 0;

        var drum = GameFlowManager.Instance?.activeDrumTrack;
        if (drum == null) return false;

        // NOTE: Manual release timing must be evaluated on the *leader* loop timeline.
        // If GetLoopLengthInSeconds() is a single bin/bar length (e.g., 16 steps), then
        // dividing it by leaderSteps (e.g., 64) makes stepDur 4x too small and rawAbsStep
        // advances 4x too fast, effectively shrinking your window.
        double baseLoopLen = drum.GetClipLengthInSeconds();
        if (baseLoopLen <= 0.0) return false;

        double dspNow = AudioSettings.dspTime;

        double transportStart = (drum.leaderStartDspTime > 0.0) ? drum.leaderStartDspTime : drum.startDspTime;
        if (transportStart <= 0.0) return false;

        int leaderSteps = Mathf.Max(1, drum.GetLeaderSteps());
        totalAbsSteps = leaderSteps;

        int binSize = Mathf.Max(1, drum.totalSteps);
        int leaderBins = Mathf.Max(1, Mathf.CeilToInt(leaderSteps / (float)binSize));
        double leaderLoopLen = baseLoopLen * leaderBins;

        // Effective loop boundary (leader loop)
        double loops = System.Math.Floor((dspNow - transportStart) / leaderLoopLen);
        double loopStart = transportStart + loops * leaderLoopLen;
        if (loopStart > dspNow) loopStart -= leaderLoopLen;

        double tPos = (dspNow - loopStart) % leaderLoopLen;
        if (tPos < 0) tPos += leaderLoopLen;

        // leaderLoopLen/leaderSteps == baseLoopLen/binSize
        double stepDur = leaderLoopLen / totalAbsSteps;
        rawAbsStep = tPos / stepDur;
        floorAbsStep = (int)System.Math.Floor(rawAbsStep) % totalAbsSteps;
        if (floorAbsStep < 0) floorAbsStep += totalAbsSteps;

        return true;
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
        }
    }
    private void HandleCollectableBurstCleared(InstrumentTrack track, int burstId, bool hadNotes)
    {
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
            return;
        }

        star.NotifyCollectableBurstCleared(hadNotes);
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
        
        if (tracks == null || tracks.Length == 0) return false; 
        foreach (var t in tracks) {
            if (t.IsExpansionPending) {
            }
            if (!t) continue;
            if (t.IsExpansionPending) return true; 
        } 
        return false;
    }

    private bool AnyCollectablesInFlight()
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

    private int ForceDestroyAllCollectablesInFlight(string reason)
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
        GameFlowManager.Instance?.activeDrumTrack.ResetBeatSequencingState("InstrumentTrackController/BeginNewMotif");
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

        var h = GameFlowManager.Instance ? GameFlowManager.Instance.harmony : null;
        if (h == null) { Debug.LogWarning("[CHORD][CTRLR] HarmonyDirector is NULL"); return; }

        // This is your “tick”: the armed cohort finished ascending on 'track'
        // 1) Optionally: small flourish / feedback hook could go here
        
        // 2) Ask HarmonyDirector to advance one chord and retune everyone
        GameFlowManager.Instance?.harmony?.AdvanceChordAndRetuneAll(1);
    }
    public void ConfigureTracksFromShips(List<ShipMusicalProfile> selectedShips)
    {
        UpdateVisualizer();
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
}