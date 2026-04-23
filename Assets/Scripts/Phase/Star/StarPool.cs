using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Manages a pool of PhaseStar husks — one slot per distinct MusicalRole in the active motif.
/// Spawns Stars reactively when role-colored dust exists and no Star for that role is live.
/// Orchestrates pause/resume across Stars during active MineNode processing.
/// Owns the bridge gate — fires GameFlowManager.BeginMotifBridge when all ejections complete.
/// </summary>
[DisallowMultipleComponent]
public sealed class StarPool : MonoBehaviour
{
    public static StarPool Instance { get; private set; }

    // ── Serialized ────────────────────────────────────────────────────────────
    [Tooltip("How many seconds after ejection before the exiting Star GameObject is destroyed.")]
    [SerializeField, Min(0f)] private float starExitDuration = 0.4f;

    // ── Runtime refs (set by Initialize) ─────────────────────────────────────
    private DrumTrack _drum;
    private MotifProfile _activeMotif;
    private PhaseStarBehaviorProfile _behaviorProfile;
    private InstrumentTrack[] _tracks;
    private CosmicDustGenerator _dustGen;

    // ── Phase plan ────────────────────────────────────────────────────────────
    // Total MineNode/SuperNode ejections still needed this motif (role-agnostic).
    // The player drives which roles eject by carving role-colored dust.
    private int _remainingEjectionsTotal;

    // ── Active Stars ──────────────────────────────────────────────────────────
    // At most one live Star per role.
    private readonly Dictionary<MusicalRole, PhaseStar> _activeStars = new();

    // Guard against spawning the same role twice in a single Tick().
    private readonly HashSet<MusicalRole> _spawningThisFrame = new();

    // Stars that are paused while a sibling's MineNode is active.
    private readonly List<PhaseStar> _pausedStars = new();

    // The most recent ejecting Star — routes gravity void safety bubble calls.
    private PhaseStar _lastEjectingStar;
    // Role of the most recent ejecting Star — used for rollback when a burst had no notes.
    private MusicalRole _lastEjectedRole = MusicalRole.None;
    // True from ejection until the burst fully clears; blocks new star spawning.
    private bool _mineNodePending;
    // Set to true when the MineNode fires OnResolved (Vehicle destroys it).
    // _mineNodePending only clears once this is true AND the resulting burst is cleared.
    // Prevents pre-ejection expansion bursts from prematurely releasing the gate.
    private bool _mineNodeResolved;
    // Set when HandleCollectableBurstCleared clears _mineNodePending but collectables are
    // still active (manual-release path fires event before vehicle deactivates them).
    // Polled in Update() so ResumeAll/CheckBridgeGate fire once they actually clear.
    private bool _pendingGateCheck;
    // Set when the ejected track fires OnCollectableBurstCleared with hadNotes=false BEFORE
    // _mineNodeResolved is true (empty-burst race: SpawnCollectableBurst fires synchronously
    // before TriggerExplosion). Tells OnStarMineNodeResolved it's safe to clear the gate
    // immediately. Without this flag, AnyCollectablesInFlight()=false after the vehicle
    // collects and deactivates notes would incorrectly look like an empty burst.
    private bool _ejectedBurstWasEmpty;

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    private void Awake()
    {
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        if (_drum == null) return;
        _spawningThisFrame.Clear();

        if (_pendingGateCheck && !_mineNodePending && !AnyCollectablesInFlight())
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
        _dustGen = GameFlowManager.Instance?.dustGenerator;

        BuildPhasePlan();
        SubscribeToTracks();

        Debug.Log($"[StarPool] Initialized — total ejections={_remainingEjectionsTotal}");
    }

    public void DespawnAll()
    {
        foreach (var star in _activeStars.Values)
        {
            if (star != null) Destroy(star.gameObject);
        }
        _activeStars.Clear();

        foreach (var star in _pausedStars)
        {
            if (star != null) Destroy(star.gameObject);
        }
        _pausedStars.Clear();

        _lastEjectingStar = null;
        _lastEjectedRole = MusicalRole.None;
        _mineNodePending = false;
        _mineNodeResolved = false;
        _pendingGateCheck = false;
        _ejectedBurstWasEmpty = false;
        _remainingEjectionsTotal = 0;
        Debug.Log("[StarPool] DespawnAll complete.");
    }

    // ── Safety bubble (replaces PhaseStar static) ────────────────────────────

