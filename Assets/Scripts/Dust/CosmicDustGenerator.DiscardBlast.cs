using UnityEngine;

public partial class CosmicDustGenerator
{
    /// <summary>
    /// Carves every currently-solid dust cell within radiusCells of centerGP, as a detonation
    /// blast from a discarded carried note. Reuses the standard carve/regrow pipeline so each
    /// cell's identity (revealed role, or hidden Voronoi ground-truth if still gray) is preserved
    /// exactly like every other clear source.
    /// </summary>
    public int TriggerDiscardBlast(Vector2Int centerGP, int radiusCells, float fadeSeconds = 0.15f, float regrowDelaySeconds = -1f)
    {
        if (radiusCells <= 0) return 0;
        EnsureCellGrid();

        int rSq = radiusCells * radiusCells;
        int affected = 0;
        for (int dy = -radiusCells; dy <= radiusCells; dy++)
        for (int dx = -radiusCells; dx <= radiusCells; dx++)
        {
            if (dx * dx + dy * dy > rSq) continue;
            var gp = new Vector2Int(centerGP.x + dx, centerGP.y + dy);
            if (!IsInBounds(gp)) continue;
            if (!TryGetCellState(gp, out var st) || st != DustCellState.Solid) continue;

            CarveCell(gp, fadeSeconds, scheduleRegrow: true, source: DustClearSource.CollectableDiscard, regrowDelaySeconds: regrowDelaySeconds, runPreExplode: true);
            affected++;
        }
        return affected;
    }
}
