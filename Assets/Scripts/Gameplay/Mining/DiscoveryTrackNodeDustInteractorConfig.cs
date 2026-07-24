using UnityEngine;

[CreateAssetMenu(fileName = "DiscoveryTrackNodeDustInteractorConfig", menuName = "Astral Planes/Discovery Track Node Dust Interactor Config")]
public class DiscoveryTrackNodeDustInteractorConfig : ScriptableObject
{
    [Header("Multipliers while in dust (node-specific)")]
    [Tooltip("Environment feedback scalar consumed by DiscoveryTrackNode locomotion while in dust.")]
    [Range(0f, 1f)] public float dustDragScalar = 0.85f;

    [Tooltip("Extra braking applied per FixedUpdate while inside dust.")]
    public float extraBrake = 0.25f;

    [Tooltip("Force applied to push the node back out when it is grid-inside a dust cell.")]
    public float escapePushForce = 12f;

    [Header("Skim Paint")]
    [Tooltip("Max exhaust-paint energy fraction applied to a dust cell the node grazes past while cornering along it (0 = off). Kept low — a graze, not a full recolor.")]
    [Range(0f, 0.4f)] public float exhaustEnergyFraction = 0.15f;
}
