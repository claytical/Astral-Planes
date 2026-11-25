using System.Collections.Generic;
using UnityEngine;

public sealed partial class StarPool
{
    // Guard against spawning the same role twice in a single Tick().
    private readonly HashSet<MusicalRole> _spawningThisFrame = new();

    private MotifProfile _cachedRolesMotif;
    private List<MusicalRole> _cachedActiveRoles;

    // A spawn that found no free cell (e.g. the maze is still growing) waits this long before
    // retrying, instead of re-scanning the grid every frame.
    private const float kSpawnRetrySeconds = 0.5f;
    private readonly Dictionary<MusicalRole, float> _spawnRetryAt = new();

    // Throttles the per-role diagnostic dump below to ~1/sec so it doesn't spam every frame.
    private float _lastDiagLogTime;

    // Roles that already got their motif-start star this motif (cleared in Initialize). The first
    // wave spawns spread across the whole maze instead of near the ship.
    private readonly HashSet<MusicalRole> _motifStartSpawned = new();
    // Each live star's spawn cell — an entering star is still off-screen, so separation uses this.
    private readonly Dictionary<MusicalRole, Vector2Int> _starSpawnCells = new();

    private PhaseStarConfig.SwimTuning SwimCfg => PhaseStarConfig.OrDefault(null, this).swim;

    // ── Tick: Star spawning ───────────────────────────────────────────────────
    // Every active role keeps a star waiting from motif start — its buds show how many flowers
    // it needs before any dust is carved. No grid scan here: a slot refills as soon as it's
    // empty and its role isn't mid-sequence, walled, or out of budget.

