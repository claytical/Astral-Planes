using System.Linq;
using UnityEngine;

public abstract class DrumVolumeInfluence : ScriptableObject
{
    public string influenceName;
    [Range(0f, 1f)] public float weight = 1f;

    // Should return a volume contribution between 0 and 1
    public abstract float EvaluateVolume(DrumTrack drumTrack);
}
