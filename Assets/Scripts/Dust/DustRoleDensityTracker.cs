using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// Tracks how many Solid cells are currently imprinted with each MusicalRole and resolves
/// which role a cell should regrow as (imprint / hidden Voronoi imprint / neighbor plurality /
/// global least-dense, in that precedence order). Counts are updated by the cell registry
/// whenever a cell's Solid state flips.
/// </summary>
public sealed class DustRoleDensityTracker
{
    private readonly DustImprintStore _imprints;
    private readonly DustGridState _gridState;
    private readonly Func<Vector2Int, bool> _hasForceGrayRegrowFlag;
    private readonly Func<Vector2Int, bool> _isCellSolid;
    private readonly Func<Vector2Int, CosmicDust> _tryGetDustAt;

    // Cell -> role that should be excluded from regrow resolution (currently never written
    // anywhere in the repo, so this exclusion branch is a permanent no-op today; carried
    // over verbatim rather than fixed/removed — see project memory for the flagged follow-up).
    private readonly Dictionary<Vector2Int, MusicalRole> _regrowExcludeRoleByCell = new Dictionary<Vector2Int, MusicalRole>(2048);

    private readonly Dictionary<MusicalRole, int> _solidCountByRole = new Dictionary<MusicalRole, int>
    {
        { MusicalRole.Bass,    0 },
        { MusicalRole.Harmony, 0 },
        { MusicalRole.Lead,    0 },
        { MusicalRole.Groove,  0 },
        { MusicalRole.Rhythm,  0 },
    };

    private List<MusicalRole> _activeRoles; // set from motif at phase start via SetActiveRoles

    public IReadOnlyList<MusicalRole> ActiveRoles => _activeRoles;

    public DustRoleDensityTracker(
        DustImprintStore imprints,
        DustGridState gridState,
        Func<Vector2Int, bool> hasForceGrayRegrowFlag,
        Func<Vector2Int, bool> isCellSolid,
        Func<Vector2Int, CosmicDust> tryGetDustAt)
    {
        _imprints = imprints;
        _gridState = gridState;
        _hasForceGrayRegrowFlag = hasForceGrayRegrowFlag;
        _isCellSolid = isCellSolid;
        _tryGetDustAt = tryGetDustAt;
    }

    public void SetActiveRoles(IReadOnlyList<MusicalRole> roles)
    {
        _activeRoles = roles != null && roles.Count > 0
            ? new List<MusicalRole>(roles)
            : new List<MusicalRole> { MusicalRole.Bass, MusicalRole.Harmony, MusicalRole.Lead, MusicalRole.Groove, MusicalRole.Rhythm };
    }

    public void RemoveExcludedRole(Vector2Int gp) => _regrowExcludeRoleByCell.Remove(gp);

    /// <summary>
    /// Call when a cell's Solid state changes so role density stays current.
    /// Pass the cell's imprinted role (MusicalRole.None = untracked).
    /// </summary>
    public void TrackRoleDensityChange(Vector2Int gp, bool becomesSolid)
    {
        MusicalRole role = MusicalRole.None;
        if (_imprints != null && _imprints.TryGetValue(gp, out var imp))
            role = imp.role;
        // Also check the live dust object in case the imprint was removed but Role was set.
        if (role == MusicalRole.None)
        {
            var d = _tryGetDustAt(gp);
            if (d != null) role = d.Role;
        }

        if (role == MusicalRole.None) return;
        if (!_solidCountByRole.ContainsKey(role)) return;

        if (becomesSolid)
            _solidCountByRole[role] = Mathf.Max(0, _solidCountByRole[role] + 1);
        else
            _solidCountByRole[role] = Mathf.Max(0, _solidCountByRole[role] - 1);
    }

    /// <summary>
    /// Returns the playable role with the fewest solid cells.
    /// Falls back to a random role if all counts are equal (avoids deterministic bias).
    /// </summary>
    private MusicalRole GetLeastDenseRole()
    {
        MusicalRole best = MusicalRole.Bass;
        int bestCount = int.MaxValue;

        // Use only motif-active roles so regrowth doesn't introduce roles the motif doesn't use.
        var roles = (_activeRoles != null && _activeRoles.Count > 0)
            ? new List<MusicalRole>(_activeRoles)
            : new List<MusicalRole> { MusicalRole.Bass, MusicalRole.Harmony, MusicalRole.Lead, MusicalRole.Groove, MusicalRole.Rhythm };

        // Shuffle order for tie-breaking
        for (int i = roles.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (roles[i], roles[j]) = (roles[j], roles[i]);
        }
        foreach (var r in roles)
        {
            int cnt = _solidCountByRole.TryGetValue(r, out var c) ? c : 0;
            if (cnt < bestCount) { bestCount = cnt; best = r; }
        }
        return best;
    }

