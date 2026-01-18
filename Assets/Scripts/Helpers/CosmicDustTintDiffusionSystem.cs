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

    public void Clear()
    {
        _dirtyQueue.Clear();
        _dirtySet.Clear();
        _accum = 0f;
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

            Color avg = ComputeNeighborAverageColor(cell, radius, out int nCount);
            if (nCount <= 0) continue;

            Color cur = dust.CurrentTint;
            Color next = Color.Lerp(cur, avg, t);

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

    private Color ComputeNeighborAverageColor(Vector2Int cell, int radius, out int count)
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

            Color c = _getCellVisualColor(new Vector2Int(cell.x + dx, cell.y + dy));
            r += c.r; g += c.g; b += c.b; a += c.a;
            count++;
        }

        if (count <= 0)
            return _getCellVisualColor(cell);

        return new Color(r / count, g / count, b / count, a / count);
    }
}
