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
        int colliderOnSpriteOff = 0;
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

            // Case 2: Collider enabled but sprite renderer disabled → invisible wall.
            if (d.visual.sprite != null && !d.visual.sprite.enabled)
            {
                bool colEnabled = false;
                var cols = go.GetComponentsInChildren<Collider2D>(true);
                for (int i = 0; i < cols.Length; i++) if (cols[i] != null && cols[i].enabled) { colEnabled = true; break; }
                if (d.terrainCollider != null && d.terrainCollider.enabled) colEnabled = true;
                if (colEnabled)
                {
                    Debug.LogWarning($"[DUST-AUDIT] ({x},{y}) state={st} collider=ON sprite=OFF → invisible wall", go);
                    colliderOnSpriteOff++;
                }
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

        if (GameFlowManager.VerboseLogging) Debug.Log($"[DUST-AUDIT] Done. role+zeroEnergy={roleZeroEnergy}  collider+spriteOff={colliderOnSpriteOff}  diffusionBleed={roleNoneVisuallyTinted}");
    }
}
