using UnityEngine;

[CreateAssetMenu(fileName = "DrumLoopPatternVisual", menuName = "Drums/Drum Loop Pattern Visual", order = 1)]
public class DrumLoopPatternVisual : ScriptableObject
{
    public DrumLoopPattern patternType;

    public Color color = Color.white;
    public ParticleSystem particleEffectPrefab;

    [Range(0.1f, 5f)]
    public float pulseSpeed = 1f;

    [Range(1f, 2f)]
    public float pulseScale = 1.2f;
}