    private void Tick()
    {
        if (_dustGen == null) { if (_gfm == null) _gfm = GameFlowManager.Instance; _dustGen = _gfm?.dustGenerator; }
        if (_dustGen == null || _drum == null) return;
        if (_remainingEjectionsTotal <= 0) return;

        // GetActiveRoles() allocates a HashSet + List; motif role configs don't change at runtime,
        // so cache per motif instead of rebuilding every frame.
        if (!ReferenceEquals(_activeMotif, _cachedRolesMotif))
        {
            _cachedRolesMotif = _activeMotif;
            _cachedActiveRoles = _activeMotif != null ? _activeMotif.GetActiveRoles() : null;
        }
        var roles = _cachedActiveRoles;
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
            // every role gets a star so the player chooses which roles spend the
            // nodesPerStar total. Leftover stars despawn when the budget hits zero.

            bool mineBlocked = role == _lastEjectedRole && (_mineNodePending || HasUnresolvedMineNodeSequence());

            bool slotEmpty = !_activeStars.TryGetValue(role, out var live) || live == null;
            bool alreadySpawning = _spawningThisFrame.Contains(role);
            // The role walled by the last completed ring is all Cosmic Blocks — never star food.
            bool walled = _dustGen.BlockedRole != MusicalRole.None && _dustGen.BlockedRole == role;
            bool coolingDown = _spawnRetryAt.TryGetValue(role, out float retryAt) && Time.time < retryAt;
            bool canSpawn = !mineBlocked && slotEmpty && !alreadySpawning && !walled && !coolingDown;

            if (logDiagThisTick)
                Debug.Log($"[StarPool] Tick diag role={role} lastEjected={_lastEjectedRole} mineBlocked={mineBlocked} minePending={_mineNodePending} unresolved={HasUnresolvedMineNodeSequence()} slotEmpty={slotEmpty} alreadySpawning={alreadySpawning} walled={walled} canSpawn={canSpawn} remainingEjections={_remainingEjectionsTotal}");

            if (canSpawn)
            {
                if (GameFlowManager.VerboseLogging) Debug.Log($"[StarPool] Tick spawn role={role} minePending={_mineNodePending} remainingEjections={_remainingEjectionsTotal} flowers={_dustGen.FlowerCount(role)}");
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
        => !_mineNodePending && !HasUnresolvedMineNodeSequence()
           && (isSuperNode || _remainingEjectionsTotal > 0);

    // ── Star spawning ─────────────────────────────────────────────────────────

    private Vector2Int PickSpawnCellAvoidingDust(MusicalRole role, bool motifStartWave)
    {
        int w = _drum.GetSpawnGridWidth();
        int h = _drum.GetSpawnGridHeight();
        var candidates = new List<Vector2Int>(w * h);
        for (int x = 0; x < w; x++)
        for (int y = 0; y < h; y++)
            if (_drum.IsSpawnCellAvailable(x, y))
                candidates.Add(new Vector2Int(x, y));
        if (candidates.Count == 0) return new Vector2Int(-1, -1);

        candidates = FilterReachableWithRoom(candidates, w, h);
        candidates = FilterBySeparation(candidates, role);

        // Motif start: nothing is carved yet, so the near-ship bias would line every star up
        // around the ship. Spread them across the maze instead.
        if (motifStartWave)
            return candidates[UnityEngine.Random.Range(0, candidates.Count)];

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

    private static readonly Vector2Int[] kSpawnNeighbours4 =
    {
        new(1, 0), new(-1, 0), new(0, 1), new(0, -1)
    };

    // Keeps candidates a ship can eventually reach (carving dust is fine; Cosmic Blocks and the
    // sealed vault shell are not) that also sit in an open pocket big enough to swim in.
    // Falls back to reachable-only, then to everything, so a spawn is never refused here.
    private List<Vector2Int> FilterReachableWithRoom(List<Vector2Int> candidates, int w, int h)
    {
        if (w <= 0 || h <= 0 || candidates.Count <= 1) return candidates;
        int n = w * h;

        // 1) Ship reachability through anything but impassable walls.
        bool[] reach = null;
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var vehicles = _gfm?.GetVehicles();
        if (vehicles != null && vehicles.Count > 0 && _dustGen != null)
        {
            reach = new bool[n];
            var q = new Queue<Vector2Int>();
            foreach (var v in vehicles)
            {
                if (v == null) continue;
                var c = _drum.WorldToGridPosition(v.transform.position);
                c.x = Mathf.Clamp(c.x, 0, w - 1); c.y = Mathf.Clamp(c.y, 0, h - 1);
                if (reach[c.y * w + c.x]) continue;
                reach[c.y * w + c.x] = true;
                q.Enqueue(c);
            }
            while (q.Count > 0)
            {
                var c = q.Dequeue();
                foreach (var d in kSpawnNeighbours4)
                {
                    var nb = c + d;
                    if (nb.x < 0 || nb.y < 0 || nb.x >= w || nb.y >= h) continue;
                    int i = nb.y * w + nb.x;
                    if (reach[i] || _dustGen.IsStarImpassable(nb)) continue;
                    reach[i] = true;
                    q.Enqueue(nb);
                }
            }
        }

        // 2) Open-pocket size: connected dust-free cells (DrumTrack.HasDustAt counts blocks).
        int minRoom = Mathf.Max(1, SwimCfg.minSpawnOpenCells);
        var compId = new int[n];
        var compSize = new List<int> { 0 }; // id 0 = unlabelled
        var cq = new Queue<Vector2Int>();
        int RoomOf(Vector2Int start)
        {
            int si = start.y * w + start.x;
            if (compId[si] != 0) return compSize[compId[si]];
            int id = compSize.Count;
            compSize.Add(0);
            compId[si] = id;
            cq.Enqueue(start);
            int size = 0;
            while (cq.Count > 0)
            {
                var c = cq.Dequeue();
                size++;
                foreach (var d in kSpawnNeighbours4)
                {
                    var nb = c + d;
                    if (nb.x < 0 || nb.y < 0 || nb.x >= w || nb.y >= h) continue;
                    int i = nb.y * w + nb.x;
                    if (compId[i] != 0 || _drum.HasDustAt(nb)) continue;
                    compId[i] = id;
                    cq.Enqueue(nb);
                }
            }
            compSize[id] = size;
            return size;
        }

        var reachable = new List<Vector2Int>(candidates.Count);
        var roomy = new List<Vector2Int>(candidates.Count);
        foreach (var c in candidates)
        {
            if (reach != null && !reach[c.y * w + c.x]) continue;
            reachable.Add(c);
            if (RoomOf(c) >= minRoom) roomy.Add(c);
        }

        if (roomy.Count > 0) return roomy;
        if (reachable.Count > 0) return reachable;
        return candidates;
    }

    // Drops candidates within minStarSeparationCells of another live star. If that leaves none,
    // keeps the single candidate farthest from every other star.
    private List<Vector2Int> FilterBySeparation(List<Vector2Int> candidates, MusicalRole role)
    {
        float minSep = SwimCfg.minStarSeparationCells;
        if (minSep <= 0f) return candidates;

        var others = new List<Vector2Int>(4);
        foreach (var kv in _activeStars)
        {
            if (kv.Key == role || kv.Value == null) continue;
            var m = kv.Value.Motion;
            if (m != null && m.IsEntering && _starSpawnCells.TryGetValue(kv.Key, out var spawnCell))
                others.Add(spawnCell);
            else
                others.Add(_drum.WorldToGridPosition(kv.Value.transform.position));
        }
        if (others.Count == 0) return candidates;

        float minSq = minSep * minSep;
        var kept = new List<Vector2Int>(candidates.Count);
        Vector2Int farthest = candidates[0];
        float farthestSq = -1f;
        foreach (var c in candidates)
        {
            float nearestSq = float.MaxValue;
            foreach (var o in others)
                nearestSq = Mathf.Min(nearestSq, (c - o).sqrMagnitude);
            if (nearestSq >= minSq) kept.Add(c);
            if (nearestSq > farthestSq) { farthestSq = nearestSq; farthest = c; }
        }
        if (kept.Count == 0) kept.Add(farthest);
        return kept;
    }

    // Just outside the nearest left/right/top edge of the play area: the star swims in from
    // there (kinematic, through walls). Never the bottom — that's the NoteVisualizer band
    // below the line of ascension. Null = no entrance (appear in place).
    private Vector2? EntranceStartFor(Vector2 target)
    {
        if (!SwimCfg.entranceEnabled) return null;
        if (!_drum.TryGetPlayAreaWorld(out var area) || area.width <= 0f || area.height <= 0f) return null;

        float outside = SwimCfg.entranceStartOutsideCells * Mathf.Max(0.01f, _drum.GetCellWorldSize());
        float dL = target.x - area.left, dR = area.right - target.x, dT = area.top - target.y;
        if (dL <= dR && dL <= dT) return new Vector2(area.left - outside, target.y);
        if (dR <= dT)             return new Vector2(area.right + outside, target.y);
        return new Vector2(target.x, area.top + outside);
    }

    private void SpawnStarForRole(MusicalRole role)
    {
        if (_drum == null || !_drum.phaseStarPrefab)
        {
            Debug.LogError($"[StarPool] Cannot spawn Star for {role} — drum or prefab missing.");
            return;
        }

        // Pick a dust-free grid cell for the Star's starting position.
        bool motifStartWave = !_motifStartSpawned.Contains(role);
        Vector2Int cell = PickSpawnCellAvoidingDust(role, motifStartWave);
        if (cell.x < 0)
        {
            if (!_spawnRetryAt.ContainsKey(role))
                Debug.LogWarning($"[StarPool] No available dust-free grid cell for {role} Star; retrying every {kSpawnRetrySeconds}s.");
            _spawnRetryAt[role] = Time.time + kSpawnRetrySeconds;
            return;
        }
        _spawnRetryAt.Remove(role);

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
        star.EnterInMaze(targetWorldPos, EntranceStartFor(targetWorldPos));

        _activeStars[role] = star;
        _starSpawnCells[role] = cell;
        _motifStartSpawned.Add(role);
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
