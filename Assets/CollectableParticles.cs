using UnityEngine;

public class CollectableParticles : MonoBehaviour
{
    public ParticleSystem particleSystem; // Assign in prefab
    private ParticleSystem.MainModule main;
    private ParticleSystem.EmissionModule emission;
    private ParticleSystem.ShapeModule shape;
    private ParticleSystem.ColorOverLifetimeModule colorOverLifetime;

    void Awake()
    {
        if (particleSystem == null)
            particleSystem = GetComponentInChildren<ParticleSystem>();

        if (particleSystem != null)
        {
            main = particleSystem.main;
            emission = particleSystem.emission;
            shape = particleSystem.shape;
            colorOverLifetime = particleSystem.colorOverLifetime;
        }
    }

    public void Configure(NoteSet noteSet)
    {
        if (particleSystem == null || noteSet == null) return;

        // Example: RhythmStyle → emission rate
        switch (noteSet.rhythmStyle)
        {
            case RhythmStyle.FourOnTheFloor:
                emission.rateOverTime = 5f;
                break;
            case RhythmStyle.Dense:
                emission.rateOverTime = 20f;
                break;
            case RhythmStyle.Sparse:
                emission.rateOverTime = 2f;
                break;
            case RhythmStyle.Swing:
                emission.rateOverTime = 4f;
                break;
            case RhythmStyle.Syncopated:
                emission.rateOverTime = 7f;
                break;
        }

        // Example: NoteBehavior → movement pattern
        switch (noteSet.noteBehavior)
        {
            case NoteBehavior.Bass:
                shape.angle = 10f;
                main.startSpeed = 0.3f;
                break;
            case NoteBehavior.Lead:
                shape.angle = 45f;
                main.startSpeed = 1.5f;
                break;
            case NoteBehavior.Harmony:
                shape.shapeType = ParticleSystemShapeType.Donut;
                main.startSpeed = 0.8f;
                break;
            case NoteBehavior.Drone:
                main.startSpeed = 0.1f;
                break;
            case NoteBehavior.Percussion:
                shape.angle = 90f;
                main.startSpeed = 2f;
                break;
        }

        // Optional: ScaleType → color tint
        Gradient gradient = new Gradient();
        gradient.SetKeys(
            new GradientColorKey[] {
                new GradientColorKey(GetColorForScale(noteSet.scale), 0f),
                new GradientColorKey(Color.white, 1f)
            },
            new GradientAlphaKey[] {
                new GradientAlphaKey(1f, 0f),
                new GradientAlphaKey(0f, 1f)
            }
        );
        colorOverLifetime.enabled = true;
        colorOverLifetime.color = new ParticleSystem.MinMaxGradient(gradient);

        particleSystem.Play();
    }

    private Color GetColorForScale(ScaleType scale)
    {
        switch (scale)
        {
            case ScaleType.Major: return Color.yellow;
            case ScaleType.Minor: return Color.blue;
            case ScaleType.Mixolydian: return new Color(0.8f, 0.4f, 1f);
            case ScaleType.Dorian: return new Color(0.2f, 1f, 0.6f);
            case ScaleType.Phrygian: return new Color(1f, 0.3f, 0.3f);
            case ScaleType.Lydian: return Color.cyan;
            case ScaleType.Locrian: return Color.gray;
            default: return Color.white;
        }
    }
}
