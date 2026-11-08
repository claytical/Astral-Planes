using UnityEngine;

[CreateAssetMenu(menuName = "Astral Planes/Competitive Scoring Config", fileName = "CompetitiveScoringConfig")]
public class CompetitiveScoringConfig : ScriptableObject
{
    [Min(1)] public int pointsPerNote = 1;
    [Tooltip("Applied to notes placed by the vehicle that depleted the DiscoveryTrackNode, until its burst clears.")]
    [Min(1)] public int discoveryMultiplier = 2;
}
