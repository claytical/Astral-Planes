using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Manages a pool of PhaseStar husks — one slot per distinct MusicalRole in the active motif.
/// Spawns Stars reactively when role-colored dust exists and no Star for that role is live.
/// Orchestrates pause/resume across Stars during active DiscoveryTrackNode processing.
/// Owns the bridge gate — fires GameFlowManager.BeginMotifBridge when all ejections complete.
/// </summary>
[DisallowMultipleComponent]
public sealed partial class StarPool : MonoBehaviour
{
    // ── Runtime refs (set by Initialize) ─────────────────────────────────────
    private GameFlowManager _gfm;
    private DrumTrack _drum;
    private MotifProfile _activeMotif;
    private PhaseStarBehaviorProfile _behaviorProfile;
    private InstrumentTrack[] _tracks;
    private CosmicDustGenerator _dustGen;

    // ── Phase plan ────────────────────────────────────────────────────────────
    // Total DiscoveryTrackNode/SuperNode ejections still needed this motif (role-agnostic).
    // The player drives which roles eject by carving role-colored dust.
    private int _remainingEjectionsTotal;
    // Nodes the Vehicle successfully captured (burst had notes placed into the loop) this motif.
    private int _nodesCapturedThisMotif;
    public int NodesCapturedThisMotif => _nodesCapturedThisMotif;

    // ── Active Stars ──────────────────────────────────────────────────────────
    // At most one live Star per role.
    private readonly Dictionary<MusicalRole, PhaseStar> _activeStars = new();

    // Stars that are paused while a sibling's DiscoveryTrackNode is active.
    private readonly List<PhaseStar> _pausedStars = new();

    // Role of the most recent ejecting Star — used for rollback when a burst had no notes.
    private MusicalRole _lastEjectedRole = MusicalRole.None;
    // True from ejection until the burst fully clears; blocks new star spawning.
    private bool _mineNodePending;
    // Set when HandleCollectableBurstCleared clears _mineNodePending but collectables are
    // still active (manual-release path fires event before vehicle deactivates them).
    // Polled in Update() so ResumeAll/CheckBridgeGate fire once they actually clear.
    private bool _pendingGateCheck;

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    private void Update()
    {
        if (_drum == null) return;
        _spawningThisFrame.Clear();

        if (_pendingGateCheck && !_mineNodePending && !HasUnresolvedMineNodeSequence())
        {
            _pendingGateCheck = false;
            ResumeAll();
            CheckBridgeGate();
        }

        Tick();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void Initialize(
        DrumTrack drum,
        MotifProfile motif,
        PhaseStarBehaviorProfile profile,
        IEnumerable<InstrumentTrack> tracks)
    {
        _drum = drum;
        _activeMotif = motif;
        _behaviorProfile = profile;
        _tracks = tracks?.Where(t => t != null).ToArray() ?? System.Array.Empty<InstrumentTrack>();
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        _dustGen = _gfm?.dustGenerator;

        BuildPhasePlan();
        SubscribeToTracks();

        if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] Initialized — total ejections={_remainingEjectionsTotal}");
    }

    public void ExplodeAndClearAll()
    {
        foreach (var star in _activeStars.Values)
        {
            if (star == null) continue;
            var explode = star.GetComponent<Explode>();
            if (explode != null) explode.Permanent();
            else Destroy(star.gameObject);
        }
        _activeStars.Clear();

        foreach (var star in _pausedStars)
        {
            if (star == null) continue;
            var explode = star.GetComponent<Explode>();
            if (explode != null) explode.Permanent();
            else Destroy(star.gameObject);
        }
        _pausedStars.Clear();

        _lastEjectingStar = null;
        _lastEjectedRole = MusicalRole.None;
        _mineNodePending = false;
        _mineNodeResolved = false;
        _pendingGateCheck = false;
        _ejectedBurstWasEmpty = false;
        _remainingEjectionsTotal = 0;
        if (GameFlowManager.VerboseLogging) Debug.Log("[StarPool] ExplodeAndClearAll complete.");
    }

    public List<MusicalRole> GetAnyActiveStarMotifRoles()
    {
        return _activeMotif?.GetActiveRoles();
    }

    // Role the player is currently working: the ejected role while its node/burst is live,
    // else the attuned role of the most-progressed live star. None if nothing is attuned.
    // Read-only — drives the drum pressure axis (DrumTrack.BeatIntensity.cs).
    public MusicalRole GetDrivingRole()
    {
        if (_mineNodePending && _lastEjectedRole != MusicalRole.None) return _lastEjectedRole;

        MusicalRole best = MusicalRole.None;
        float bestProgress = -1f;
        foreach (var star in _activeStars.Values)
        {
            if (star == null || star.AttunedRole == MusicalRole.None) continue;
            float p = star.ZapProgress01;
            if (p > bestProgress)
            {
                bestProgress = p;
                best = star.AttunedRole;
            }
        }
        return best;
    }

    // ── Phase plan ────────────────────────────────────────────────────────────

    private void BuildPhasePlan()
    {
        _remainingEjectionsTotal = _activeMotif?.nodesPerStar ?? 1;
        _nodesCapturedThisMotif = 0;
        if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] BuildPhasePlan: total ejections={_remainingEjectionsTotal} roles=[{string.Join(",", _activeMotif?.GetActiveRoles() ?? new System.Collections.Generic.List<MusicalRole>())}]");
    }

    private void OnDisable()
    {
        UnsubscribeFromTracks();
    }
}