    public static bool IsPointInsideAnySafetyBubble(Vector2 pos)
    {
        var inst = Instance;
        if (inst == null) return PhaseStar.IsPointInsideSafetyBubble(pos); // fallback

        foreach (var star in inst._activeStars.Values)
        {
            if (star != null && star.IsPointInsideMyBubble(pos)) return true;
        }
        if (inst._lastEjectingStar != null && inst._lastEjectingStar.IsPointInsideMyBubble(pos))
            return true;

        return false;
    }

    // ── Gravity void routing (ITC → last ejecting star) ──────────────────────

    public void SetGravityVoidSafetyBubbleActive(bool active, Vector3 center = default)
    {
        var target = _lastEjectingStar;
        if (target == null && _activeStars.Count > 0)
            target = _activeStars.Values.FirstOrDefault(s => s != null);
        target?.SetGravityVoidSafetyBubbleActive(active, center);
    }

    public int GetSafetyBubbleRadiusCells()
    {
        var target = _lastEjectingStar;
        if (target == null && _activeStars.Count > 0)
            target = _activeStars.Values.FirstOrDefault(s => s != null);
        if (target != null) return target.GetSafetyBubbleRadiusCells();
        return _behaviorProfile != null ? _behaviorProfile.safetyBubbleRadiusCells : 4;
    }

    public List<MusicalRole> GetAnyActiveStarMotifRoles()
    {
        return _activeMotif?.GetActiveRoles();
    }

    public bool HasAnyActiveStars() => _activeStars.Count > 0 || _pausedStars.Count > 0;

    // ── Phase plan ────────────────────────────────────────────────────────────

    private void BuildPhasePlan()
    {
        _remainingEjectionsTotal = _activeMotif?.nodesPerStar ?? 1;
        Debug.Log($"[StarPool] BuildPhasePlan: total ejections={_remainingEjectionsTotal} roles=[{string.Join(",", _activeMotif?.GetActiveRoles() ?? new System.Collections.Generic.List<MusicalRole>())}]");
    }

    // ── Tick: reactive Star spawning ──────────────────────────────────────────

    private void Tick()
    {
        if (_dustGen == null) _dustGen = GameFlowManager.Instance?.dustGenerator;
        if (_dustGen == null || _drum == null) return;
        if (_mineNodePending || AnyCollectablesInFlight()) return;
        if (_remainingEjectionsTotal <= 0) return;

        var roles = _activeMotif?.GetActiveRoles();
        if (roles == null || roles.Count == 0) return;

        string activeSnapshot = string.Join(",", _activeStars.Select(kv => $"{kv.Key}:{(kv.Value == null ? "NULL" : kv.Value.name)}"));
//        Debug.Log($"[StarPool] Tick activeStars=[{activeSnapshot}] remainingTotal={_remainingEjectionsTotal}");

        foreach (var role in roles)
        {
            bool hasKey = _activeStars.ContainsKey(role);
            bool notNull = hasKey && _activeStars[role] != null;
            if (hasKey && notNull) continue;
            Debug.Log($"[StarPool] Tick: {role} slot open (hasKey={hasKey} notNull={notNull}) — attempting spawn");
            if (_spawningThisFrame.Contains(role)) continue;
            if (!_dustGen.HasAnyDustWithRole(role)) continue;

            _spawningThisFrame.Add(role);
            SpawnStarForRole(role);
        }
    }

    // ── Star spawning ─────────────────────────────────────────────────────────

    private void SpawnStarForRole(MusicalRole role)
    {
        if (_drum == null || !_drum.phaseStarPrefab)
        {
            Debug.LogError($"[StarPool] Cannot spawn Star for {role} — drum or prefab missing.");
            return;
        }

        // Pick a grid cell for the Star's logical home.
        Vector2Int cell = _drum.GetRandomAvailableCell();
        if (cell.x < 0)
        {
            Debug.LogWarning($"[StarPool] No available grid cell for {role} Star.");
            return;
        }

        Vector2 targetWorldPos = _drum.GridToWorldPosition(cell);

        var go = Instantiate(_drum.phaseStarPrefab, (Vector3)targetWorldPos, Quaternion.identity);
        var star = go.GetComponent<PhaseStar>();
        if (star == null)
        {
            Debug.LogError("[StarPool] phaseStarPrefab is missing PhaseStar component.");
            Destroy(go);
            _drum.FreeSpawnCell(cell.x, cell.y);
            return;
        }

        // Apply dust profile.
        var dustGen = GameFlowManager.Instance?.dustGenerator;
        if (dustGen && _behaviorProfile) dustGen.ApplyProfile(_behaviorProfile);

        // Wire events before Initialize so subscriptions are in place.
        star.OnEjected += OnStarEjected;
        star.OnMineNodeResolved += OnStarMineNodeResolved;

        // Initialize and enter from off-screen.
        star.Initialize(_drum, _tracks, _behaviorProfile, _activeMotif);
        star.PreAttuneTo(role);
        star.EnterFromOffScreen(targetWorldPos);

        _activeStars[role] = star;
        Debug.Log($"[StarPool] _activeStars[{role}] = {star.name} (set in SpawnStarForRole, remainingTotal={_remainingEjectionsTotal})");

        // Free the grid cell when the star is destroyed.
        var relay = go.AddComponent<StarDestroyRelay>();
        relay.Init(cell, _drum);
    }

