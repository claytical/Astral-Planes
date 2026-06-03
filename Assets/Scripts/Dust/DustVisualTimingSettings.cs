using System;
using UnityEngine;

[CreateAssetMenu(fileName = "DustVisualTimingSettings", menuName = "Astral Planes/Dust/Visual Timing Settings")]
public sealed class DustVisualTimingSettings : ScriptableObject
{
    [SerializeField] private DustVisualTimings timings = DustVisualTimings.Default;

    public DustVisualTimings Timings => timings.Sanitized();
}
