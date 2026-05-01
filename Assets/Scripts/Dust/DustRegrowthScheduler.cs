using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public sealed class DustRegrowthScheduler
{
    public readonly Queue<List<Vector2Int>> PendingCorridorRegrowth = new();
    public readonly Dictionary<Vector2Int, Coroutine> RegrowthCoroutines = new();
    public readonly Dictionary<Vector2Int, Coroutine> VoidGrowCoroutines = new();

    public int RegrowCellsPerStep { get; private set; }
    public float ColliderEnableDelaySeconds { get; private set; }

    public void Initialize(int regrowCellsPerStep, float colliderEnableDelaySeconds)
    {
        RegrowCellsPerStep = Mathf.Max(1, regrowCellsPerStep);
        ColliderEnableDelaySeconds = Mathf.Max(0f, colliderEnableDelaySeconds);
    }
}