    // ── Star event handlers ───────────────────────────────────────────────────

    private void OnStarEjected(PhaseStar star, MusicalRole role)
    {
        int before = _remainingEjectionsTotal;
        _remainingEjectionsTotal = Mathf.Max(0, _remainingEjectionsTotal - 1);
        Debug.Log($"[StarPool] OnStarEjected role={role} remainingTotal {before}→{_remainingEjectionsTotal}");

        _lastEjectingStar = star;
        _lastEjectedRole = role;
        _mineNodePending = true;
        _mineNodeResolved = false;
        _ejectedBurstWasEmpty = false;

        // Remove from active dict so the slot can be refilled.
        if (_activeStars.TryGetValue(role, out var active) && active == star)
        {
            _activeStars.Remove(role);
            Debug.Log($"[StarPool] _activeStars.Remove({role}) — ejecting star={star.name}");
        }
        else
        {
            Debug.Log($"[StarPool] _activeStars.Remove({role}) skipped — active={(active == null ? "null" : active.name)} star={star.name} match={active == star}");
        }

        // Pause all other Stars while this MineNode is being processed.
        PauseAllExcept(star);

        // Destroy the ejecting Star after a short exit animation.
        StartCoroutine(DestroyStarAfterDelay(star));
    }

    private IEnumerator DestroyStarAfterDelay(PhaseStar star)
    {
        if (starExitDuration > 0f)
            yield return new WaitForSeconds(starExitDuration);
        if (star != null) Destroy(star.gameObject);
        if (star == _lastEjectingStar) _lastEjectingStar = null;
    }

    // ── Pause / Resume ────────────────────────────────────────────────────────

    private void PauseAllExcept(PhaseStar except)
    {
        foreach (var star in _activeStars.Values)
        {
            if (star == null || star == except) continue;
            star.Pause();
            if (!_pausedStars.Contains(star)) _pausedStars.Add(star);
        }
    }

    private void ResumeAll()
    {
        foreach (var star in _pausedStars)
        {
            if (star != null) star.Resume();
        }
        _pausedStars.Clear();
        Debug.Log("[StarPool] Resumed all paused Stars.");
    }

    // ── Collectable burst tracking ────────────────────────────────────────────

    private void SubscribeToTracks()
    {
        if (_tracks == null) return;
        foreach (var track in _tracks)
        {
            if (track == null) continue;
            track.OnCollectableBurstCleared -= HandleCollectableBurstCleared;
            track.OnCollectableBurstCleared += HandleCollectableBurstCleared;
        }
    }

    private void UnsubscribeFromTracks()
    {
        if (_tracks == null) return;
        foreach (var track in _tracks)
        {
            if (track == null) continue;
            track.OnCollectableBurstCleared -= HandleCollectableBurstCleared;
        }
    }

    private void OnStarMineNodeResolved(PhaseStar star, MusicalRole role)
    {
        _mineNodeResolved = true;
        bool wasSuperNode = star != null && star.LastNodeWasSuperNode;
        bool wasExpired   = star != null && star.LastNodeWasExpired;
        Debug.Log($"[StarPool] MineNode resolved for role={role} ejectedBurstWasEmpty={_ejectedBurstWasEmpty} wasSuperNode={wasSuperNode} wasExpired={wasExpired} CIF={AnyCollectablesInFlight()}");

        // Clear the gate immediately for a confirmed empty burst, a SuperNode (no burst spawned),
        // or an expired MineNode (player never captured it — no burst, refund the ejection slot).
        if (_mineNodePending && (_ejectedBurstWasEmpty || wasSuperNode || wasExpired))
        {
            if (wasExpired)
            {
                _remainingEjectionsTotal++;
                Debug.Log($"[StarPool] MineNode expired — ejection slot refunded (total now {_remainingEjectionsTotal})");
            }
            _mineNodeResolved = false;
            _mineNodePending = false;
            _ejectedBurstWasEmpty = false;
            ResumeAll();
            CheckBridgeGate();
        }
    }

