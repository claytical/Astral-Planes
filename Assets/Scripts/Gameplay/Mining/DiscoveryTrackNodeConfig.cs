using UnityEngine;

[CreateAssetMenu(fileName = "DiscoveryTrackNodeConfig", menuName = "Astral Planes/Discovery Track Node Config")]
public class DiscoveryTrackNodeConfig : ScriptableObject
{
    [Header("Motion")]
    public float driftSpeedMultiplier = 0.35f;

    [Header("Flee Boundary Targeting")]
    [Tooltip("How strongly fleeing node steers toward its sought border gap.")]
    [Range(0f, 1f)] public float fleeTowardBoundaryWeight = 0.45f;

    [Header("Hit Stun")]
    [Tooltip("Seconds after any Vehicle hit during which the node dashes away from the vehicle instead of seeking an exit.")]
    [Min(0f)] public float hitStunDuration = 0.8f;
    [Tooltip("Speed multiplier applied while dashing away during the stun window.")]
    [Min(1f)] public float hitStunSpeedMultiplier = 1.6f;

    [Header("Expiry")]
    [Tooltip("Radius in grid cells within which hidden dust matching this node's role is revealed on expiry.")]
    [Min(0)] public int expireBlastRadiusCells = 5;

    [Header("NoteSet-Driven Motion")]
    public bool driveCarvingMotionFromNoteSet = true;

    [Header("Null-Profile Fallbacks")]
    [Tooltip("Used only when no DiscoveryTrackNodeLocomotionProfile resolves for this node's role (e.g. MusicalRole.None).")]
    [Min(1)] public int defaultStrength = 100;
    [Tooltip("Used only when no DiscoveryTrackNodeLocomotionProfile resolves and no NoteSet override applies. 0 = never.")]
    [Min(0)] public int defaultExpireAfterLoops = 0;

    [Header("Locomotion Archetype Library")]
    [Tooltip("Fallback bucket, indexed by role speed 0-1, used when a role's MusicalRoleProfile has no direct mineNodeLocomotionProfile override.")]
    public DiscoveryTrackNodeLocomotionProfile[] locomotionArchetypeProfiles = new DiscoveryTrackNodeLocomotionProfile[4];
    public DiscoveryTrackNodeLocomotionProfile defaultLocomotionProfile;
    public DiscoveryTrackNodeDecisionArchetypeLibrary decisionArchetypeLibrary;
}
