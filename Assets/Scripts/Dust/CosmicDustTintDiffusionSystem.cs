using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Local, bounded tint diffusion for dust cells.
///
/// Responsibilities:
/// - Track a dirty queue of cells whose visual tint should relax toward neighborhood average.
/// - Apply diffusion over time with a configurable budget to avoid frame spikes.
///
/// Non-responsibilities:
/// - Deciding WHICH events should mark cells dirty (caller responsibility).
/// - Knowing about pooling, regrowth, claims, or topology (caller responsibility).
/// - Computing "visual color" policy (caller supplies a function).
/// </summary>
public sealed class CosmicDustTintDiffusionSystem
{
    private readonly Queue<Vector2Int> _dirtyQueue = new Queue<Vector2Int>();
    private readonly HashSet<Vector2Int> _dirtySet  = new HashSet<Vector2Int>();
    private float _accum;

    private readonly Func<Vector2Int, CosmicDust> _getDustOrNull;
    private readonly Func<Vector2Int, Color> _getCellVisualColor;

    public CosmicDustTintDiffusionSystem(
        Func<Vector2Int, CosmicDust> getDustOrNull,
        Func<Vector2Int, Color> getCellVisualColor)
    {
        _getDustOrNull = getDustOrNull ?? throw new ArgumentNullException(nameof(getDustOrNull));
        _getCellVisualColor = getCellVisualColor ?? throw new ArgumentNullException(nameof(getCellVisualColor));
    }
    

    public void MarkDirty(Vector2Int center, int radius)
    {
        radius = Mathf.Max(0, radius);
        EnqueueDirty(center);

        if (radius == 0) return;

        for (int dy = -radius; dy <= radius; dy++)
        for (int dx = -radius; dx <= radius; dx++)
        {
            if (dx == 0 && dy == 0) continue;
            EnqueueDirty(new Vector2Int(center.x + dx, center.y + dy));
        }
    }

    public void Tick(
        float dt,
        bool enabled,
        float intervalSeconds,
        int maxCellsPerTick,
        int neighborRadius,
        float strength,
        bool propagateOnChange,
        float minDelta)
    {
        if (!enabled) return;

        _accum += dt;
        if (_accum < intervalSeconds) return;
        _accum = 0f;

        if (_dirtyQueue.Count == 0) return;

        int budget = Mathf.Max(1, maxCellsPerTick);
        int radius = Mathf.Max(0, neighborRadius);
        float t = Mathf.Clamp01(strength);

        for (int i = 0; i < budget && _dirtyQueue.Count > 0; i++)
        {
            Vector2Int cell = _dirtyQueue.Dequeue();
            _dirtySet.Remove(cell);

            CosmicDust dust = _getDustOrNull(cell);
            if (dust == null)
                continue;

            // Not-yet-carved cells (Role == None) show a static darkened preview of their secret
            // role color (or plain gray filler) — neither is a stable "identity" to diffuse, and
            // letting them blend into neighbors (of either kind) is exactly what was washing a
            // revealed-looking preview color back toward gray shortly after spawn. Only cells
            // that have actually been carved (a real Role) participate in diffusion, same as the
            // original pre-color-model-changes intent: same-role neighbors blend for a
            // "connectedness" glow, nothing else.
            if (dust.Role == MusicalRole.None)
                continue;

            Color avg = ComputeNeighborAverageColor(cell, dust.Role, radius, out int nCount);
            if (nCount <= 0) continue;

            Color cur = dust.CurrentTint;
            Color next = Color.Lerp(cur, avg, t);

// Don't let diffusion drag a solid cell's alpha below the visibility
// threshold — that creates invisible-but-collidable terrain.
// 0.55 matches CosmicDust.kSolidAlphaFloor.
            if (cur.a >= 0.55f)
                next.a = Mathf.Max(next.a, 0.55f);

            if (ColorMaxAbsDelta(cur, next) < minDelta)
                continue;

            dust.SetTint(next);

            if (propagateOnChange)
            {
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    EnqueueDirty(new Vector2Int(cell.x + dx, cell.y + dy));
                }
            }
        }
    }

    private void EnqueueDirty(Vector2Int cell)
    {
        if (_dirtySet.Add(cell))
            _dirtyQueue.Enqueue(cell);
    }

    private float ColorMaxAbsDelta(Color a, Color b)
    {
        float dr = Mathf.Abs(a.r - b.r);
        float dg = Mathf.Abs(a.g - b.g);
        float db = Mathf.Abs(a.b - b.b);
        float da = Mathf.Abs(a.a - b.a);
        return Mathf.Max(Mathf.Max(dr, dg), Mathf.Max(db, da));
    }

    // Neighbors whose role differs from the center's are excluded from the average, not just
    // down-weighted — otherwise a role-color boundary (the exact signal a player needs to read
    // "safe vs. dense") gets permanently blurred by its opposite-role neighbors every tick.
    // Tick() only ever calls this with a revealed (non-None) centerRole now, so requiring an
    // exact role match already excludes not-yet-carved neighbors too — their color is a static
    // preview or plain gray filler, not a stable identity to blend toward.
    private Color ComputeNeighborAverageColor(Vector2Int cell, MusicalRole centerRole, int radius, out int count)
    {
        radius = Mathf.Max(0, radius);
        count = 0;

        if (radius == 0)
            return _getCellVisualColor(cell);

        float r = 0f, g = 0f, b = 0f, a = 0f;

        for (int dy = -radius; dy <= radius; dy++)
        for (int dx = -radius; dx <= radius; dx++)
        {
            if (dx == 0 && dy == 0) continue;

            var neighborCell = new Vector2Int(cell.x + dx, cell.y + dy);
            var neighborDust = _getDustOrNull(neighborCell);
            var neighborRole = neighborDust != null ? neighborDust.Role : MusicalRole.None;
            if (neighborRole != centerRole)
                continue;

            Color c = _getCellVisualColor(neighborCell);
            r += c.r; g += c.g; b += c.b; a += c.a;
            count++;
        }

        if (count <= 0)
            return _getCellVisualColor(cell);

        return new Color(r / count, g / count, b / count, a / count);
    }
}
