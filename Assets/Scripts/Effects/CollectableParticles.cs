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
    // 🟣 Color tint — use track color with soft gradient
    Gradient gradient = new Gradient();
    Color baseColor = noteSet.assignedInstrumentTrack.trackColor;
    Debug.Log($"[Particles] Playing at {transform.position}, color: {baseColor}");
    baseColor.a = 1f;
    gradient.SetKeys(
        new GradientColorKey[] {
            new GradientColorKey(baseColor, 0f),
            new GradientColorKey(baseColor * 0.9f, 1f)
        },
        new GradientAlphaKey[] {
            new GradientAlphaKey(0f, 0f),
            new GradientAlphaKey(0.8f, 0.2f),
            new GradientAlphaKey(0.7f, 0.8f),
            new GradientAlphaKey(0f, 1f)
        }
    );
    colorOverLifetime.enabled = true;
    colorOverLifetime.color = gradient;

    // 💫 RhythmStyle → emission density (musical "breath")
    emission.rateOverTime = noteSet.rhythmStyle switch
    {
        RhythmStyle.FourOnTheFloor => 6f,
        RhythmStyle.Dense => 14f,
        RhythmStyle.Sparse => 3f,
        RhythmStyle.Swing => 5f,
        RhythmStyle.Syncopated => 7f,
        _ => 4f
    };

    // 🔊 NoteBehavior → motion shape and energy
    shape.shapeType = ParticleSystemShapeType.Cone;
    shape.radius = 0.1f;

    float speed = 0.5f;
    float angle = 15f;

    switch (noteSet.noteBehavior)
    {
        case NoteBehavior.Bass:
            angle = 5f;
            speed = 0.2f;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = 0.05f; // tighter radius
            break;
        case NoteBehavior.Drone:
            speed = 0.05f;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = 0.1f; // looser but still limited
            break;
        case NoteBehavior.Lead:
            angle = 60f;
            speed = 1.2f;
            shape.radius = 0.05f;
            break;
        case NoteBehavior.Harmony:
            shape.shapeType = ParticleSystemShapeType.Donut;
            shape.radius = 0.07f;
            break;
        case NoteBehavior.Percussion:
            angle = 90f;
            speed = 2f;
            shape.radius = 0.05f;
            break;
        default:
            shape.radius = 0.1f;
            break;
    }


    shape.angle = angle;
    main.startSpeed = speed;
    main.startLifetime = 1.2f;
    main.startSize = new ParticleSystem.MinMaxCurve(0.1f, 0.3f);
    main.startRotation = new ParticleSystem.MinMaxCurve(0, 360 * Mathf.Deg2Rad);
    main.simulationSpace = ParticleSystemSimulationSpace.Local;

    // 🫧 Soft breathing burst
    var sizeOverLifetime = particleSystem.sizeOverLifetime;
    sizeOverLifetime.enabled = true;
    AnimationCurve growShrink = new AnimationCurve(
        new Keyframe(0f, 0f),
        new Keyframe(0.2f, 1f),
        new Keyframe(0.8f, 0.8f),
        new Keyframe(1f, 0f)
    );
    sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, growShrink);

    particleSystem.Play();
}

}
