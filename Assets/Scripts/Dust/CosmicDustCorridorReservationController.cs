using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Claims a persistent lane of cells between each vehicle's spawn cell and the phase's objective
/// (the active PhaseStar cell) so runtime dust growth (GravityVoidController's void rings, regrow)
/// can never fill it in — at least one guaranteed route stays open for the whole motif. Recomputed
/// once per phase/motif start; cells that leave the corridor are released and regrow is requested
/// on them, the same diff-then-release pattern CosmicDustVehicleReservationController uses for its
/// moving keep-clear pockets.
/// </summary>
public sealed class CosmicDustCorridorReservationController
{
    private readonly Func<int> _drumsWidth;
    private readonly Func<int> _drumsHeight;
    private readonly Func<DustClaimManager> _dustClaims;
    private readonly Func<Vector2Int, bool> _isPermanentClearCell;
    private readonly Action<Vector2Int> _requestRegrowCellAt;

    private const string kClaimOwner = "Corridor";
    private readonly HashSet<Vector2Int> _currentCorridorCells = new HashSet<Vector2Int>();

    public CosmicDustCorridorReservationController(
        Func<int> drumsWidth,
        Func<int> drumsHeight,
        Func<DustClaimManager> dustClaims,
        Func<Vector2Int, bool> isPermanentClearCell,
        Action<Vector2Int> requestRegrowCellAt)
    {
        _drumsWidth = drumsWidth;
        _drumsHeight = drumsHeight;
        _dustClaims = dustClaims;
        _isPermanentClearCell = isPermanentClearCell;
        _requestRegrowCellAt = requestRegrowCellAt;
    }

    // fromCells: one anchor per vehicle (spawn/current cell). toCell: the phase objective
    // (PhaseStar) cell. Builds the union of every fromCell->toCell lane in one pass so multiple
    // vehicles (split-screen/co-op) each get a guaranteed route without clobbering each other.
    public void SetCorridors(IReadOnlyList<Vector2Int> fromCells, Vector2Int toCell, int radiusCells)
    {
        SetCorridors(fromCells, new[] { toCell }, radiusCells);
    }

    // Same as above, but unions a lane from every fromCell to every toCell — used to keep
    // several destinations (e.g. multiple path-choice exits) simultaneously reachable.
    public void SetCorridors(IReadOnlyList<Vector2Int> fromCells, IReadOnlyList<Vector2Int> toCells, int radiusCells)
    {
        int w = _drumsWidth();
        int h = _drumsHeight();
        if (w <= 0 || h <= 0 || fromCells == null || fromCells.Count == 0 || toCells == null || toCells.Count == 0) return;

        int r = Mathf.Max(1, radiusCells);
        var next = new HashSet<Vector2Int>();
        for (int i = 0; i < fromCells.Count; i++)
            for (int j = 0; j < toCells.Count; j++)
                foreach (var p in BresenhamLine(fromCells[i], toCells[j]))
                    DilateInto(next, p, r, w, h);

        var dustClaims = _dustClaims();
        if (dustClaims == null) { _currentCorridorCells.Clear(); return; }

        // Release cells that left the corridor and let them regrow again.
        foreach (var cell in _currentCorridorCells)
        {
            if (next.Contains(cell)) continue;
            dustClaims.ReleaseCell(kClaimOwner, cell, DustClaimType.CorridorReserved);
            if (!_isPermanentClearCell(cell)) _requestRegrowCellAt(cell);
        }

        // Claim (or refresh) every cell in the new corridor.
        foreach (var cell in next)
            dustClaims.ClaimCell(kClaimOwner, cell, DustClaimType.CorridorReserved, seconds: -1f, refresh: true);

        _currentCorridorCells.Clear();
        _currentCorridorCells.UnionWith(next);
    }

    public void ReleaseCorridors()
    {
        var dustClaims = _dustClaims();
        foreach (var cell in _currentCorridorCells)
        {
            dustClaims?.ReleaseCell(kClaimOwner, cell, DustClaimType.CorridorReserved);
            if (!_isPermanentClearCell(cell)) _requestRegrowCellAt(cell);
        }
        _currentCorridorCells.Clear();
    }

    private static void DilateInto(HashSet<Vector2Int> result, Vector2Int center, int r, int w, int h)
    {
        for (int dx = -r; dx <= r; dx++)
        for (int dy = -r; dy <= r; dy++)
        {
            if (dx * dx + dy * dy > r * r) continue;
            int x = center.x + dx, y = center.y + dy;
            if ((uint)x >= (uint)w || (uint)y >= (uint)h) continue;
            result.Add(new Vector2Int(x, y));
        }
    }

    // Standard integer Bresenham line between two grid cells (inclusive of both endpoints).
    private static IEnumerable<Vector2Int> BresenhamLine(Vector2Int a, Vector2Int b)
    {
        int x0 = a.x, y0 = a.y, x1 = b.x, y1 = b.y;
        int dx = Mathf.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Mathf.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        int x = x0, y = y0;
        while (true)
        {
            yield return new Vector2Int(x, y);
            if (x == x1 && y == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x += sx; }
            if (e2 <= dx) { err += dx; y += sy; }
        }
    }
}
