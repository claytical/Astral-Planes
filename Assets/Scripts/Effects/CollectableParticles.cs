using UnityEngine;

public class CollectableParticles : MonoBehaviour
{
    public ParticleSystem particleSystem; // Assign in prefab
    private ParticleSystem.MainModule main;
    private ParticleSystem.EmissionModule emission;
    private ParticleSystem.ShapeModule shape;
    private ParticleSystem.ColorOverLifetimeModule colorOverLifetime;
    private NoteTether tetherRef;
    public ParticleSystem coreParticleSystem;
    [SerializeField] private float pullStrength = 0.7f;  // force toward tether
    [SerializeField] private float baseUpSpeed  = 0.35f; // fountain upward component

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
    public void EmitZap()
    {
        if (particleSystem == null) return;

        var burst = new ParticleSystem.Burst(0f, 8); // 8 sparks immediately
        emission.SetBursts(new[] { burst });
        particleSystem.Play();
    }
    public void SetEmissionActive(bool isActive)
    {
        if (particleSystem == null) return;

        if (isActive)
        {
            emission.rateOverTime = 10f; // or whatever suits your looped look
            if (!particleSystem.isPlaying)
                particleSystem.Play();
        }
        else
        {
            emission.rateOverTime = 0f;
            // This combination forces emission to halt while allowing particles to fade naturally
            particleSystem.Stop(withChildren: false, ParticleSystemStopBehavior.StopEmitting);
        }
    }
    public void SetGravityForBeat(bool isOnBeat)
    {
        if (particleSystem == null) return;

        var main = particleSystem.main;
        main.gravityModifier = isOnBeat ? 0f : 2.5f; // tweak the gravity value to taste
    }

