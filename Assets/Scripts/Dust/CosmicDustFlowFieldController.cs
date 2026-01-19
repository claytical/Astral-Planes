using System;
using UnityEngine;

/// <summary>
/// Lightweight, incremental 2D flow field used for dust "weather" steering.
/// Owns flow storage and update cursor; caller supplies phase bias and world->grid mapping.
/// </summary>
public sealed class CosmicDustFlowFieldController
{
    private Vector2[,] _flow;
    private int _w;
    private int _h;
    private int _cursor;

    // Hive timing: how often we change intent.
    private float _hiveTimer;
    private Vector2 _lastBias;

    public int Width  => _w;
    public int Height => _h;

    public void EnsureSize(int w, int h)
    {
        w = Mathf.Max(0, w);
        h = Mathf.Max(0, h);
        if (w == 0 || h == 0)
        {
            _flow = null;
            _w = _h = 0;
            _cursor = 0;
            return;
        }

        if (_flow != null && _w == w && _h == h)
            return;

        _w = w;
        _h = h;
        _cursor = 0;
        _flow = new Vector2[_w, _h];

        // Deterministic-ish initial direction.
        for (int x = 0; x < _w; x++)
        for (int y = 0; y < _h; y++)
            _flow[x, y] = Vector2.up;
    }

    /// <summary>
    /// Advances the flow field a small amount each frame.
    /// </summary>
    public void Tick(
        float dt,
        int flowTilesPerFrame,
        float hiveShiftInterval,
        float hiveShiftBlend,
        Func<Vector2> computeNewBias)
    {
        if (_flow == null || _w == 0 || _h == 0)
            return;

        // Shift hive "intent" occasionally.
        _hiveTimer += Mathf.Max(0f, dt);
        if (hiveShiftInterval > 0f && _hiveTimer >= hiveShiftInterval)
        {
            _hiveTimer = 0f;
            if (computeNewBias != null)
                _lastBias = computeNewBias();
        }

        int total = _w * _h;
        int steps = Mathf.Clamp(flowTilesPerFrame, 1, total);

        for (int n = 0; n < steps; n++)
        {
            int idx = _cursor++;
            if (_cursor >= total) _cursor = 0;

            int x = idx % _w;
            int y = idx / _w;

            // Target with a little noise + last bias.
            Vector2 noise = UnityEngine.Random.insideUnitCircle;
            if (noise.sqrMagnitude > 1e-6f) noise.Normalize();
            Vector2 target = (noise * 0.7f + _lastBias);
            if (target.sqrMagnitude > 1e-6f) target.Normalize();
            else target = Vector2.up;

            _flow[x, y] = Vector2.Lerp(_flow[x, y], target, hiveShiftBlend);
            if (_flow[x, y].sqrMagnitude < 1e-4f) _flow[x, y] = Vector2.up;
        }
    }

    public Vector2 SampleAtGrid(Vector2Int grid)
    {
        if (_flow == null) return Vector2.zero;
        if ((uint)grid.x >= (uint)_w || (uint)grid.y >= (uint)_h) return Vector2.zero;
        return _flow[grid.x, grid.y];
    }
}
