using System.Collections.Generic;
using UnityEngine;

// =========================================================================
//  CosmicDustGenerator.Blocks — role-wide Cosmic Blocks
//
//  When a ring completes (CosmicBlockField.OnRingCompleted), every solid dust
//  cell of that ring's role becomes a Cosmic Block IN PLACE: the cell gets
//  CellFlags.RoleBlocked (indestructible to every clear path) and its CosmicDust
//  swells into block form (CosmicDust.BlockForm.cs, never a star target). The
//  previous role's blocks revert first, so the walls move with every ring and
//  push the player onto another dust type.
//
//  No-trap guarantee: before walling, a clearance-aware 0-1 BFS
//  (DustReachability, clearance = widest ship) runs from a ship to every other
//  ship, every live note and every queued note destination; candidate cells on
//  the cheapest route to anything that would be sealed off stay ordinary
//  carvable dust ("doorways"). While a role is blocked, its cells regrowing to
//  Solid become blocks only if that doesn't trap anything (CanBlockWithoutTrapping),
//  and note spawns only pick ship-reachable cells (IsCellReachableForShips),
//  opening a doorway as a last resort (OpenDoorwayTo).
//
//  Blocked cells stay Solid dust, so CosmicDustGenerator.HasDustAt and every
//  grid pathfinder already treat them as walls; IsBlockAt keeps its old API.
// =========================================================================
public partial class CosmicDustGenerator
{
    [Header("Cosmic Blocks (role-wide)")]
    [Tooltip("Particle start-size multiplier for a dust cell in block form (old Cosmic Block prefab 2 ÷ Cosmic Potential 0.5 = 4).")]
    [SerializeField] private float roleBlockParticleSizeMul = 4f;
    [SerializeField] private bool logRoleBlocks;

    private MusicalRole _blockedRole = MusicalRole.None;
    private readonly HashSet<Vector2Int> _roleBlockedCells = new HashSet<Vector2Int>();
    // Cells of the blocked role left carvable (doorways, ship overlap, trap-avoiding regrows):
    // shown as inert gray passage and never star food, so the walled role has nothing to charge.
    private readonly HashSet<Vector2Int> _rolePassageCells = new HashSet<Vector2Int>();
    private readonly List<Vector2Int> _doorwayScratch = new List<Vector2Int>(64);
    private readonly DustReachability _reach = new DustReachability();
    private readonly List<Vector2Int> _reachSeeds = new List<Vector2Int>(4);
    private readonly List<Vector2Int> _reachTargets = new List<Vector2Int>(64);
    private readonly List<Vector2Int> _reachPath = new List<Vector2Int>(128);
    private readonly List<Vector2Int> _pendingSpawnScratch = new List<Vector2Int>(32);
    private int _reachCacheFrame = -1;

    private CosmicBlockField _blockField;
    private bool _blockFieldLookedUp;

    public CosmicBlockField BlockField
    {
        get
        {
            if (_blockField == null && !_blockFieldLookedUp)
            {
                _blockFieldLookedUp = true;
                _blockField = GetComponent<CosmicBlockField>();
                if (_blockField == null) _blockField = FindAnyObjectByType<CosmicBlockField>();
            }
            return _blockField;
        }
    }

    public MusicalRole BlockedRole => _blockedRole;
    public IReadOnlyCollection<Vector2Int> RoleBlockedCells => _roleBlockedCells;

    /// <summary>True when this cell is currently a Cosmic Block (role-blocked dust).</summary>
    public bool IsBlockAt(Vector2Int cell) => HasCellFlag(cell, CellFlags.RoleBlocked);

    /// <summary>
    /// Cells a Star's vine/burrow can never break: Cosmic Blocks and the record vault's standing
    /// shell — the same two guards ClearCell refuses on. Star routing treats these as walls
    /// (anything else is breakable dust).
    /// </summary>
    public bool IsStarImpassable(Vector2Int cell) =>
        HasCellFlag(cell, CellFlags.RoleBlocked) || IsSealedVaultShell(cell);

    // Bypass-proof walls: interstitial solid dust + Cosmic Blocks.
    private bool IsUnbreakable(Vector2Int cell) => HasCellFlag(cell, CellFlags.Solid | CellFlags.RoleBlocked);

