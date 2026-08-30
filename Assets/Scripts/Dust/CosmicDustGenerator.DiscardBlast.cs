using System.Collections.Generic;
using UnityEngine;

public partial class CosmicDustGenerator
{
    // Reused across calls to avoid a per-blast allocation. Safe as a static scratch buffer
    // since TriggerDiscardBlast is fully synchronous (no yields between collect/shuffle/split/
    // clear), so there's no reentrancy window even across same-frame multi-bomb detonations.
    private static readonly List<Vector2Int> _discardBlastScratch = new List<Vector2Int>();

    /// <summary>
    /// Carves every currently-solid dust cell within radiusCells of centerGP, as a detonation
    /// blast (from a discarded carried note, a time bomb collectable, etc). Reuses the standard
    /// carve/regrow pipeline so each cell's identity (revealed role, or hidden Voronoi
    /// ground-truth if still gray) is preserved exactly like every other clear source.
    ///
    /// permanentClearRatio01 (default 0 = fully backward compatible): a shuffled fraction of the
    /// in-radius solid cells are added to _permanentClearCells BEFORE their carve call, so they
    /// never regrow this motif (RequestRegrowCellAt, invoked synchronously from within ClearCell,
    /// checks this set and skips scheduling) — the fade/explosion visual pipeline is unaffected,
    /// only the regrow outcome differs. The remainder carve exactly as before.
    /// </summary>
    public int TriggerDiscardBlast(Vector2Int centerGP, int radiusCells, float fadeSeconds = 0.15f, float regrowDelaySeconds = -1f, DustClearSource source = DustClearSource.CollectableDiscard, float permanentClearRatio01 = 0f)
    {
        if (radiusCells <= 0) return 0;
        EnsureCellGrid();

        int rSq = radiusCells * radiusCells;
        _discardBlastScratch.Clear();
        for (int dy = -radiusCells; dy <= radiusCells; dy++)
        for (int dx = -radiusCells; dx <= radiusCells; dx++)
        {
            if (dx * dx + dy * dy > rSq) continue;
            var gp = new Vector2Int(centerGP.x + dx, centerGP.y + dy);
            if (!IsInBounds(gp)) continue;
            if (!TryGetCellState(gp, out var st) || st != DustCellState.Solid) continue;
            _discardBlastScratch.Add(gp);
        }

        // Shuffle before splitting so the permanent-clear subset doesn't clump spatially —
        // same idiom as ClaimExactCountTerritoryAndFillGeneric/ReserveUniversalGenericFill
        // (CosmicDustGenerator.MazeGeneration.cs).
        for (int s = _discardBlastScratch.Count - 1; s > 0; s--)
        {
            int r = Random.Range(0, s + 1);
            (_discardBlastScratch[s], _discardBlastScratch[r]) = (_discardBlastScratch[r], _discardBlastScratch[s]);
        }

        int permanentCount = Mathf.Clamp(
            Mathf.RoundToInt(_discardBlastScratch.Count * permanentClearRatio01),
            0, _discardBlastScratch.Count);

        for (int i = 0; i < _discardBlastScratch.Count; i++)
        {
            var gp = _discardBlastScratch[i];
            if (i < permanentCount)
                _permanentClearCells.Add(gp);

            CarveCell(gp, fadeSeconds, scheduleRegrow: true, source: source, regrowDelaySeconds: regrowDelaySeconds, runPreExplode: true);
        }
        return _discardBlastScratch.Count;
    }
}