    private MusicalRole GetLeastDenseRoleExcluding(MusicalRole excluded)
    {
        MusicalRole best = MusicalRole.None;
        int bestCount = int.MaxValue;

        // Use only motif-active roles so regrowth doesn't introduce roles the motif doesn't use.
        var roles = (_activeRoles != null && _activeRoles.Count > 0)
            ? (IReadOnlyList<MusicalRole>)_activeRoles
            : new[] { MusicalRole.Bass, MusicalRole.Harmony, MusicalRole.Lead, MusicalRole.Groove, MusicalRole.Rhythm };

        foreach (var r in roles)
        {
            if (r == excluded) continue;
            int cnt = _solidCountByRole.TryGetValue(r, out var c) ? c : 0;
            if (cnt < bestCount)
            {
                bestCount = cnt;
                best = r;
            }
            else if (cnt == bestCount && Random.value > 0.5f)
            {
                best = r; // random tie-break
            }
        }

        // If excluded was the only active role, ignore the exclusion rather than
        // returning None or a role outside the motif's active set.
        if (best == MusicalRole.None)
            best = GetLeastDenseRole();

        return best;
    }

    public int TotalSolidCount()
    {
        // _gridState.AllSolidCount tracks ALL solid cells including MusicalRole.None.
        // _solidCountByRole only tracks non-None roles, so it can't be used here
        // after the gray-start change where all initial cells have role=None.
        return _gridState.AllSolidCount;
    }

    public MusicalRole ResolveRegrowRole(Vector2Int gp)
    {
        MusicalRole excludedRole = MusicalRole.None;
        if (_regrowExcludeRoleByCell.TryGetValue(gp, out var ex))
            excludedRole = ex;

        if (_imprints != null && _imprints.TryGetValue(gp, out var existingImp))
        {
            if (existingImp.role == MusicalRole.None)
            {
                // A non-vehicle carve requested the cell stay gray: regrow gray, full stop.
                // Falling through to neighbor/density here would re-tint (and re-charge)
                // cells the player never earned, feeding stars from untouched dust.
                if (_hasForceGrayRegrowFlag(gp))
                    return MusicalRole.None;

                // Gray-start cell: consult hidden imprint before giving up.
                if (existingImp.hiddenRole != MusicalRole.None && IsRoleActive(existingImp.hiddenRole))
                    return existingImp.hiddenRole;
                // No active hidden imprint — fall through to neighbor/density logic.
            }
            else if (IsRoleActive(existingImp.role))
                return existingImp.role;
        }

        // No imprint or imprint role is inactive: fall back to neighbor plurality / least dense.
        // Force-gray cells never take a fallback role.
        if (_hasForceGrayRegrowFlag(gp))
            return MusicalRole.None;

        var neighborRole = GetPluralityNeighborRole(gp, excludedRole);
        return neighborRole != MusicalRole.None ? neighborRole : GetLeastDenseRoleExcluding(excludedRole);
    }

    /// <summary>
    /// Returns the role held by the plurality of solid imprinted neighbors within 1 cell.
    /// Ties broken by global density (least-dense wins). Returns None when no imprinted
    /// solid neighbors exist.
    /// </summary>
    private MusicalRole GetPluralityNeighborRole(Vector2Int cell, MusicalRole excluded)
    {
        if (_imprints == null) return MusicalRole.None;

        int bassC = 0, harmC = 0, leadC = 0, grooveC = 0, rhythmC = 0;
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            if (dx == 0 && dy == 0) continue;
            var n = new Vector2Int(cell.x + dx, cell.y + dy);
            if (!_isCellSolid(n)) continue;
            if (!_imprints.TryGetValue(n, out var imp)
                || imp.role == MusicalRole.None || imp.role == excluded
                || !IsRoleActive(imp.role)) continue;
            switch (imp.role)
            {
                case MusicalRole.Bass:    bassC++;    break;
                case MusicalRole.Harmony: harmC++;    break;
                case MusicalRole.Lead:    leadC++;    break;
                case MusicalRole.Groove:  grooveC++;  break;
                case MusicalRole.Rhythm:  rhythmC++;  break;
            }
        }

        int max = Mathf.Max(bassC, Mathf.Max(harmC, Mathf.Max(leadC, Mathf.Max(grooveC, rhythmC))));
        if (max == 0) return MusicalRole.None;

        // Among tied leaders, prefer the globally least-dense (secondary balance signal).
        MusicalRole best = MusicalRole.None;
        int bestDensity = int.MaxValue;
        UpdatePluralityBest(MusicalRole.Bass,    bassC,    max, ref best, ref bestDensity);
        UpdatePluralityBest(MusicalRole.Harmony, harmC,    max, ref best, ref bestDensity);
        UpdatePluralityBest(MusicalRole.Lead,    leadC,    max, ref best, ref bestDensity);
        UpdatePluralityBest(MusicalRole.Groove,  grooveC,  max, ref best, ref bestDensity);
        UpdatePluralityBest(MusicalRole.Rhythm,  rhythmC,  max, ref best, ref bestDensity);
        return best;
    }

    private void UpdatePluralityBest(
        MusicalRole role, int count, int max,
        ref MusicalRole best, ref int bestDensity)
    {
        if (count != max) return;
        int d = _solidCountByRole.TryGetValue(role, out int cv) ? cv : 0;
        if (d < bestDensity) { bestDensity = d; best = role; }
    }

    // Returns true if the role is present in the current motif's active role list.
    // When no active roles are set (fallback), all roles are considered active.
    public bool IsRoleActive(MusicalRole role)
    {
        if (_activeRoles == null || _activeRoles.Count == 0) return true;
        return _activeRoles.Contains(role);
    }
}
