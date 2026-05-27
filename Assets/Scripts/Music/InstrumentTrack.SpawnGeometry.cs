using UnityEngine;
using System.Collections.Generic;

public partial class InstrumentTrack
{
    private bool IsOpenOrPermanentCell(CosmicDustGenerator dustGen, Vector2Int gp) {
        if (dustGen == null) return true;
        if (dustGen.IsPermanentlyClearCell(gp)) return true;
        return !dustGen.HasDustAt(gp);
    }
    private bool HasTrapBuffer(CosmicDustGenerator dustGen, Vector2Int gp, int gridW, int gridH, int bufferCells) {
    if (bufferCells <= 0) return true;

    for (int dx = -bufferCells; dx <= bufferCells; dx++)
    {
        for (int dy = -bufferCells; dy <= bufferCells; dy++)
        {
            int x = gp.x + dx;
            int y = gp.y + dy;

            if (x < 0 || y < 0 || x >= gridW || y >= gridH)
                continue;

            if (IsOpenOrPermanentCell(dustGen, new Vector2Int(x, y)))
                return false;
        }
    }
    return true;
}

/// <summary>
/// Builds a near-to-far ring-ordered list of candidate cells that are:
/// - dust present
/// - not permanently clear
/// - free (no collectable occupant)
/// - not within trapBufferCells of open/permanent space
/// </summary>
    private List<Vector2Int> BuildTrappedCandidatesNearOrigin(
    CosmicDustGenerator dustGen,
    DrumTrack dt,
    Vector3 originWorld,
    int gridW,
    int gridH,
    int trapSearchRadiusCells,
    int trapBufferCells)
{
    var candidates = new List<Vector2Int>(256);
    if (dustGen == null || dt == null) return candidates;

    Vector2Int oc = dt.WorldToGridPosition(originWorld);

    int rMax = Mathf.Clamp(trapSearchRadiusCells, 0, Mathf.Max(gridW, gridH));
    for (int r = 0; r <= rMax; r++)
    {
        int xMin = oc.x - r;
        int xMax = oc.x + r;
        int yMin = oc.y - r;
        int yMax = oc.y + r;

        // Perimeter only (ring)
        for (int x = xMin; x <= xMax; x++)
        {
            TryAdd(x, yMin);
            TryAdd(x, yMax);
        }
        for (int y = yMin + 1; y <= yMax - 1; y++)
        {
            TryAdd(xMin, y);
            TryAdd(xMax, y);
        }
    }

    return candidates;

    void TryAdd(int x, int y)
    {
        if (x < 0 || y < 0 || x >= gridW || y >= gridH) return;

        var gp = new Vector2Int(x, y);

        if (dustGen.IsPermanentlyClearCell(gp)) return;
        if (!dustGen.HasDustAt(gp)) return;
        if (!Collectable.IsCellFreeStatic(gp)) return;
        if (!HasTrapBuffer(dustGen, gp, gridW, gridH, trapBufferCells)) return;

        candidates.Add(gp);
    }
}
}
