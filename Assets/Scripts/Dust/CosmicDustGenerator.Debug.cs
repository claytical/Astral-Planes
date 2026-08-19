using UnityEngine;

public partial class CosmicDustGenerator
{
    [ContextMenu("Debug: Find Phantom Colliders")]
    public void DebugFindPhantomColliders()
    {
        if (_gridState.CellState == null || _gridState.CellGo == null) return;
        int phantoms = 0;
        for (int x = 0; x < _gridState.Width; x++)
        for (int y = 0; y < _gridState.Height; y++)
        {
            var st = _gridState.CellState[x, y];
            var go = _gridState.CellGo[x, y];
            if (go == null) continue;

            if (st != DustCellState.Solid)
            {
                // Cell isn't solid, but does it have an enabled collider?
                if (go.TryGetComponent<CosmicDust>(out var d) && d.terrainCollider != null
                                                              && d.terrainCollider.enabled)
                {
                    Debug.LogWarning($"[PHANTOM] Cell ({x},{y}) state={st} but collider ENABLED on {go.name}", go);
                    phantoms++;
                }
            }
        }
        if (GameFlowManager.VerboseLogging) Debug.Log($"[PHANTOM] Scan complete. Found {phantoms} phantom colliders.");
    }

    [ContextMenu("Debug: Audit Colored Dust vs Star Visibility")]
    public void DebugAuditColoredDustStarVisibility()
    {
        if (_gridState.CellState == null || _gridState.CellDust == null || _gridState.CellGo == null) return;

        int roleZeroEnergy = 0;
        int roleNoneVisuallyTinted = 0;

        for (int x = 0; x < _gridState.Width; x++)
        for (int y = 0; y < _gridState.Height; y++)
        {
            var st  = _gridState.CellState[x, y];
            var go  = _gridState.CellGo[x, y];
            var gp  = new Vector2Int(x, y);
            if (go == null) continue;
            if (!go.TryGetComponent<CosmicDust>(out var d) || d == null) continue;

            // Case 1: Solid cell with a role but 0 energy → stars can never drain it.
            if (st == DustCellState.Solid && d.Role != MusicalRole.None && d.currentEnergyUnits <= 0)
            {
                Debug.LogWarning($"[DUST-AUDIT] ({x},{y}) ROLE={d.Role} energy=0 → invisible to star tentacles (trapped dormant)", go);
                roleZeroEnergy++;
            }

            // Case 3: Gray (None-role) cell that appears tinted (RGB far from mazeTint) → diffusion artifact.
            if (st == DustCellState.Solid && d.Role == MusicalRole.None)
            {
                Color cur = d.CurrentTint;
                Color gray = config.mazeTint;
                float delta = Mathf.Max(Mathf.Abs(cur.r - gray.r), Mathf.Max(Mathf.Abs(cur.g - gray.g), Mathf.Abs(cur.b - gray.b)));
                if (delta > 0.15f)
                {
                    if (GameFlowManager.VerboseLogging) Debug.Log($"[DUST-AUDIT] ({x},{y}) Role=None but tint delta={delta:F2} from mazeTint — likely diffusion bleed", go);
                    roleNoneVisuallyTinted++;
                }
            }
        }

        if (GameFlowManager.VerboseLogging) Debug.Log($"[DUST-AUDIT] Done. role+zeroEnergy={roleZeroEnergy}  diffusionBleed={roleNoneVisuallyTinted}");
    }

    /// <summary>
    /// Flags cells that are Solid/Regrowing/PendingRegrow but belong to neither the current
    /// motif's authored maze pattern nor a legitimate VoidGrow ring — the signature of a stray
    /// coroutine that survived a ClearMaze() reset and materialized a cell at old coordinates.
    /// Run right after a motif/phase transition settles.
    /// </summary>
    [ContextMenu("Debug: Audit Orphan Cells")]
    public void DebugAuditOrphanCells()
    {
        if (_gridState.CellState == null) return;

        int orphans = 0;
        for (int x = 0; x < _gridState.Width; x++)
        for (int y = 0; y < _gridState.Height; y++)
        {
            var st = _gridState.CellState[x, y];
            if (st != DustCellState.Solid && st != DustCellState.Regrowing && st != DustCellState.PendingRegrow)
                continue;

            var gp = new Vector2Int(x, y);
            if (_mazePatternCells != null && _mazePatternCells.Contains(gp)) continue;
            if (_registry.HasCellFlag(gp, CellFlags.VoidGrow)) continue;
            if (_permanentClearCells.Contains(gp)) continue;

            Debug.LogWarning($"[DUST-AUDIT] ({x},{y}) state={st} outside current maze pattern and not VoidGrow — likely orphan cell from a prior motif.");
            orphans++;
        }

        if (GameFlowManager.VerboseLogging) Debug.Log($"[DUST-AUDIT] Orphan scan complete. Found {orphans} orphan cells.");
    }
}
