using System.Collections.Generic;
using UnityEngine;

public sealed class CosmicDustCollectableHoldMap
{
    private readonly Dictionary<Vector2Int, float> _until = new Dictionary<Vector2Int, float>();

    public bool Release(Vector2Int cell)
    {
        return _until.Remove(cell);
    }

    public void ExtendHold(Vector2Int cell, float untilTime)
    {
        if (_until.TryGetValue(cell, out float prev))
            _until[cell] = Mathf.Max(prev, untilTime);
        else
            _until[cell] = untilTime;
    }

    public bool IsHeld(Vector2Int cell, float nowTime)
    {
        if (!_until.TryGetValue(cell, out float until)) return false;
        if (until <= nowTime) return false;
        return true;
    }

    public float GetUntilOrZero(Vector2Int cell)
    {
        return _until.TryGetValue(cell, out float until) ? until : 0f;
    }
}