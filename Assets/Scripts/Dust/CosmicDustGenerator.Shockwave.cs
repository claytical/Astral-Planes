using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Radial color shockwave through solid dust — the visual tell for the bass drop
// (DrumTrack.BassDrop.cs, LEGIBILITY_AUDIT.md D1). Each solid cell flashes as the expanding
// ring passes it; cells are bucketed by distance once up front, and solidity is re-checked
// when the ring reaches them so cells cleared mid-wave are skipped.
public partial class CosmicDustGenerator
{
    private Coroutine _shockwaveRoutine;
    // Ring buckets reused across bass drops (one wave runs at a time — a new one stops the old).
    private List<Vector2Int>[] _shockwaveRings;

    /// <summary>Flashes one cell's displayed tint if it's currently solid dust (no state change).
    /// Used by the locked phase-exit gate shimmer (PathExitLockedStub).</summary>
    public bool TryFlashSolidCell(Vector2Int gp, Color color, float seconds)
    {
        if (_gridState.CellState == null || _gridState.CellGo == null) return false;
        if (gp.x < 0 || gp.y < 0 || gp.x >= _gridState.Width || gp.y >= _gridState.Height) return false;
        if (_gridState.CellState[gp.x, gp.y] != DustCellState.Solid) return false;
        var dust = _gridState.CellDust != null ? _gridState.CellDust[gp.x, gp.y] : null;
        if (dust == null)
        {
            var go = _gridState.CellGo[gp.x, gp.y];
            if (go == null || !go.TryGetComponent(out dust)) return false;
        }
        dust.BeginColorFlash(color, seconds);
        return true;
    }

    public void BeginShockwaveFromCenter(Color color, float cellsPerSecond, float flashSeconds)
    {
        if (_gridState.CellState == null || _gridState.CellGo == null || _gridState.Width <= 0 || _gridState.Height <= 0) return;
        var origin = new Vector2((_gridState.Width - 1) * 0.5f, (_gridState.Height - 1) * 0.5f);
        if (_shockwaveRoutine != null) StopCoroutine(_shockwaveRoutine);
        _shockwaveRoutine = StartCoroutine(ShockwaveRoutine(origin, color, Mathf.Max(1f, cellsPerSecond), flashSeconds));
    }

    private IEnumerator ShockwaveRoutine(Vector2 origin, Color color, float cellsPerSecond, float flashSeconds)
    {
        int w = _gridState.Width, h = _gridState.Height;
        int maxRing = Mathf.CeilToInt(Mathf.Sqrt(w * w + h * h) * 0.5f) + 1;
        if (_shockwaveRings == null || _shockwaveRings.Length < maxRing + 1)
            _shockwaveRings = new List<Vector2Int>[maxRing + 1];
        var rings = _shockwaveRings;
        for (int i = 0; i < rings.Length; i++) rings[i]?.Clear();

        for (int x = 0; x < w; x++)
        for (int y = 0; y < h; y++)
        {
            if (_gridState.CellState[x, y] != DustCellState.Solid) continue;
            int r = Mathf.Min(maxRing, Mathf.RoundToInt(Vector2.Distance(origin, new Vector2(x, y))));
            (rings[r] ??= new List<Vector2Int>()).Add(new Vector2Int(x, y));
        }

        float start = Time.time;
        for (int r = 0; r <= maxRing; r++)
        {
            float due = r / cellsPerSecond;
            while (Time.time - start < due) yield return null;
            // Grid rebuilt mid-wave (motif change) — bucketed coordinates are stale.
            if (_gridState.CellState == null || _gridState.Width != w || _gridState.Height != h) break;

            var ring = rings[r];
            if (ring == null || ring.Count == 0) continue;
            for (int i = 0; i < ring.Count; i++)
            {
                var c = ring[i];
                if (_gridState.CellState[c.x, c.y] != DustCellState.Solid) continue;
                // Flashes are budgeted per frame (DustParticleRewriteBudget), so a maze-wide wave
                // no longer rewrites every cell's particles every frame.
                var dust = _gridState.CellDust != null ? _gridState.CellDust[c.x, c.y] : null;
                if (dust == null)
                {
                    var go = _gridState.CellGo[c.x, c.y];
                    if (go == null || !go.TryGetComponent(out dust)) continue;
                }
                dust.BeginColorFlash(color, flashSeconds);
            }
        }
        _shockwaveRoutine = null;
    }
}
