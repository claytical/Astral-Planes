using System;
using UnityEngine;

public sealed class DustGridState
{
    public DustCellState[,] CellState { get; private set; }
    public GameObject[,] CellGo { get; private set; }
    public CosmicDust[,] CellDust { get; private set; }

    public int Width { get; private set; } = -1;
    public int Height { get; private set; } = -1;
    public int AllSolidCount { get; set; }
    public int RegrowingCount { get; set; }

    public void Allocate(int width, int height)
    {
        Width = width;
        Height = height;
        CellGo = new GameObject[width, height];
        CellDust = new CosmicDust[width, height];
        CellState = new DustCellState[width, height];
        for (int x = 0; x < width; x++)
        for (int y = 0; y < height; y++)
            CellState[x, y] = DustCellState.Empty;
    }

    public bool TryGetCellState(Vector2Int gp, bool toroidal, Func<Vector2Int, Vector2Int> wrap, out DustCellState st)
    {
        st = DustCellState.Empty;
        if (CellState == null) return false;
        if (toroidal && wrap != null) gp = wrap(gp);
        if ((uint)gp.x >= (uint)Width || (uint)gp.y >= (uint)Height) return false;
        st = CellState[gp.x, gp.y];
        return true;
    }

    public void SetCellState(
        Vector2Int gp,
        DustCellState next,
        bool toroidal,
        Func<Vector2Int, Vector2Int> wrap,
        Action<Vector2Int, bool> onSolidTransition)
    {
        if (CellState == null) return;
        if (toroidal && wrap != null) gp = wrap(gp);
        if ((uint)gp.x >= (uint)Width || (uint)gp.y >= (uint)Height) return;
        var prev = CellState[gp.x, gp.y];
        CellState[gp.x, gp.y] = next;
        if (prev == next) return;
        if (next == DustCellState.Solid)
        {
            AllSolidCount++;
            onSolidTransition?.Invoke(gp, true);
        }
        else if (prev == DustCellState.Solid)
        {
            AllSolidCount = Mathf.Max(0, AllSolidCount - 1);
            onSolidTransition?.Invoke(gp, false);
        }
        if (prev == DustCellState.Regrowing && next != DustCellState.Regrowing) RegrowingCount = Mathf.Max(0, RegrowingCount - 1);
        else if (prev != DustCellState.Regrowing && next == DustCellState.Regrowing) RegrowingCount++;
    }
}

public enum DustCellState
{
    Empty = 0,
    PendingRegrow = 1,
    Regrowing = 2,
    Clearing = 3,
    Solid = 4,
}
