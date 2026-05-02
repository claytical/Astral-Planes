using System;
using UnityEngine;

[CreateAssetMenu(menuName = "Astral Planes/Mine Node Decision Archetype Library", fileName = "MineNodeDecisionArchetypes")]
public class MineNodeDecisionArchetypeLibrary : ScriptableObject
{
    [Serializable]
    public struct Archetype
    {
        public string id;

        [Header("Timing")]
        public Vector2 reactionDelayWindow;
        [Min(0f)] public float pathCommitmentDuration;

        [Header("Direction Variance")]
        [Range(0f, 45f)] public float turnJitter;
        [Range(0f, 1f)] public float fleeBias;

        [Header("Recovery & Risk")]
        [Range(0f, 1f)] public float stallRecoveryAggressiveness;
        [Range(0f, 1f)] public float dustRiskTolerance;

        public float SampleReactionDelay()
        {
            float min = Mathf.Min(reactionDelayWindow.x, reactionDelayWindow.y);
            float max = Mathf.Max(reactionDelayWindow.x, reactionDelayWindow.y);
            return UnityEngine.Random.Range(min, max);
        }
    }

    [Header("Archetypes")]
    public Archetype[] archetypes =
    {
        new Archetype
        {
            id = "Skittish",
            reactionDelayWindow = new Vector2(0.02f, 0.15f),
            pathCommitmentDuration = 0.30f,
            turnJitter = 18f,
            fleeBias = 0.90f,
            stallRecoveryAggressiveness = 0.95f,
            dustRiskTolerance = 0.25f,
        },
        new Archetype
        {
            id = "Steady",
            reactionDelayWindow = new Vector2(0.12f, 0.25f),
            pathCommitmentDuration = 0.85f,
            turnJitter = 6f,
            fleeBias = 0.55f,
            stallRecoveryAggressiveness = 0.60f,
            dustRiskTolerance = 0.50f,
        },
        new Archetype
        {
            id = "Aggressive",
            reactionDelayWindow = new Vector2(0.05f, 0.16f),
            pathCommitmentDuration = 0.70f,
            turnJitter = 10f,
            fleeBias = 0.70f,
            stallRecoveryAggressiveness = 0.85f,
            dustRiskTolerance = 0.85f,
        },
        new Archetype
        {
            id = "Darting",
            reactionDelayWindow = new Vector2(0.03f, 0.12f),
            pathCommitmentDuration = 0.22f,
            turnJitter = 22f,
            fleeBias = 0.80f,
            stallRecoveryAggressiveness = 0.90f,
            dustRiskTolerance = 0.35f,
        }
    };

    public bool TryGet(string id, out Archetype archetype)
    {
        if (archetypes != null)
        {
            for (int i = 0; i < archetypes.Length; i++)
            {
                if (string.Equals(archetypes[i].id, id, StringComparison.OrdinalIgnoreCase))
                {
                    archetype = archetypes[i];
                    return true;
                }
            }
        }

        archetype = default;
        return false;
    }
}
