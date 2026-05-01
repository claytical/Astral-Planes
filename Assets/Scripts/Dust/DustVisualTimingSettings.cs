using System;
using UnityEngine;

[CreateAssetMenu(fileName = "DustVisualTimingSettings", menuName = "Dust/Visual Timing Settings")]
public sealed class DustVisualTimingSettings : ScriptableObject
{
    [SerializeField] private CosmicDust.DustVisualTimings timings = CosmicDust.DustVisualTimings.Default;

    public CosmicDust.DustVisualTimings Timings => timings.Sanitized();
}
