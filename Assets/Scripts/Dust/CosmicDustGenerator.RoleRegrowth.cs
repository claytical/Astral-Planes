using System.Collections.Generic;
using UnityEngine;

// =========================================================================
//  CosmicDustGenerator.RoleRegrowth — per-role regrowth state
//
//  Dust lifecycle rules (DUST_LIFECYCLE_AUDIT.md §0b):
//   Motif begins            → every role regrows normally (ClearMaze resets state).
//   Dust is carved          → cell empties and schedules regrowth.
//   Dust regrows with buds  → eligible for star consumption.
//   Star consumes dust      → cell empties (held by its discovery node).
//   Star becomes active     → regrowth SUSPENDED for that role (zapped enough energy:
//                             PhaseStar.ZapState readyNow). Hold passes to the ejected discovery;
//                             captured → stays suspended; escaped/expired → resumes once no
//                             other hold of the role is live.
//   Ring is completed       → remaining dust of that role becomes Cosmic Blocks; empty cells
//                             stay empty (ApplyRoleBlock → MarkRoleBlocked).
//   Role is released        → blocks become ordinary dust; empty cells resume regrowth
//                             (CosmicBlockField → ResumeRoleRegrowth).
//
//  The gate lives in RequestRegrowCellAt: a cell whose territory role isn't Normal never
//  schedules regrowth, whatever cleared it — so star-consumed and player-carved cells are
//  treated identically. Suspended empty cells simply stay Empty; ResumeRoleRegrowth re-scans
//  the role's territory and re-requests them. No separate dormant set.
// =========================================================================
public enum RoleRegrowState { Normal, Suspended, Blocked }

public partial class CosmicDustGenerator
{
    private readonly Dictionary<MusicalRole, RoleRegrowState> _roleRegrowState = new();
    // Live regrowth holds per role (a charged Star, then the discovery it ejects), and roles
    // whose discovery was captured.
    private readonly Dictionary<MusicalRole, int> _liveDiscoveries = new();
    private readonly HashSet<MusicalRole> _harvestedRoles = new();

    /// <summary>
    /// A Star of <paramref name="role"/> zapped enough energy to become active: the role's regrowth
    /// is suspended from now on. The hold passes from the Star to the discovery it ejects
    /// (PhaseStar.AcquireRegrowHold → DiscoveryTrackNode.AdoptRoleRegrowthHold).
    /// </summary>
    public void AcquireRoleRegrowthHold(MusicalRole role)
    {
        if (role == MusicalRole.None) return;
        _liveDiscoveries.TryGetValue(role, out int n);
        _liveDiscoveries[role] = n + 1;
        SuspendRoleRegrowth(role);
    }

    /// <summary>
    /// A hold ended. Captured discovery → the role stays suspended until its ring completes and is
    /// released. Escaped/expired discovery, a lost charge or a destroyed Star → once no other hold
    /// of the role is live (and none was captured), the role regrows normally again.
    /// </summary>
    public void ReleaseRoleRegrowthHold(MusicalRole role, bool captured)
    {
        if (role == MusicalRole.None) return;
        _liveDiscoveries.TryGetValue(role, out int n);
        n = Mathf.Max(0, n - 1);
        if (n == 0) _liveDiscoveries.Remove(role); else _liveDiscoveries[role] = n;

        if (captured)
        {
            _harvestedRoles.Add(role);
            SuspendRoleRegrowth(role); // no-op if already suspended/blocked
            return;
        }
        if (n == 0 && !_harvestedRoles.Contains(role) && GetRoleRegrowState(role) == RoleRegrowState.Suspended)
            ResumeRoleRegrowth(role);
    }

    // Motif begins (ClearMaze): every role regrows normally.
    private void ResetRoleRegrowth()
    {
        _roleRegrowState.Clear();
        _liveDiscoveries.Clear();
        _harvestedRoles.Clear();
    }
    private static readonly List<Vector2Int> _roleRegrowScratch = new List<Vector2Int>(256);

    public RoleRegrowState GetRoleRegrowState(MusicalRole role) =>
        _roleRegrowState.TryGetValue(role, out var s) ? s : RoleRegrowState.Normal;