    private void HandleCollectableBurstCleared(InstrumentTrack track, int burstId, bool hadNotes)
    {
        // Only the ejected role's track is authoritative for rollback and the mine-node gate.
        bool isEjectedTrack = track.assignedRole == _lastEjectedRole;

        // Only treat this as the MineNode's burst if OnResolved already fired.
        // Pre-ejection expansion bursts arrive before the MineNode is destroyed, so
        // _mineNodeResolved is still false when they clear.
        bool isMineBurst = isEjectedTrack && _mineNodeResolved;
        Debug.Log($"[StarPool] HandleCollectableBurstCleared track={track.assignedRole} burstId={burstId} hadNotes={hadNotes} isEjected={isEjectedTrack} mineResolved={_mineNodeResolved} isMineBurst={isMineBurst} CIF={AnyCollectablesInFlight()}");

        // Detect the empty-burst race: SpawnCollectableBurst fires OnCollectableBurstCleared
        // synchronously with hadNotes=false BEFORE TriggerExplosion sets _mineNodeResolved.
        // Flag it so OnStarMineNodeResolved knows it's a true empty burst (not notes-in-vehicle).
        if (isEjectedTrack && !_mineNodeResolved && !hadNotes)
            _ejectedBurstWasEmpty = true;

        if (isMineBurst && !hadNotes)
        {
            _remainingEjectionsTotal++;
            Debug.Log($"[StarPool] Burst rolled back — re-added ejection slot (total now {_remainingEjectionsTotal})");
        }

        // Clear the mine-node gate immediately when the burst is placed,
        // without requiring AnyCollectablesInFlight() == false first.
        // Tick() has its own AnyCollectablesInFlight() guard that prevents
        // spawning a new star while notes are still being carried.
        if (isMineBurst)
        {
            _mineNodeResolved = false;
            _mineNodePending = false;
            Debug.Log($"[StarPool] Mine burst cleared — _mineNodePending=false");
        }

        if (AnyCollectablesInFlight())
        {
            // Manual-release path fires this event while collectables are still active in the
            // vehicle. Set a flag so Update() calls ResumeAll/CheckBridgeGate once they clear.
            if (isMineBurst) _pendingGateCheck = true;
            return;
        }

        if (!_mineNodePending)
        {
            ResumeAll();
            CheckBridgeGate();
        }
    }

    private bool AnyCollectablesInFlight()
    {
        if (_tracks == null) return false;
        foreach (var t in _tracks)
        {
            if (t == null) continue;
            t.PruneSpawnedCollectables();
            if (t.spawnedCollectables == null) continue;
            foreach (var go in t.spawnedCollectables)
            {
                if (go != null && go.activeInHierarchy) return true;
            }
        }
        return false;
    }

    // ── Bridge gate ───────────────────────────────────────────────────────────

    private void CheckBridgeGate()
    {
        if (_remainingEjectionsTotal > 0)
        {
            Debug.Log($"[StarPool] CheckBridgeGate: {_remainingEjectionsTotal} ejections remaining — blocked");
            return;
        }
        if (_activeStars.Values.Any(s => s != null)) { Debug.Log($"[StarPool] CheckBridgeGate: activeStars still live — blocked"); return; }
        if (_pausedStars.Any(s => s != null)) { Debug.Log($"[StarPool] CheckBridgeGate: pausedStars still live — blocked"); return; }
        if (AnyCollectablesInFlight()) { Debug.Log($"[StarPool] CheckBridgeGate: CIF — blocked"); return; }

        var gfm = GameFlowManager.Instance;
        if (gfm == null) return;

        Debug.Log($"[StarPool] CheckBridgeGate: PASS remainingTotal=0 — triggering bridge.");
        gfm.BeginMotifBridge("StarPool");
    }

    private void OnDisable()
    {
        UnsubscribeFromTracks();
    }

    // ── Nested: destroy relay ─────────────────────────────────────────────────

    private sealed class StarDestroyRelay : MonoBehaviour
    {
        private Vector2Int _cell;
        private DrumTrack _drum;

        public void Init(Vector2Int cell, DrumTrack drum)
        {
            _cell = cell;
            _drum = drum;
        }

        private void OnDestroy()
        {
            try { _drum?.FreeSpawnCell(_cell.x, _cell.y); } catch { }
        }
    }
}