    // ------------------------------------------------------------------
    // Apply / release
    // ------------------------------------------------------------------

    /// <summary>Reverts every Cosmic Block to ordinary dust.</summary>
    public void ReleaseRoleBlock()
    {
        foreach (var cell in _roleBlockedCells)
        {
            ClearCellFlag(cell, CellFlags.RoleBlocked);
            if (TryGetDustAt(cell, out var dust)) dust.SetBlockForm(false);
        }
        foreach (var cell in _rolePassageCells)
            if (TryGetDustAt(cell, out var dust)) dust.SetPassageForm(false);
        if (logRoleBlocks && (_roleBlockedCells.Count > 0 || _rolePassageCells.Count > 0))
            Debug.Log($"[ROLE_BLOCK] released {_roleBlockedCells.Count} {_blockedRole} block(s), {_rolePassageCells.Count} passage(s)", this);
        _roleBlockedCells.Clear();
        _rolePassageCells.Clear();
        _blockedRole = MusicalRole.None;
        _reachCacheFrame = -1;
    }

    /// <summary>
    /// Turns every solid dust cell of <paramref name="role"/> (revealed or still hidden) into a
    /// Cosmic Block, except the doorway cells needed to keep every ship, note and queued note
    /// destination reachable. Call ReleaseRoleBlock first.
    /// </summary>
    public void ApplyRoleBlock(MusicalRole role)
    {
        if (role == MusicalRole.None || _gridState.CellState == null) return;
        _blockedRole = role;
        // Ring completed: the role's empty cells stay empty until it is released
        // (CosmicBlockField → ResumeRoleRegrowth). ReleaseRoleBlock deliberately does NOT
        // resume — it also runs during teardown, where new regrow timers would leak.
        MarkRoleBlocked(role);

        var candidates = new HashSet<Vector2Int>();
        for (int y = 0; y < _gridState.Height; y++)
        for (int x = 0; x < _gridState.Width; x++)
        {
            if (_gridState.CellState[x, y] != DustCellState.Solid) continue;
            var gp = new Vector2Int(x, y);
            if (!IsBlockCandidateRole(gp, role)) continue;
            // A ship plowing through this cell right now would be wedged inside a wall — leave it
            // as gray passage (it can still join the wall later if it regrows while this role is blocked).
            if (IsVehicleOverlappingCell(gp)) { MakePassage(gp); continue; }
            candidates.Add(gp);
        }

        _doorwayScratch.Clear();
        int doorways = CarveDoorways(candidates, _doorwayScratch);
        foreach (var gp in _doorwayScratch) MakePassage(gp);
        foreach (var gp in candidates) BlockCell(gp);

        _reachCacheFrame = -1;
        if (logRoleBlocks)
            Debug.Log($"[ROLE_BLOCK] {role}: blocked={candidates.Count} doorway cells={doorways} passages={_rolePassageCells.Count} clearance={_reach.ClearanceCells}", this);
    }

    private bool IsBlockCandidateRole(Vector2Int gp, MusicalRole role)
    {
        if (HasCellFlag(gp, CellFlags.Solid)) return false; // interstitial walls are already walls
        if (_imprints != null && _imprints.TryGetValue(gp, out var imp) && imp.hiddenRoleIsGenericFill) return false;
        if (GetProspectiveRegrowRole(gp) == role) return true;
        // Stars target by the live dust role, so a cell whose imprint disagrees must still wall.
        return TryGetDustAt(gp, out var dust) && dust != null && dust.Role == role;
    }

    private void BlockCell(Vector2Int gp)
    {
        RevertPassage(gp);
        SetCellFlag(gp, CellFlags.RoleBlocked);
        _roleBlockedCells.Add(gp);
        _colliderSuppression.ClearSuppression(gp);   // a block is always a wall
        _carveAccumulator.Remove(gp);
        if (TryGetDustAt(gp, out var dust))
        {
            SetDustCollision(dust, true);
            dust.SetBlockForm(true, roleBlockParticleSizeMul);
        }
    }

