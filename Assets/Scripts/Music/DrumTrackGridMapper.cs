using System;
using UnityEngine;

/// <summary>
/// Owns world↔grid coordinate mapping and spawn-grid delegation for DrumTrack:
/// play-area computation/locking, tile-size caching, and SpawnGrid/CosmicDustGenerator
/// occupancy queries. Fully decoupled from DrumTrack's DSP/motif state — constructed
/// with closures back into DrumTrack the same way CosmicDustCellRegistry is constructed
/// by CosmicDustGenerator.
/// </summary>
public sealed class DrumTrackGridMapper
{
    private readonly Func<DrumTrackConfig> _config;
    private readonly Func<SpawnGrid> _spawnGrid;
    private readonly Func<CosmicDustGenerator> _dust;
    private readonly Func<GameFlowManager> _gfm;
    private readonly Func<RectTransform> _playAreaBottomAnchor;

    private DrumTrack.PlayArea _lockedPlayArea;
    private bool _hasLockedPlayArea;
    private float _cachedTileDiameterWorld = -1f;
    private DrumTrack.PlayArea _lastPlayAreaForTileCache;
    private bool _hasLastPlayAreaForTileCache;

    public DrumTrackGridMapper(
        Func<DrumTrackConfig> config,
        Func<SpawnGrid> spawnGrid,
        Func<CosmicDustGenerator> dust,
        Func<GameFlowManager> gfm,
        Func<RectTransform> playAreaBottomAnchor)
    {
        _config = config;
        _spawnGrid = spawnGrid;
        _dust = dust;
        _gfm = gfm;
        _playAreaBottomAnchor = playAreaBottomAnchor;
    }

    private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

    private DrumTrack.PlayArea GetPlayAreaWorld()
    {
        TryGetPlayAreaWorld(out var area);
        return area;
    }

    /// <summary>
    /// Returns the world-space play area used to map SpawnGrid cells to world positions.
    /// The play area is the visible camera region, clipped so it does not overlap the NoteVisualizer.
    /// </summary>
    public bool TryGetPlayAreaWorld(out DrumTrack.PlayArea area)
    {
        var config = _config();
        if (config.lockPlayAreaAfterInit && _hasLockedPlayArea)
        {
            area = _lockedPlayArea;
            return true;
        }

        area = default;

        // We want DrumTrack to be the sole authority for grid→world mapping.
        // Do not depend on NoteVisualizer (it may not be initialized yet, and its layout can change).
        if (!HasSpawnGrid()) return false;

        var cam = Camera.main;
        if (cam == null) return false;

        // Prefer orthographic projection (your game is 2D). If not orthographic, fall back to viewport corners.
        float halfH = cam.orthographicSize;
        float halfW = halfH * cam.aspect;
        float left, right, bottom, top;
        float z = cam.orthographic ? 0f : Mathf.Abs(cam.transform.position.z);

        if (cam.orthographic)
        {
            // Anchor to world origin (0,0) so the play area aligns with Boundaries.cs,
            // which positions all four boundary colliders at ±halfW/±halfH from world
            // origin — independent of where the camera happens to sit.
            left   = -halfW;
            right  = +halfW;
            bottom = -halfH;
            top    = +halfH;
        }
        else
        {
            // Perspective camera safety path.
            Vector3 bl = cam.ViewportToWorldPoint(new Vector3(0f, 0f, z));
            Vector3 tr = cam.ViewportToWorldPoint(new Vector3(1f, 1f, z));
            left   = bl.x;
            right  = tr.x;
            bottom = bl.y;
            top    = tr.y;
        }

        // Optional padding to keep spawns away from the very edge of the screen.
        if (config.gridPadding > 0f)
        {
            left   += config.gridPadding;
            right  -= config.gridPadding;
            bottom += config.gridPadding;
            top    -= config.gridPadding;
        }

        // Reserve a bottom viewport band for UI derived from config.uiBottomPaddingPx.
        float uiBotV = Screen.height > 0 ? Mathf.Clamp01(config.uiBottomPaddingPx / (float)Screen.height) : 0f;
        if (uiBotV > 0f)
        {
            // For orthographic cameras map the viewport fraction from world origin,
            // consistent with the world-anchored bounds above.
            float uiBottomWorld = cam.orthographic
                ? Mathf.Lerp(-halfH, halfH, uiBotV)
                : cam.ViewportToWorldPoint(new Vector3(0f, uiBotV, z)).y;
            bottom = Mathf.Max(bottom, uiBottomWorld);
        }
        var anchor = _playAreaBottomAnchor();
        if (anchor != null)
        {
            var corners = new Vector3[4];
            anchor.GetWorldCorners(corners);
            // Only override when Canvas layout has been applied (rect has non-zero width).
            if (corners[2].x - corners[0].x > 0.001f)
                bottom = corners[1].y;  // top-left corner — same source as NoteVisualizer.GetTopWorldY()
        }
        // Validate.
        if (!IsFinite(left) || !IsFinite(right) || !IsFinite(bottom) || !IsFinite(top)) return false;
        if (right <= left || top <= bottom) return false;

        area.left = left;
        area.right = right;
        area.bottom = bottom;
        area.top = top;
        if (config.lockPlayAreaAfterInit && !_hasLockedPlayArea)
        {
            _lockedPlayArea = area;
            _hasLockedPlayArea = true;
        }
        return true;
    }