    /// <summary>The role whose territory a cell belongs to: painted role, else hidden Voronoi role. Generic fill = None.</summary>
    private MusicalRole GetTerritoryRole(Vector2Int cell)
    {
        if (_imprints == null || !_imprints.TryGetValue(cell, out var imp)) return MusicalRole.None;
        if (imp.hiddenRoleIsGenericFill) return MusicalRole.None;
        return imp.role != MusicalRole.None ? imp.role : imp.hiddenRole;
    }

    /// <summary>True when the cell's territory role is Suspended or Blocked (it must stay empty).</summary>
    private bool IsRegrowthSuspended(Vector2Int cell)
    {
        if (_roleRegrowState.Count == 0) return false;
        var role = GetTerritoryRole(cell);
        return role != MusicalRole.None && GetRoleRegrowState(role) != RoleRegrowState.Normal;
    }

    /// <summary>Discovery harvested: this role's cleared cells stop regrowing (a Blocked role stays Blocked).</summary>
    public void SuspendRoleRegrowth(MusicalRole role)
    {
        if (role == MusicalRole.None) return;
        if (GetRoleRegrowState(role) == RoleRegrowState.Blocked) return;
        SetRoleStateAndHaltRegrowth(role, RoleRegrowState.Suspended);
    }

    /// <summary>Ring completed: the role is walled; its empty cells stay empty until released.</summary>
    private void MarkRoleBlocked(MusicalRole role)
    {
        if (role == MusicalRole.None) return;
        SetRoleStateAndHaltRegrowth(role, RoleRegrowState.Blocked);
    }

    /// <summary>Role released: every empty cell of its territory schedules regrowth again.</summary>
    public void ResumeRoleRegrowth(MusicalRole role)
    {
        if (role == MusicalRole.None) return;
        if (GetRoleRegrowState(role) == RoleRegrowState.Normal) return;
        _roleRegrowState.Remove(role);
        _harvestedRoles.Remove(role);

        CollectTerritoryCells(role, includePendingAndClearing: false);
        foreach (var cell in _roleRegrowScratch)
            RequestRegrowCellAt(cell, -1f, refreshIfPending: true);

        if (GameFlowManager.VerboseLogging)
            Debug.Log($"[DUST:ROLE] role={role} state=Normal resumed={_roleRegrowScratch.Count}", this);
        _roleRegrowScratch.Clear();
    }

    private void ResumeAllRoleRegrowth()
    {
        if (_roleRegrowState.Count == 0) return;
        var roles = new List<MusicalRole>(_roleRegrowState.Keys);
        foreach (var r in roles) ResumeRoleRegrowth(r);
    }

    // Cancels every not-yet-visible regrow of the role. Cells already growing in (Regrowing)
    // finish; under a wall they become blocks via OnCellSolidTransitionForBlocks.
    private void SetRoleStateAndHaltRegrowth(MusicalRole role, RoleRegrowState state)
    {
        _roleRegrowState[role] = state;

        CollectTerritoryCells(role, includePendingAndClearing: true);
        foreach (var cell in _roleRegrowScratch)
        {
            _regrow.CancelRegrow(cell);
            if (TryGetCellState(cell, out var st) && st == DustCellState.PendingRegrow)
                SetCellState(cell, DustCellState.Empty); // the step gate skips non-pending cells
        }

        if (GameFlowManager.VerboseLogging)
            Debug.Log($"[DUST:ROLE] role={role} state={state} halted={_roleRegrowScratch.Count}", this);
        _roleRegrowScratch.Clear();
    }

    // Fills _roleRegrowScratch with the role's territory cells that are Empty (plus Clearing /
    // PendingRegrow when asked). Collected first so callers never mutate while enumerating imprints.
    private void CollectTerritoryCells(MusicalRole role, bool includePendingAndClearing)
    {
        _roleRegrowScratch.Clear();
        if (_imprints == null) return;
        foreach (var kv in _imprints)
        {
            var imp = kv.Value;
            if (imp.hiddenRoleIsGenericFill) continue;
            var territory = imp.role != MusicalRole.None ? imp.role : imp.hiddenRole;
            if (territory != role) continue;
            if (!TryGetCellState(kv.Key, out var st)) continue;
            bool take = st == DustCellState.Empty
                || (includePendingAndClearing && (st == DustCellState.PendingRegrow || st == DustCellState.Clearing));
            if (take) _roleRegrowScratch.Add(kv.Key);
        }
    }
}
