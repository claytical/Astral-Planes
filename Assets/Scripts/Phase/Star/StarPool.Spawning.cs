using System.Collections.Generic;
using UnityEngine;

public sealed partial class StarPool
{
    // Guard against spawning the same role twice in a single Tick().
    private readonly HashSet<MusicalRole> _spawningThisFrame = new();

    // Throttles the per-role diagnostic dump below to ~1/sec so it doesn't spam every frame.
    private float _lastDiagLogTime;

    // ── Tick: reactive Star spawning ──────────────────────────────────────────

    private void Tick()
    {
        if (_dustGen == null) { if (_gfm == null) _gfm = GameFlowManager.Instance; _dustGen = _gfm?.dustGenerator; }
        if (_dustGen == null || _drum == null) return;
        if (_remainingEjectionsTotal <= 0) return;

        var roles = _activeMotif?.GetActiveRoles();
        if (roles == null || roles.Count == 0) return;

        // Diagnostic-only: dumps every active role's gating state on a throttled cadence,
        // independent of whether it can spawn — the success-only log below can't explain a
        // role that's stuck blocked (e.g. the pink/Lead-never-spawns report), since a blocked
        // role never reaches that line.
        bool logDiagThisTick = GameFlowManager.VerboseLogging && Time.time - _lastDiagLogTime > 1f;
        if (logDiagThisTick) _lastDiagLogTime = Time.time;

        foreach (var role in roles)
        {
            // Budget is committed at EJECT time (CanStarCommitEjection), not at spawn —
            // every role with dust gets a star so the player chooses which roles spend the
            // nodesPerStar total. Leftover stars despawn when the budget hits zero.

            bool mineBlocked = role == _lastEjectedRole && (_mineNodePending || HasUnresolvedMineNodeSequence());

            bool hasKey = _activeStars.ContainsKey(role);
            bool notNull = hasKey && _activeStars[role] != null;
            bool slotEmpty = !hasKey || !notNull;
            bool alreadySpawning = _spawningThisFrame.Contains(role);
            bool hasDust = !mineBlocked && _dustGen.HasAnyDustWithRole(role);
            bool canSpawn = !mineBlocked && slotEmpty && !alreadySpawning && hasDust;

            if (logDiagThisTick)
                Debug.Log($"[StarPool] Tick diag role={role} lastEjected={_lastEjectedRole} mineBlocked={mineBlocked} minePending={_mineNodePending} unresolved={HasUnresolvedMineNodeSequence()} slotEmpty={slotEmpty} alreadySpawning={alreadySpawning} hasDust={hasDust} canSpawn={canSpawn} remainingEjections={_remainingEjectionsTotal}");

            if (mineBlocked) continue;

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

        // Bias toward cells reachable through the player's already-carved corridors, so
        // deliberate carving pays off on average without ever guaranteeing a reachable spawn.
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var vehicles = _gfm?.GetVehicles();
        if (vehicles != null && vehicles.Count > 0)
        {
            var vehicleCells = new List<Vector2Int>(vehicles.Count);
            foreach (var v in vehicles)
                if (v != null) vehicleCells.Add(_drum.WorldToGridPosition(v.transform.position));

            if (vehicleCells.Count > 0)
            {
                float falloff = _behaviorProfile != null ? _behaviorProfile.spawnReachabilityFalloff : 0.35f;
                float floor = _behaviorProfile != null ? _behaviorProfile.spawnUnreachableFloorWeight01 : 0.05f;
                return StarSpawnReachabilityWeighter.PickWeighted(_drum, candidates, vehicleCells, falloff, floor);
            }
        }

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

        // If another role's DiscoveryTrackNode is in flight, only pause this star if its next ejection
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
}