    public float GetCellWorldSize() => GetTileDiameterWorld();

    public Vector2Int CellOf(Vector3 world) => WorldToGridPosition(world);

    public void OccupySpawnCell(int x, int y, GridObjectType type)
    {
        var grid = _spawnGrid();
        if (grid == null)
        {
            Debug.LogError("[DrumTrack] OccupySpawnCell: spawnGrid is null");
            return;
        }
        grid.OccupyCell(x, y, type);
    }

    public int GetSpawnGridWidth() => _spawnGrid().gridWidth;

    public int GetSpawnGridHeight() => _spawnGrid().gridHeight;

    public bool HasDustAt(Vector2Int cell)
    {
        var dust = _dust();
        if (dust == null) return false;
        return dust.HasDustAt(cell);
    }

    public bool TryGetDustAt(Vector2Int cell, out CosmicDust dust)
    {
        dust = null;
        var d = _dust();
        return d != null && d.TryGetDustAt(cell, out dust);
    }

    public bool IsSpawnCellAvailable(int x, int y)
    {
        if (_gfm() == null || _spawnGrid() == null) return false;
        // 1) Spawn-grid occupancy (nodes, notes, etc.)
        if (!_spawnGrid().IsCellAvailable(x, y))
            return false;
        // 2) Dust blocks spawning (your decision 8A: no spawning inside dust)
        if (_dust() != null && _dust().HasDustAt(new Vector2Int(x, y)))
            return false;
        return true;
    }

    public bool HasSpawnGrid() => _spawnGrid() != null;

    public void ResetSpawnCellBehavior(int x, int y) => _spawnGrid().ResetCellBehavior(x, y);

    public void FreeSpawnCell(int x, int y) => _spawnGrid().FreeCell(x, y);

    public Vector2Int GetRandomAvailableCell() => _spawnGrid().GetRandomAvailableCell();

    public Vector2 GridToWorldPosition(Vector2Int cell)
    {
        if (!TryGetPlayAreaWorld(out var area))
        {
            if (_config().lockPlayAreaAfterInit && _hasLockedPlayArea)
                area = _lockedPlayArea;
            else
                return Vector2.zero;
        }

        GetTileSizeWorld(out float tileX, out float tileY);

        float x = area.left   + cell.x * tileX;
        float y = area.bottom + cell.y * tileY;

        return new Vector2(x, y);
    }

    private void GetTileSizeWorld(out float tileX, out float tileY)
    {
        tileX = 1f;
        tileY = 1f;

        if (!TryGetPlayAreaWorld(out var area))
        {
            if (_config().lockPlayAreaAfterInit && _hasLockedPlayArea)
                area = _lockedPlayArea;
            else
                return;
        }

        int w = Mathf.Max(1, GetSpawnGridWidth());
        int h = Mathf.Max(1, GetSpawnGridHeight());

        // Divide by (count - 1) so cell 0 lands on area.left and cell w-1 lands on area.right,
        // aligning outermost dust cells with the physical boundary colliders.
        tileX = area.width  / Mathf.Max(1, w - 1);
        tileY = area.height / Mathf.Max(1, h - 1);

        if (tileX <= 0.00001f) tileX = 1f;
        if (tileY <= 0.00001f) tileY = 1f;
    }