    // An opened block becomes gray passage, not plain role dust: carvable, never star food.
    private void UnblockCell(Vector2Int gp)
    {
        if (!_roleBlockedCells.Remove(gp)) return;
        ClearCellFlag(gp, CellFlags.RoleBlocked);
        if (TryGetDustAt(gp, out var dust)) dust.SetBlockForm(false);
        MakePassage(gp);
        _reachCacheFrame = -1;
    }

    private void MakePassage(Vector2Int gp)
    {
        if (!_rolePassageCells.Add(gp)) return;
        if (TryGetDustAt(gp, out var dust)) dust.SetPassageForm(true);
    }

    private void RevertPassage(Vector2Int gp)
    {
        if (!_rolePassageCells.Remove(gp)) return;
        if (TryGetDustAt(gp, out var dust)) dust.SetPassageForm(false);
    }

    // Registry solid-transition hook (CosmicDustGenerator.cs). A cell of the blocked role that
    // regrows (or void-grows) to Solid joins the wall unless that would trap something.
    private void OnCellSolidTransitionForBlocks(Vector2Int gp, bool becameSolid)
    {
        if (!becameSolid)
        {
            // Defensive: a block should never stop being Solid (every clear path is gated),
            // but if teardown code does it directly, drop the stale flag.
            if (_roleBlockedCells.Remove(gp)) ClearCellFlag(gp, CellFlags.RoleBlocked);
            // A carved passage cell is reset by PrepareForReuse on regrow; just forget it.
            _rolePassageCells.Remove(gp);
            return;
        }
        if (_blockedRole == MusicalRole.None) return;
        if (!IsBlockCandidateRole(gp, _blockedRole)) return;
        if (!CanBlockWithoutTrapping(gp)) { MakePassage(gp); return; }
        BlockCell(gp);
        _reachCacheFrame = -1;
    }

    // ------------------------------------------------------------------
    // Reachability
    // ------------------------------------------------------------------

    private bool HardWallForShips(Vector2Int gp) =>
        IsUnbreakable(gp) && TryGetCellState(gp, out var st) && st == DustCellState.Solid;

    // Clearance from the widest active ship, same formula as PathChoiceCoordinator's lanes.
    private int ComputeShipClearanceCells()
    {
        float cellWorld = drums != null ? Mathf.Max(0.01f, drums.GetCellWorldSize()) : 1f;
        float widest = 0f;
        var vehicles = GameFlowManager.Instance != null ? GameFlowManager.Instance.GetVehicles() : null;
        if (vehicles != null)
            foreach (var v in vehicles)
                if (v != null && v.isActiveAndEnabled)
                    widest = Mathf.Max(widest, v.GetColliderBoundingRadiusWorld());
        return Mathf.Max(1, Mathf.CeilToInt(widest / cellWorld));
    }

    // Seed = first active ship; targets = the other ships, every on-screen PhaseStar, the record
    // vault's mouth, live notes and queued note destinations. Stars matter: a walled-off star can dead-end the motif.
    private bool GatherReachSeedsAndTargets()
    {
        _reachSeeds.Clear();
        _reachTargets.Clear();
        var gfm = GameFlowManager.Instance;
        if (gfm == null || drums == null) return false;

        var vehicles = gfm.GetVehicles();
        if (vehicles != null)
            foreach (var v in vehicles)
            {
                if (v == null || !v.isActiveAndEnabled) continue;
                var c = drums.WorldToGridPosition(v.transform.position);
                if (_reachSeeds.Count == 0) _reachSeeds.Add(c); else _reachTargets.Add(c);
            }
        if (_reachSeeds.Count == 0) return false;

        foreach (var star in PhaseStar.Live)
        {
            if (star == null || !star.isActiveAndEnabled || star.IsParkedOffScreen) continue;
            var sc = drums.WorldToGridPosition(star.transform.position);
            if (IsInBounds(sc)) _reachTargets.Add(sc);
        }

        // The record vault's mouth: the only way in to the record.
        AddRecordVaultReachTargets(_reachTargets);

        var tracks = gfm.controller != null ? gfm.controller.tracks : null;
        if (tracks != null)
            foreach (var t in tracks)
            {
                if (t == null) continue;
                if (t.spawnedCollectables != null)
                    foreach (var go in t.spawnedCollectables)
                    {
                        if (go == null || !go.TryGetComponent<Collectable>(out var c)) continue;
                        if (c.TryGetReachCell(out var rc)) _reachTargets.Add(rc);
                    }
                t.GetPendingSpawnCells(_pendingSpawnScratch);
                _reachTargets.AddRange(_pendingSpawnScratch);
            }
        return true;
    }

