using System.Collections.Generic;
using UnityEngine;

public struct DustImprint
{
    public Color color;
    public float healDelay;
    public float carveResistance01;
    public float drainResistance01;
    public int   maxEnergyUnits;
    public MusicalRole role;
    public MusicalRole hiddenRole; // permanent Voronoi ground-truth; None = not set
}

/// <summary>
/// Per-cell persistent dust metadata: baked color, role, permanent hidden Voronoi role,
/// and carve/drain resistance. The single most shared piece of CosmicDustGenerator state —
/// registry, clearing, regrow, maze-generation, and tint code all read or mutate imprints
/// without owning the underlying dictionary themselves.
/// </summary>
public sealed class DustImprintStore
{
    private Dictionary<Vector2Int, DustImprint> _map;

    public void EnsureAllocated(int capacity = 256)
    {
        _map ??= new Dictionary<Vector2Int, DustImprint>(capacity);
    }

    public bool TryGetValue(Vector2Int cell, out DustImprint imprint)
    {
        if (_map == null) { imprint = default; return false; }
        return _map.TryGetValue(cell, out imprint);
    }

    public bool ContainsKey(Vector2Int cell) => _map != null && _map.ContainsKey(cell);

    public DustImprint this[Vector2Int cell]
    {
        get => _map[cell];
        set { EnsureAllocated(); _map[cell] = value; }
    }

    public bool Remove(Vector2Int cell) => _map != null && _map.Remove(cell);

    public void Clear() => _map?.Clear();

    public Dictionary<Vector2Int, DustImprint>.Enumerator GetEnumerator()
    {
        EnsureAllocated();
        return _map.GetEnumerator();
    }

    /// <summary>
    /// If <paramref name="gp"/> is a gray (None-role) cell and has a hidden Voronoi role,
    /// promotes that role into the active imprint so regrowth uses it.
    /// Returns true when a promotion occurred.
    /// </summary>
    public bool PromoteHiddenRole(Vector2Int gp)
    {
        if (!TryGetValue(gp, out var imp)) return false;
        if (imp.hiddenRole == MusicalRole.None) return false;
        if (imp.role != MusicalRole.None) return false; // already colored — no-op

        imp.role = imp.hiddenRole;
        this[gp] = imp;
        // hiddenRole is kept as permanent Voronoi ground-truth for the motif lifetime.
        // RestoreVoronoiImprint() uses it to revert DiscoveryTrackNode paint when a vehicle carves the cell.
        return true;
    }

    /// <summary>
    /// Clears any DiscoveryTrackNode paint on a cell and re-promotes its permanent Voronoi role.
    /// Use this instead of PromoteHiddenRole when carving should revert to Voronoi regardless
    /// of whether a DiscoveryTrackNode has already painted the cell.
    /// Returns true if a Voronoi assignment existed and was applied, false otherwise.
    /// </summary>
    public bool RestoreVoronoiImprint(Vector2Int gp)
    {
        if (!TryGetValue(gp, out var imp) || imp.hiddenRole == MusicalRole.None) return false;

        // Clear any DiscoveryTrackNode paint so PromoteHiddenRole can re-apply the Voronoi role.
        if (imp.role != MusicalRole.None)
        {
            imp.role = MusicalRole.None;
            this[gp] = imp;
        }
        PromoteHiddenRole(gp);
        return true;
    }

    public void SetHiddenRole(Vector2Int gp, MusicalRole role)
    {
        TryGetValue(gp, out var imp);
        imp.hiddenRole = role;
        this[gp] = imp;
    }

    public void ApplyHiddenHintToDust(Vector2Int gp, CosmicDust dust)
    {
        if (!TryGetValue(gp, out var imp) || imp.hiddenRole == MusicalRole.None)
        {
            dust.SetHiddenHintColor(Color.clear);
            return;
        }
        var profile = MusicalRoleProfileLibrary.GetProfile(imp.hiddenRole);
        dust.SetHiddenHintColor(profile != null ? profile.GetBaseColor() : Color.clear);
    }
}