    public Vector2Int WorldToGridPosition(Vector3 worldPos)
    {
        if (!TryGetPlayAreaWorld(out var area))
        {
            if (_config().lockPlayAreaAfterInit && _hasLockedPlayArea)
                area = _lockedPlayArea;
            else
                return Vector2Int.zero;
        }

        int w = Mathf.Max(1, GetSpawnGridWidth());
        int h = Mathf.Max(1, GetSpawnGridHeight());

        GetTileSizeWorld(out float tileX, out float tileY);

        float gx = (worldPos.x - area.left) / tileX;
        float gy = (worldPos.y - area.bottom) / tileY;

        // Use nearest-cell mapping instead of floor-bias so minor play-area/camera
        // drift does not slowly push the reconstructed dust grid downward/leftward.
        int ix = Mathf.RoundToInt(gx);
        int iy = Mathf.RoundToInt(gy);

        var dust = _dust();
        if (dust != null && dust.toroidal)
        {
            ix = ((ix % w) + w) % w;
            iy = ((iy % h) + h) % h;
        }
        else
        {
            ix = Mathf.Clamp(ix, 0, w - 1);
            iy = Mathf.Clamp(iy, 0, h - 1);
        }

        return new Vector2Int(ix, iy);
    }

    /// <summary>
    /// Wraps a grid coordinate toroidally when the dust generator is in toroidal mode.
    /// Returns the input clamped to grid bounds otherwise.
    /// </summary>
    public Vector2Int WrapGridCell(Vector2Int gp)
    {
        var dust = _dust();
        if (dust != null && dust.toroidal)
            return dust.WrapCell(gp);
        int w = Mathf.Max(1, GetSpawnGridWidth());
        int h = Mathf.Max(1, GetSpawnGridHeight());
        return new Vector2Int(Mathf.Clamp(gp.x, 0, w - 1), Mathf.Clamp(gp.y, 0, h - 1));
    }

    private float GetTileDiameterWorld()
    {
        // Prefer to recompute from current play area + grid dimensions.
        // This must match SyncTileWithScreen() and the GridToWorld/WorldToGrid formulas.
        if (TryGetPlayAreaWorld(out var area))
        {
            int w = Mathf.Max(1, GetSpawnGridWidth());
            int h = Mathf.Max(1, GetSpawnGridHeight());

            // If play area changed, invalidate cache.
            if (!_hasLastPlayAreaForTileCache || !ApproximatelyEqual(area, _lastPlayAreaForTileCache))
            {
                _cachedTileDiameterWorld = 0f;
                _lastPlayAreaForTileCache = area;
                _hasLastPlayAreaForTileCache = true;
            }

            if (_cachedTileDiameterWorld <= 0f)
            {
                float tileX = area.width  / Mathf.Max(1, w - 1);
                float tileY = area.height / Mathf.Max(1, h - 1);
                float tile  = Mathf.Min(tileX, tileY);
                _cachedTileDiameterWorld = (tile > 0f) ? tile : 1f;
            }

            return _cachedTileDiameterWorld;
        }

        // If we can't compute play area, fall back to prior cache if available.
        if (_cachedTileDiameterWorld > 0f)
            return _cachedTileDiameterWorld;

        return 1f;
    }

    private void InvalidateGridWorldCache()
    {
        _cachedTileDiameterWorld = 0f;
        _hasLastPlayAreaForTileCache = false;
        _hasLockedPlayArea = false;
    }

    private static bool ApproximatelyEqual(DrumTrack.PlayArea a, DrumTrack.PlayArea b)
    {
        // Loose epsilon; avoids thrashing cache due to float jitter.
        const float eps = 0.0005f;
        return Mathf.Abs(a.left - b.left) < eps &&
               Mathf.Abs(a.right - b.right) < eps &&
               Mathf.Abs(a.bottom - b.bottom) < eps &&
               Mathf.Abs(a.top - b.top) < eps;
    }

    public void RefreshPlayAreaLock()
    {
        _hasLockedPlayArea = false;
        TryGetPlayAreaWorld(out _);
    }