    // Leaves ship-wide doorways through `candidates` (moving those cells into `doorways`) so every
    // target stays reachable once the candidates become walls. Returns the doorway cell count.
    private int CarveDoorways(HashSet<Vector2Int> candidates, List<Vector2Int> doorways)
    {
        if (candidates.Count == 0 || !GatherReachSeedsAndTargets()) return 0;
        int rc = ComputeShipClearanceCells();
        _reach.Run(_gridState.Width, _gridState.Height, rc, _reachSeeds,
            HardWallForShips, gp => candidates.Contains(gp));

        int opened = 0;
        for (int i = 0; i < _reachTargets.Count; i++)
        {
            if (!_reach.TryGetBestNear(_reachTargets[i], out var node, out int cost) || cost <= 0) continue;
            _reach.TracePath(node, _reachPath);
            foreach (var step in _reachPath)
                _reach.ForEachInClearance(step, p => { if (candidates.Remove(p)) { doorways.Add(p); opened++; } });
        }
        return opened;
    }

    /// <summary>
    /// Would turning <paramref name="gp"/> into a wall cut any ship off from another ship, a live
    /// note or a queued note destination? (Clearance = widest ship.)
    /// </summary>
    private bool CanBlockWithoutTrapping(Vector2Int gp)
    {
        if (!GatherReachSeedsAndTargets()) return true;
        if (_reachTargets.Contains(gp)) return false;
        _reach.Run(_gridState.Width, _gridState.Height, ComputeShipClearanceCells(), _reachSeeds,
            c => c == gp || HardWallForShips(c));
        for (int i = 0; i < _reachTargets.Count; i++)
            if (!_reach.IsReachableNear(_reachTargets[i])) return false;
        _reachCacheFrame = -1;
        return true;
    }

    /// <summary>
    /// True when a ship (clearance = widest ship) can get to <paramref name="gp"/> without passing
    /// a Cosmic Block or uncarveable dust, and the cell itself isn't a block. Cached per frame, so a
    /// burst's notes all test against one search.
    /// </summary>
    public bool IsCellReachableForShips(Vector2Int gp)
    {
        if (IsBlockAt(gp)) return false;
        if (_roleBlockedCells.Count == 0) return true; // nothing has walled anything off
        if (_reachCacheFrame != Time.frameCount)
        {
            if (!GatherReachSeedsAndTargets()) return true; // no ships yet — don't constrain spawns
            _reachCacheFrame = Time.frameCount;
            _reach.Run(_gridState.Width, _gridState.Height, ComputeShipClearanceCells(), _reachSeeds, HardWallForShips);
        }
        return _reach.IsReachableNear(gp);
    }

    private readonly List<Vector2Int> _watchdogScratch = new List<Vector2Int>(16);

    /// <summary>
    /// Watchdog (polled by CosmicBlockField): stars, notes and ships keep moving after a wall goes
    /// up — and stars/notes are narrower than ships, so they can slip into pockets a ship can't
    /// follow. Anything that's become unreachable gets a ship-wide doorway through the blocks.
    /// Returns the number of doorways opened.
    /// </summary>
    public int EnsureTargetsReachable()
    {
        if (_roleBlockedCells.Count == 0 || !GatherReachSeedsAndTargets()) return 0;
        _reach.Run(_gridState.Width, _gridState.Height, ComputeShipClearanceCells(), _reachSeeds, HardWallForShips);
        _reachCacheFrame = Time.frameCount; // this run is also the frame's spawn-reachability cache

        _watchdogScratch.Clear();
        for (int i = 0; i < _reachTargets.Count; i++)
            if (!_reach.IsReachableNear(_reachTargets[i])) _watchdogScratch.Add(_reachTargets[i]);

        // Every OTHER role that can still progress must keep some reachable dust: its star only
        // charges from its own carved dust, so a role whose whole territory sits behind the wall
        // would dead-end the motif once it's the last role with bins left.
        CollectSealedRoleTerritories(_watchdogScratch);

        int opened = 0;
        for (int i = 0; i < _watchdogScratch.Count; i++)   // OpenDoorwayTo re-gathers/reruns, hence the copy
            if (OpenDoorwayTo(_watchdogScratch[i])) opened++;
        if (opened > 0) _reachCacheFrame = -1;
        return opened;
    }

