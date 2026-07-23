using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System;
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
    // ── Serialized ────────────────────────────────────────────────────────────
    [Tooltip("How many seconds after ejection before the exiting Star GameObject is destroyed.")]
    [SerializeField, Min(0f)] private float starExitDuration = 0.4f;

    // ── Runtime refs (set by Initialize) ─────────────────────────────────────
    private GameFlowManager _gfm;
    private DrumTrack _drum;
    private MotifProfile _activeMotif;
    private PhaseStarBehaviorProfile _behaviorProfile;
    private InstrumentTrack[] _tracks;
    private CosmicDustGenerator _dustGen;

    // ── Phase plan ────────────────────────────────────────────────────────────
    // Total MineNode/SuperNode ejections still needed this motif (role-agnostic).
    // The player drives which roles eject by carving role-colored dust.
    private int _remainingEjectionsTotal;
    // Nodes the Vehicle successfully captured (burst had notes placed into the loop) this motif.
    private int _nodesCapturedThisMotif;
    public int NodesCapturedThisMotif => _nodesCapturedThisMotif;

    // ── Active Stars ──────────────────────────────────────────────────────────
    // At most one live Star per role.
    private readonly Dictionary<MusicalRole, PhaseStar> _activeStars = new();

    // Guard against spawning the same role twice in a single Tick().
    private readonly HashSet<MusicalRole> _spawningThisFrame = new();

    // Stars that are paused while a sibling's MineNode is active.
    private readonly List<PhaseStar> _pausedStars = new();

    // The most recent ejecting Star — kept alive briefly for its exit animation.
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
    
    // ── Phase plan ────────────────────────────────────────────────────────────

    private void BuildPhasePlan()
    {
        _remainingEjectionsTotal = _activeMotif?.nodesPerStar ?? 1;
        _nodesCapturedThisMotif = 0;
        if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] BuildPhasePlan: total ejections={_remainingEjectionsTotal} roles=[{string.Join(",", _activeMotif?.GetActiveRoles() ?? new System.Collections.Generic.List<MusicalRole>())}]");
    }

    // ── Tick: reactive Star spawning ──────────────────────────────────────────

    private void Tick()
    {
        if (_dustGen == null) { if (_gfm == null) _gfm = GameFlowManager.Instance; _dustGen = _gfm?.dustGenerator; }
        if (_dustGen == null || _drum == null) return;
        if (_remainingEjectionsTotal <= 0) return;

        var roles = _activeMotif?.GetActiveRoles();
        if (roles == null || roles.Count == 0) return;

        foreach (var role in roles)
        {
            // Budget is committed at EJECT time (CanStarCommitEjection), not at spawn —
            // every role with dust gets a star so the player chooses which roles spend the
            // nodesPerStar total. Leftover stars despawn when the budget hits zero.

            // Only block this role's slot if its own MineNode sequence is pending.
            if (role == _lastEjectedRole && (_mineNodePending || HasUnresolvedMineNodeSequence())) continue;

            bool hasKey = _activeStars.ContainsKey(role);
            bool notNull = hasKey && _activeStars[role] != null;
            bool slotEmpty = !hasKey || !notNull;
            bool alreadySpawning = _spawningThisFrame.Contains(role);
            bool hasDust = _dustGen.HasAnyDustWithRole(role);
            bool canSpawn = slotEmpty && !alreadySpawning && hasDust;

            if (canSpawn)
            {
                if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] Tick spawn-check role={role} minePending={_mineNodePending} unresolved={HasUnresolvedMineNodeSequence()} slotEmpty={slotEmpty} alreadySpawning={alreadySpawning} hasDust={hasDust} remainingEjections={_remainingEjectionsTotal}");
                _spawningThisFrame.Add(role);
                SpawnStarForRole(role);
            }
        }
    }

    // Stars query this at poke time — budget is committed at ejection, not spawn, so every
    // role with dust keeps a star while the total number of harvests can't overshoot
    // nodesPerStar. Only ONE sequence (of any kind) may be in flight at a time: the
    // burst-identification state (_lastEjectedRole/_mineNodeResolved) is single-slot, and
    // overlapping sequences overwrite it, losing harvest decrements. Mine ejections
    // additionally need unspent budget; SuperNode ejections never spend any. Expired/
    // escaped/empty-burst outcomes clear _mineNodePending without spending, so refunds
    // keep working.
    private bool CanStarCommitEjection(bool isSuperNode)
        => !_mineNodePending && (isSuperNode || _remainingEjectionsTotal > 0);

    // ── Star spawning ─────────────────────────────────────────────────────────

    private Vector2Int PickSpawnCellAvoidingDust()
    {
        int w = _drum.GetSpawnGridWidth();
        int h = _drum.GetSpawnGridHeight();
        var candidates = new List<Vector2Int>(w * h);
        for (int x = 0; x < w; x++)
        for (int y = 0; y < h; y++)
            if (_drum.IsSpawnCellAvailable(x, y))
                candidates.Add(new Vector2Int(x, y));
        if (candidates.Count == 0) return new Vector2Int(-1, -1);
        return candidates[UnityEngine.Random.Range(0, candidates.Count)];
    }

    private void SpawnStarForRole(MusicalRole role)
    {
        if (_drum == null || !_drum.phaseStarPrefab)
        {
            Debug.LogError($"[StarPool] Cannot spawn Star for {role} — drum or prefab missing.");
            return;
        }

        // Pick a dust-free grid cell for the Star's starting position.
        Vector2Int cell = PickSpawnCellAvoidingDust();
        if (cell.x < 0)
        {
            Debug.LogWarning($"[StarPool] No available dust-free grid cell for {role} Star.");
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
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var dustGen = _gfm?.dustGenerator;
        if (dustGen && _behaviorProfile) dustGen.ApplyProfile(_behaviorProfile);

        // Wire events before Initialize so subscriptions are in place.
        star.OnEjected += OnStarEjected;
        star.OnMineNodeResolved += OnStarMineNodeResolved;
        star.CanCommitEjection = (_, isSuperNode) => CanStarCommitEjection(isSuperNode);

        // Initialize and enter in-maze (already at target position, invisible, tentacles will grow).
        star.Initialize(_drum, _tracks, _behaviorProfile, _activeMotif);
        star.PreAttuneTo(role);
        star.EnterInMaze(targetWorldPos);

        _activeStars[role] = star;
        if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] _activeStars[{role}] = {star.name} (set in SpawnStarForRole, remainingTotal={_remainingEjectionsTotal})");

        // If another role's MineNode is in flight, only pause this star if its next ejection
        // would expand its bin count — same-bin stars are allowed to run concurrently.
        if (_mineNodePending && role != _lastEjectedRole)
        {
            var track = FindTrackForRole(role);
            bool wouldExpand = track == null || track.GetBinCursor() >= track.loopMultiplier;
            if (wouldExpand)
            {
                star.Pause();
                if (!_pausedStars.Contains(star)) _pausedStars.Add(star);
                if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] SpawnStarForRole: pre-paused {role} star (expansion conflict) — waiting for {_lastEjectedRole} mine sequence to resolve.");
            }
            else
            {
                if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] SpawnStarForRole: {role} star NOT pre-paused — same-bin ejection allowed concurrently.");
            }
        }

        // Free the grid cell when the star is destroyed.
        var relay = go.AddComponent<StarDestroyRelay>();
        relay.Init(cell, _drum);
    }

    // ── Star event handlers ───────────────────────────────────────────────────

    private void OnStarEjected(PhaseStar star, MusicalRole role)
    {
        if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] OnStarEjected role={role} remainingHarvests={_remainingEjectionsTotal}");

        _lastEjectingStar = star;
        _lastEjectedRole = role;
        _mineNodePending = true;
        _mineNodeResolved = false;
        _ejectedBurstWasEmpty = false;

        // Remove from active dict so the slot can be refilled.
        if (_activeStars.TryGetValue(role, out var active) && active == star)
        {
            _activeStars.Remove(role);
            if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] _activeStars.Remove({role}) — ejecting star={star.name}");
        }
        else
        {
            if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] _activeStars.Remove({role}) skipped — active={(active == null ? "null" : active.name)} star={star.name} match={active == star}");
        }

        // Pause stars that would expand their bin on next ejection — same-bin stars stay active.
        PauseExpansionConflicts(star);

        // Destroy the ejecting Star after a short exit animation.
        StartCoroutine(DestroyStarAfterDelay(star));

        // SuperNode with no shard tracks resolves synchronously inside SpawnSuperNodeCommon,
        // firing OnMineNodeResolved BEFORE OnEjected — the clear branch there sees
        // _mineNodePending=false and skips. Clear the gate now, mirroring that branch.
        if (_mineNodePending && !ReferenceEquals(star, null)
            && star.LastNodeWasSuperNode && !star.HasLiveEjectionNode)
        {
            if (GameFlowManager.VerboseLogging) Debug.Log("[StarPool] OnStarEjected: SuperNode already resolved synchronously — clearing gate now.");
            _mineNodePending = false;
            _mineNodeResolved = false;
            ResumeAll();
            CheckBridgeGate();
        }
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

    // Only pause stars whose next ejection would expand their track's bin count.
    // Stars that would stay in the same bin are left active so concurrent same-bin
    // MineNodes can coexist for distinct InstrumentTracks.
    private void PauseExpansionConflicts(PhaseStar except)
    {
        foreach (var kvp in _activeStars)
        {
            var star = kvp.Value;
            if (star == null || star == except) continue;

            var track = FindTrackForRole(kvp.Key);
            if (track != null && track.GetBinCursor() < track.loopMultiplier)
            {
                if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] PauseExpansionConflicts: {kvp.Key} star NOT paused — same-bin ejection allowed.");
                continue;
            }

            star.Pause();
            if (!_pausedStars.Contains(star)) _pausedStars.Add(star);
            if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] PauseExpansionConflicts: {kvp.Key} star paused — would expand bin.");
        }
    }

    private InstrumentTrack FindTrackForRole(MusicalRole role)
    {
        if (_tracks == null) return null;
        foreach (var t in _tracks)
            if (t != null && t.assignedRole == role) return t;
        return null;
    }

    private void ResumeAll()
    {
        foreach (var star in _pausedStars)
        {
            if (star != null) star.Resume();
        }
        _pausedStars.Clear();
        if (GameFlowManager.VerboseLogging) Debug.Log("[StarPool] Resumed all paused Stars.");
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
        // Use ReferenceEquals rather than Unity's == operator: the PhaseStar GameObject may
        // already be destroyed (DestroyStarAfterDelay runs 0.4s after ejection), but the C#
        // object remains alive in memory with valid property values. Unity's == override
        // returns false for destroyed objects, incorrectly hiding all outcome flags.
        bool hasRef       = !ReferenceEquals(star, null);
        bool wasSuperNode = hasRef && star.LastNodeWasSuperNode;
        bool wasExpired   = hasRef && star.LastNodeWasExpired;
        bool wasEscaped   = hasRef && star.LastNodeWasEscaped;
        bool wasCaptured  = hasRef && star.LastNodeWasCaptured;
        if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] MineNode resolved role={role} captured={wasCaptured} escaped={wasEscaped} expired={wasExpired} superNode={wasSuperNode} emptyBurst={_ejectedBurstWasEmpty} CIF={AnyCollectablesInFlight()}");

        if (wasCaptured)
        {
            _nodesCapturedThisMotif++;
            if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] Node captured — capturedThisMotif={_nodesCapturedThisMotif}");
        }

        // Clear the gate immediately for:
        //   empty burst, SuperNode, expired (player ignored it), or escaped (node fled successfully).
        // These outcomes do not count as harvests — _remainingEjectionsTotal is unchanged and
        // Tick() will spawn the next PhaseStar so the player can try again.
        if (_mineNodePending && (_ejectedBurstWasEmpty || wasSuperNode || wasExpired || wasEscaped))
        {
            if (wasExpired || wasEscaped)
                if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] MineNode {(wasExpired ? "expired" : "escaped")} — no harvest, spawning next star (remainingHarvests={_remainingEjectionsTotal})");

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
        if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] HandleCollectableBurstCleared track={track.assignedRole} burstId={burstId} hadNotes={hadNotes} isEjected={isEjectedTrack} mineResolved={_mineNodeResolved} isMineBurst={isMineBurst} unresolved={HasUnresolvedMineNodeSequence()}");

        // Detect the empty-burst race: SpawnCollectableBurst fires OnCollectableBurstCleared
        // synchronously with hadNotes=false BEFORE TriggerExplosion sets _mineNodeResolved.
        // Flag it so OnStarMineNodeResolved knows it's a true empty burst (not notes-in-vehicle).
        if (isEjectedTrack && !_mineNodeResolved && !hadNotes)
            _ejectedBurstWasEmpty = true;

        if (isMineBurst)
        {
            if (hadNotes)
            {
                _remainingEjectionsTotal = Mathf.Max(0, _remainingEjectionsTotal - 1);
                if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] Harvest complete — remainingHarvests={_remainingEjectionsTotal}");

                // Budget is committed at eject time, so stars for every dusty role may still
                // be live when the last harvest lands. CheckBridgeGate requires no live stars —
                // clear them out now that no further ejection could ever spend budget.
                if (_remainingEjectionsTotal == 0)
                    DespawnLeftoverStars();
            }
            else
            {
                if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] Mine burst had no notes — not counted as harvest, spawning next star");
            }
        }

        // Clear the mine-node gate immediately when the burst is placed,
        // without requiring AnyCollectablesInFlight() == false first.
        // Tick() has its own AnyCollectablesInFlight() guard that prevents
        // spawning a new star while notes are still being carried.
        if (isMineBurst)
        {
            _mineNodeResolved = false;
            _mineNodePending = false;
            if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] Mine burst cleared — _mineNodePending=false");
        }

        if (HasUnresolvedMineNodeSequence())
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

    // Explodes all remaining stars (active + paused) once the harvest budget is spent, so
    // CheckBridgeGate's no-live-stars requirement can pass. Mirrors ExplodeAndClearAll's star
    // teardown but leaves the gate/budget state alone — callers are mid-gate-clear.
    private void DespawnLeftoverStars()
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
        if (GameFlowManager.VerboseLogging) Debug.Log("[StarPool] DespawnLeftoverStars: budget spent — cleared remaining stars for bridge.");
    }

    private bool AnyCollectablesInFlight()
    {
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        return _gfm != null && _gfm.AnyCollectablesInFlightGlobal();
    }

    private bool AnyVehicleCapturedCollectablesPendingRelease()
    {
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var vehicles = _gfm?.GetVehicles();
        if (vehicles == null) return false;

        for (int i = 0; i < vehicles.Count; i++)
        {
            var vehicle = vehicles[i];
            if (vehicle != null && vehicle.HasCapturedCollectablesPendingRelease())
                return true;
        }

        return false;
    }

    private bool HasUnresolvedMineNodeSequence()
        => AnyCollectablesInFlight() || AnyVehicleCapturedCollectablesPendingRelease();

    // ── Bridge gate ───────────────────────────────────────────────────────────

    private void CheckBridgeGate()
    {
        if (_remainingEjectionsTotal > 0)
        {
            if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] CheckBridgeGate: {_remainingEjectionsTotal} ejections remaining — blocked");
            return;
        }
        if (_activeStars.Values.Any(s => s != null)) { if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] CheckBridgeGate: activeStars still live — blocked"); return; }
        if (_pausedStars.Any(s => s != null)) { if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] CheckBridgeGate: pausedStars still live — blocked"); return; }
        if (HasUnresolvedMineNodeSequence()) { if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] CheckBridgeGate: CIF — blocked"); return; }

        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var gfm = _gfm;
        if (gfm == null) return;

        // If no nodes were captured this motif and all track loops are still empty,
        // the player had no successful interaction — restart the same motif instead of bridging.
        if (_nodesCapturedThisMotif == 0 && AllTracksEmpty())
        {
            if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] CheckBridgeGate: zero captures, empty loops — restarting motif.");
            BuildPhasePlan();
            return;
        }

        if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] CheckBridgeGate: PASS remainingTotal=0 captured={_nodesCapturedThisMotif} — triggering bridge.");
        gfm.BeginMotifBridge("StarPool");
    }

    private bool AllTracksEmpty()
    {
        if (_tracks == null) return true;
        foreach (var track in _tracks)
        {
            if (track == null) continue;
            var notes = track.GetPersistentLoopNotes();
            if (notes != null && notes.Count > 0) return false;
        }
        return true;
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