    public void Configure(NoteSet noteSet) {
    if (particleSystem == null || noteSet == null) return;
    // 🟣 Color tint — use track color with soft gradient
    Gradient gradient = new Gradient();
    Color baseColor = noteSet.assignedInstrumentTrack.trackColor;
    Debug.Log($"[Particles] Playing at {transform.position}, color: {baseColor}");
    baseColor.a = 1f;
    gradient.SetKeys(
        new[] {
            new GradientColorKey(baseColor, 0f),
            new GradientColorKey(baseColor * 0.9f, 1f)
        },
        new[] {
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
public void ConfigureByDuration(NoteSet noteSet, int durationTicks, InstrumentTrack track)
{
    if (!particleSystem || noteSet == null || track == null) return;

    // --- duration in seconds (respects loop multipliers) ---
    int totalSteps     = Mathf.Max(1, track.GetTotalSteps());
    float stepsPerBeat = Mathf.Max(1f, totalSteps / 4f);
    int ticksPerStep   = Mathf.Max(1, Mathf.RoundToInt(480f / stepsPerBeat));
    int durSteps       = Mathf.Max(1, durationTicks / ticksPerStep);

    float loopSeconds  = Mathf.Max(0.0001f, track.drumTrack.GetLoopLengthInSeconds());
    float secPerStep   = loopSeconds / totalSteps;
    float durationSec  = Mathf.Clamp(durSteps * secPerStep, 0.05f, loopSeconds);

    // Modules (always fetch fresh)
    var main  = particleSystem.main;
    var emis  = particleSystem.emission;
    var shape = particleSystem.shape;
    var col   = particleSystem.colorOverLifetime;
    var sizeL = particleSystem.sizeOverLifetime;
    var noise = particleSystem.noise;

    // Color/gradient (track tint with a soft white core)
    Color c = track.trackColor;
    if (coreParticleSystem != null)
    {
        ParticleSystem.MainModule core = coreParticleSystem.main;
        core.startColor = track.trackColor;
    }

    var g = new Gradient();
    g.SetKeys(
        new [] {
            new GradientColorKey(c, 0f),
            new GradientColorKey(Color.Lerp(c, Color.white, 0.2f), 0.45f),
            new GradientColorKey(c, 1f)
        },
        new [] {
            new GradientAlphaKey(0f, 0f),
            new GradientAlphaKey(0.9f, 0.12f),
            new GradientAlphaKey(0.75f, 0.85f),
            new GradientAlphaKey(0f, 1f)
        }
    );
    col.enabled = true;
    col.color   = g;

    bool isShort = durationSec <= 0.20f;
    bool isLong  = durationSec >= 0.60f;

    // --- Fountain baseline ---
    main.simulationSpace = ParticleSystemSimulationSpace.World;
    main.startLifetime   = Mathf.Clamp(durationSec * (isLong ? 1.1f : 0.8f), 0.14f, 2.0f);
    main.startSpeed      = baseUpSpeed * (isLong ? 0.8f : 1.2f); // short pops rise a bit quicker
    main.startSize       = new ParticleSystem.MinMaxCurve(isLong ? 0.12f : 0.08f,
                                                          isLong ? 0.26f : 0.16f);
    main.maxParticles    = Mathf.Max(1024, main.maxParticles);
    main.gravityModifier = 0f;

    // Upward cone with a narrow angle (feels “pressurized”)
    shape.enabled   = true;
    shape.shapeType = ParticleSystemShapeType.Cone;
    shape.radius    = 0.05f;
    shape.angle     = 12f;
    shape.position  = Vector3.zero;
    shape.rotation  = Vector3.zero; // local +Y

    // Gentle turbulence so it feels raw
    noise.enabled     = true;
    noise.strength    = 0.2f;
    noise.frequency   = 0.5f;
    noise.scrollSpeed = 0.6f;

    // Breath over lifetime
    sizeL.enabled = true;
    sizeL.size = new ParticleSystem.MinMaxCurve(
        1f,
        new AnimationCurve(
            new Keyframe(0f, 0f),
            new Keyframe(0.18f, 1f),
            new Keyframe(0.85f, isLong ? 0.9f : 0.7f),
            new Keyframe(1f, 0f)
        )
    );

    // Emission: sustained for long notes, one-shot for very short
    emis.enabled = true;
    emis.rateOverTime = isShort ? 0f : Mathf.Clamp(4f + durSteps * 0.8f, 5f, 20f);

    // Short pop without SetBursts (avoids the crash)
    if (isShort)
    {
        if (!particleSystem.isPlaying) particleSystem.Play();
        int burstCount = Mathf.Clamp(6 + durSteps, 4, 18);
        particleSystem.Emit(burstCount);
    }
    else if (!particleSystem.isPlaying)
    {
        particleSystem.Play();
    }
}
public void RegisterTether(NoteTether tether, float pull = 0.7f)
{
    tetherRef = tether;
    pullStrength = pull;
}

void LateUpdate()
{
    if (!particleSystem) return;

    // Pull everything slightly toward the tether endpoint (if present)
    var fol = particleSystem.forceOverLifetime;
    if (tetherRef && tetherRef.end)  // attraction target = marker end
    {
        Vector3 dir = (tetherRef.end.position - transform.position).normalized;
        fol.enabled = true;
        fol.space   = ParticleSystemSimulationSpace.World;

        // apply a constant directional force (small!)
        fol.x = new ParticleSystem.MinMaxCurve(dir.x * pullStrength);
        fol.y = new ParticleSystem.MinMaxCurve(dir.y * pullStrength);
        fol.z = new ParticleSystem.MinMaxCurve(dir.z * pullStrength);
    }
    else
    {
        fol.enabled = false; // no tether yet: pure fountain
    }
}

public void SetDripDirection(Vector3 worldDir, float baseSpeed, float gravity = 0f)
{
    if (!particleSystem) return;

    worldDir = worldDir.sqrMagnitude < 1e-6f ? Vector3.down : worldDir.normalized;

    var main = particleSystem.main;
    main.simulationSpace = ParticleSystemSimulationSpace.World;
    main.gravityModifier = gravity;

    var vel = particleSystem.velocityOverLifetime;
    vel.enabled = true;

    // push along line, then ease out
    vel.x = new ParticleSystem.MinMaxCurve(worldDir.x);
    vel.y = new ParticleSystem.MinMaxCurve(worldDir.y);
    vel.z = new ParticleSystem.MinMaxCurve(worldDir.z);

    var speed = new AnimationCurve(
        new Keyframe(0f, baseSpeed * 1.0f),
        new Keyframe(0.25f, baseSpeed * 0.8f),
        new Keyframe(1f, baseSpeed * 0.2f)
    );
    vel.speedModifier = new ParticleSystem.MinMaxCurve(1f, speed);

    var limit = particleSystem.limitVelocityOverLifetime;
    limit.enabled = true;
    limit.dampen  = 0.6f; // keeps them hugging the line
}

}