    // Uses the reach run already in _reach. For each active, non-blocked role whose track still
    // has unfilled bins: if none of its solid dust is reachable, add its cell nearest a seed ship
    // as a doorway target.
    private void CollectSealedRoleTerritories(List<Vector2Int> outTargets)
    {
        var gfm = GameFlowManager.Instance;
        var tracks = gfm != null && gfm.controller != null ? gfm.controller.tracks : null;
        if (tracks == null || _reachSeeds.Count == 0) return;
        var motif = gfm.phaseTransitionManager != null ? gfm.phaseTransitionManager.currentMotif : null;
        var activeRoles = motif != null ? motif.GetActiveRoles() : null;
        var seed = _reachSeeds[0];

        foreach (var t in tracks)
        {
            if (t == null) continue;
            var role = t.assignedRole;
            if (role == MusicalRole.None || role == _blockedRole) continue;
            if (activeRoles != null && !activeRoles.Contains(role)) continue;
            if (t.GetFilledBinCount() >= Mathf.Max(1, t.maxLoopMultiplier)) continue;

            bool anyReachable = false;
            int bestDist = int.MaxValue;
            Vector2Int best = default;
            for (int y = 0; y < _gridState.Height && !anyReachable; y++)
            for (int x = 0; x < _gridState.Width; x++)
            {
                if (_gridState.CellState[x, y] != DustCellState.Solid) continue;
                var gp = new Vector2Int(x, y);
                if (IsUnbreakable(gp) || GetProspectiveRegrowRole(gp) != role) continue;
                if (_reach.IsReachableNear(gp)) { anyReachable = true; break; }
                int d = Mathf.Abs(gp.x - seed.x) + Mathf.Abs(gp.y - seed.y);
                if (d < bestDist) { bestDist = d; best = gp; }
            }
            if (!anyReachable && bestDist != int.MaxValue)
            {
                outTargets.Add(best);
                if (logRoleBlocks) Debug.Log($"[ROLE_BLOCK] {role} territory sealed — opening doorway to {best}", this);
            }
        }
    }

    /// <summary>
    /// Last resort for a note destination no ship can reach: reverts the Cosmic Blocks along the
    /// cheapest ship-wide route to it back to ordinary dust. Returns true if anything opened.
    /// </summary>
    private readonly List<Vector2Int> _openDoorwayScratch = new List<Vector2Int>(32);

    public bool OpenDoorwayTo(Vector2Int gp)
    {
        if (_roleBlockedCells.Count == 0 || !GatherReachSeedsAndTargets()) return false;
        _reach.Run(_gridState.Width, _gridState.Height, ComputeShipClearanceCells(), _reachSeeds,
            c => HasCellFlag(c, CellFlags.Solid) && TryGetCellState(c, out var st) && st == DustCellState.Solid,
            c => _roleBlockedCells.Contains(c));
        if (!_reach.TryGetBestNear(gp, out var node, out int cost) || cost <= 0) return false;

        var toOpen = _openDoorwayScratch;
        toOpen.Clear();
        _reach.TracePath(node, _reachPath);
        foreach (var step in _reachPath)
            _reach.ForEachInClearance(step, p => { if (_roleBlockedCells.Contains(p)) toOpen.Add(p); });
        toOpen.Add(gp);
        foreach (var c in toOpen) UnblockCell(c);
        if (logRoleBlocks) Debug.Log($"[ROLE_BLOCK] doorway opened to {gp}: {toOpen.Count} cell(s)", this);
        return toOpen.Count > 0;
    }
}