    public void SyncTileWithScreen()
    {
        var gfm = _gfm();
        var spawnGrid = _spawnGrid();
        var dust = _dust();
        if (gfm == null || spawnGrid == null || dust == null)
            return;

        int gridW = spawnGrid.gridWidth;
        int gridH = spawnGrid.gridHeight;
        if (gridW <= 0 || gridH <= 0) return;

        // Invalidate the cached play area so TryGetPlayAreaWorld recomputes from current values.
        _hasLockedPlayArea = false;
        InvalidateGridWorldCache();

        DrumTrack.PlayArea area = GetPlayAreaWorld();
        if (area.width <= 0f || area.height <= 0f) return;

        // NOTE: Dividing by gridW / gridH, NOT (gridW - 1)/(gridH - 1)
        float tileX = area.width  / gridW;
        float tileY = area.height / gridH;

        float tile = Mathf.Min(tileX, tileY);   // square cells; in your case tileX == tileY

        dust.TileDiameterWorld = tile;
        _cachedTileDiameterWorld = tile;
    }

    public void AutoSizeSpawnGridIfEnabled()
    {
        var config = _config();
        if (!config.autoSizeSpawnGridToScreen) return;
        var spawnGrid = _spawnGrid();
        if (spawnGrid == null) return;

        // Grid dimensions are fixed to the reference resolution so that dust tile world-size
        // stays uniform across all devices.  What changes per-device is only the camera's
        // orthographic size / world-space extents — not the number of grid cells.
        //
        // Previously cols/rows were derived from actual screen pixels, which caused dust to
        // appear spaced out on high-res laptops (more cells → larger tile world size) and
        // packed on the Steam Deck (fewer cells → smaller tile world size).
        if (config.referenceWidthPx <= 0 || config.referenceColumns <= 0)
        {
            Debug.LogWarning("[GridAutoSize] config.referenceWidthPx or config.referenceColumns not set; skipping auto-size.");
            return;
        }

        float cellPx  = config.referenceWidthPx / (float)config.referenceColumns; // e.g. 1920/36 ≈ 53.33
        int   refCols = config.referenceColumns;
        // Derive reference row count from the reference height (1080) minus the reference UI padding.
        int   refUsableH = 1080 - Mathf.Max(0, config.uiBottomPaddingPx);
        int   refRows    = Mathf.Max(1, Mathf.RoundToInt(refUsableH / cellPx));

        int sh = Mathf.Max(1, Screen.height);

        // Apply fixed reference grid — same on every device.
        spawnGrid.ResizeGrid(refCols, refRows);
        _hasLockedPlayArea = false;

        // Any cached world mapping based on old grid dims must be invalidated.
        InvalidateGridWorldCache();

        if (GameFlowManager.VerboseLogging) Debug.Log($"[GridAutoSize] screen={Screen.width}x{sh} cellPx={cellPx:F2} -> grid={refCols}x{refRows} (reference-locked)");
    }

    public void ValidateSpawnGrid()
    {
        var gfm = _gfm();
        var spawnGrid = _spawnGrid();
        if (gfm == null || spawnGrid == null)
            return;

        for (int x = 0; x < spawnGrid.gridWidth; x++)
        {
            for (int y = 0; y < spawnGrid.gridHeight; y++)
            {
                // Skip empty cells outright
                if (spawnGrid.IsCellAvailable(x, y))
                    continue;

                // Only validate cells that *should* belong to Collectables / notes.
                // Do NOT touch Dust or MineNode occupancy here.
                var cell = spawnGrid.GridCells[x, y];
                if (cell.ObjectType != GridObjectType.Note)
                    continue;

                Vector3 worldPos = GridToWorldPosition(new Vector2Int(x, y));
                Collider2D[] hits = Physics2D.OverlapCircleAll(worldPos, 0.25f);

                bool objectPresent = false;
                foreach (var hit in hits)
                {
                    if (hit.GetComponent<Collectable>())
                    {
                        objectPresent = true;
                        break;
                    }
                }

                if (!objectPresent)
                {
                    spawnGrid.FreeCell(x, y);
                }
            }
        }
    }

    public float GetScreenWorldWidth()
    {
        var cam = Camera.main;
        if (!cam)
        {
            Debug.LogWarning("[DrumTrack] GetScreenWorldWidth: Camera.main is null.");
            return 0f;
        }

        float z          = -cam.transform.position.z;
        Vector3 bottomLeft = cam.ViewportToWorldPoint(new Vector3(0f, 0f, z));
        Vector3 topRight   = cam.ViewportToWorldPoint(new Vector3(1f, 1f, z));
        var config = _config();
        return (topRight.x - config.gridPadding) - (bottomLeft.x + config.gridPadding);
    }
}
