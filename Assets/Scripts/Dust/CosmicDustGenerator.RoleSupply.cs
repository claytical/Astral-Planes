using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Snapshot of one role's dust supply: how much territory is left to feed that role's PhaseStar.
/// Territory = every cell whose effective role (painted imprint role, else hidden Voronoi role)
/// is the role, minus permanent clears and Cosmic Blocks.
/// </summary>
public struct RoleDustSupply
{
    public int live;       // territory cells not permanently cleared (any state)
    public int unrevealed; // solid, still gray (Role None) — carve to reveal + bloom
    public int revealed;   // solid, colored, no flower (e.g. drained then regrown) — carve again to bloom
    public int bloomed;    // solid with a flower — star food right now
    public int held;       // drained by a star, held empty until its node dies

    /// <summary>Cells that are or can become star food without waiting on a node.</summary>
    public int Available => live - held;

    public string Describe(MusicalRole role) =>
        $"role={role} live={live} unrevealed={unrevealed} revealed={revealed} bloomed={bloomed} held={held}";
}

public partial class CosmicDustGenerator
{
    public RoleDustSupply GetRoleSupply(MusicalRole role) => ScanRoleSupply(role);

    // One pass over the imprint store. Event-driven callers only (star refresh) — not per frame.
    private RoleDustSupply ScanRoleSupply(MusicalRole role)
    {
        var s = default(RoleDustSupply);
        if (role == MusicalRole.None || _imprints == null) return s;

        foreach (var kv in _imprints)
        {
            var imp = kv.Value;
            var effectiveRole = imp.role != MusicalRole.None ? imp.role : imp.hiddenRole;
            if (effectiveRole != role) continue;

            var gp = kv.Key;
            if (_permanentClearCells.Contains(gp) || IsBlockAt(gp)) continue;
            s.live++;

            if (_heldRegrowCells.Contains(gp)) { s.held++; continue; }
            if (!TryGetCellState(gp, out var st) || st != DustCellState.Solid) continue;
            if (!TryGetDustAt(gp, out var dust) || dust == null) continue;

            if (dust.HasBloomedFlower) { s.bloomed++; continue; }
            if (dust.Role == MusicalRole.None)
            {
                s.unrevealed++;
            }
            else if (dust.Role == role)
            {
                s.revealed++;
            }
        }
        return s;
    }
}
