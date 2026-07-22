using System;
using System.Collections.Generic;
using UnityEngine;

[Flags]
public enum CellFlags
{
    None            = 0,
    ForceGrayRegrow = 1 << 0,
    ZapForceGray    = 1 << 1,
    VoidGrow        = 1 << 2,
    PlayerCarved    = 1 << 3,
}

/// <summary>
/// Authoritative cell-grid lookups: solid/empty state, cell GameObject/CosmicDust
/// resolution, toroidal wrap/bounds checks, and per-cell CellFlags bits. Wraps the
/// existing DustGridState array storage (shared by reference with the facade — clearing
/// and maze-generation code still read it directly) plus the GameObject reverse-lookup
/// and flags dictionaries the facade previously owned inline.
/// </summary>
public sealed class CosmicDustCellRegistry
{
    private readonly DustGridState _gridState;
    private readonly Func<bool> _toroidal;
    private readonly Func<int> _drumsWidth;
    private readonly Func<int> _drumsHeight;
    private readonly Func<Vector2Int, MusicalRole> _hiddenRoleLookup;
    private readonly Action<Vector2Int, bool> _onSolidTransition;

    private readonly Dictionary<GameObject, Vector2Int> _goToCell = new Dictionary<GameObject, Vector2Int>(1024);
    private readonly Dictionary<Vector2Int, CellFlags> _cellFlags = new();

    public CosmicDustCellRegistry(
        DustGridState gridState,
        Func<bool> toroidal,
        Func<int> drumsWidth,
        Func<int> drumsHeight,
        Func<Vector2Int, MusicalRole> hiddenRoleLookup,
        Action<Vector2Int, bool> onSolidTransition)
    {
        _gridState = gridState;
        _toroidal = toroidal;
        _drumsWidth = drumsWidth;
        _drumsHeight = drumsHeight;
        _hiddenRoleLookup = hiddenRoleLookup;
        _onSolidTransition = onSolidTransition;
    }

    public Dictionary<GameObject, Vector2Int> GoToCell => _goToCell;

    public bool IsInBounds(Vector2Int gp)
    {
        int w = _drumsWidth();
        int h = _drumsHeight();
        if (w <= 0 || h <= 0) return false;
        if (_toroidal()) return true; // every coordinate is valid after wrapping
        return gp.x >= 0 && gp.y >= 0 && gp.x < w && gp.y < h;
    }

    /// <summary>
    /// Wraps a grid coordinate toroidally when toroidal mode is enabled.
    /// Returns the input unchanged in non-toroidal mode.
    /// </summary>
    public Vector2Int WrapCell(Vector2Int gp)
    {
        if (!_toroidal() || _gridState.Width <= 0 || _gridState.Height <= 0) return gp;
        return new Vector2Int(
            ((gp.x % _gridState.Width) + _gridState.Width) % _gridState.Width,
            ((gp.y % _gridState.Height) + _gridState.Height) % _gridState.Height);
    }

    public bool TryGetCellGo(Vector2Int gp, out GameObject go)
    {
        go = null;
        if (_gridState.CellGo == null) return false;
        if (_toroidal()) gp = WrapCell(gp);
        if ((uint)gp.x >= (uint)_gridState.Width || (uint)gp.y >= (uint)_gridState.Height) return false;
        go = _gridState.CellGo[gp.x, gp.y];
        return go != null;
    }

    public bool TryGetCellState(Vector2Int gp, out DustCellState st) =>
        _gridState.TryGetCellState(gp, _toroidal(), WrapCell, out st);

    public void SetCellState(Vector2Int gp, DustCellState st) =>
        _gridState.SetCellState(gp, st, _toroidal(), WrapCell, _onSolidTransition);

    public bool HasDustAt(Vector2Int gridPos) =>
        TryGetCellState(gridPos, out var st) && st == DustCellState.Solid;

    public bool TryGetDustAt(Vector2Int cell, out CosmicDust dust)
    {
        dust = null;
        if (!TryGetCellGo(cell, out var go) || go == null) return false;
        return go.TryGetComponent(out dust) && dust != null;
    }

    /// <summary>
    /// Populates <paramref name="results"/> with every Solid cell whose CosmicDust role is not None.
    /// Clears the list before filling.
    /// </summary>
    public void GetColoredDustCells(List<Vector2Int> results)
    {
        results.Clear();
        if (_gridState.CellState == null || _gridState.CellDust == null) return;
        for (int y = 0; y < _gridState.Height; y++)
        for (int x = 0; x < _gridState.Width; x++)
        {
            if (_gridState.CellState[x, y] != DustCellState.Solid) continue;
            var dust = _gridState.CellDust[x, y];
            if (dust != null && dust.Role != MusicalRole.None && dust.currentEnergyUnits > 0 && dust.IsVisuallyPresentForTargeting())
                results.Add(new Vector2Int(x, y));
        }
    }

    public MusicalRole GetZoneRole(Vector2Int cell) => _hiddenRoleLookup(cell);

    public bool HasCellFlag(Vector2Int cell, CellFlags flag) =>
        _cellFlags.TryGetValue(cell, out var f) && (f & flag) != 0;

    public void SetCellFlag(Vector2Int cell, CellFlags flag)
    {
        _cellFlags.TryGetValue(cell, out var f);
        _cellFlags[cell] = f | flag;
    }

    public bool ClearCellFlag(Vector2Int cell, CellFlags flag)
    {
        if (!_cellFlags.TryGetValue(cell, out var f)) return false;
        var next = f & ~flag;
        if (next == CellFlags.None) _cellFlags.Remove(cell);
        else _cellFlags[cell] = next;
        return (f & flag) != 0;
    }

    public void ClearAllFlags() => _cellFlags.Clear();
}
