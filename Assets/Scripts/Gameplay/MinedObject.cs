using UnityEngine;
using System.Collections;
public enum MinedObjectCategory {
    NoteSpawner,
    TrackUtility,
    NoteModifier
}

public class MinedObject : MonoBehaviour
{
    public MinedObjectType minedObjectType;
    public ParticleSystem visualEffect;
    public Material baseMaterial;
    public SpriteRenderer sprite;
  
    public InstrumentTrack assignedTrack;
    private NoteSet targetNoteSet;
    private Collider2D minedCollider;
    private Vector3 originalScale;
    private float animationTime = 0.5f; // Used for animations like pulsing

    private void Start()
    {
        minedCollider = GetComponent<Collider2D>();
        if (minedCollider != null)
        {
            minedCollider.enabled = false; // ✅ Start with disabled collider
        }
        originalScale = transform.localScale;
        // ⏱️ Apply lifetime from profile if Explode is present
        var explode = GetComponent<Explode>();
        if (explode != null)
        {
            LifetimeProfile profile = LifetimeProfile.GetProfile(minedObjectType);
            explode.ApplyLifetimeProfile(profile);
        }        

    }
    public void AssignTrack(InstrumentTrack track)
    {
        assignedTrack = track;
        if (assignedTrack.drumTrack != null)
        {
            assignedTrack.drumTrack.RegisterMinedObject(this);
        }
        AssignVisuals();

    }

    void OnDestroy()
    {
        if (assignedTrack.drumTrack != null)
        {
            assignedTrack.drumTrack.UnregisterMinedObject(this);
        }
    }

    public static MinedObjectCategory GetCategory(MinedObjectType type)
    {
        switch (type)
        {
            case MinedObjectType.NoteSpawner:
                return MinedObjectCategory.NoteSpawner;

            case MinedObjectType.TrackClear:
            case MinedObjectType.LoopExpansion:
            case MinedObjectType.AntiNoteSpawner:
                return MinedObjectCategory.TrackUtility;

            case MinedObjectType.ChordChange:
            case MinedObjectType.NoteBehaviorChange:
            case MinedObjectType.RootShift:
            case MinedObjectType.RhythmStyleChange:
            case MinedObjectType.Shoft:
                return MinedObjectCategory.NoteModifier;

            default:
                Debug.LogWarning($"⚠️ Unhandled MinedObjectType: {type}. Defaulting to NoteModifier.");
                return MinedObjectCategory.NoteModifier;
        }
    }

    
    public void EnableColliderAfterDelay(float delay)
    {
        StartCoroutine(EnableColliderCoroutine(delay));
    }

    private IEnumerator EnableColliderCoroutine(float delay)
    {
        yield return new WaitForSeconds(delay);
        
        if (minedCollider != null)
        {
            minedCollider.enabled = true; // ✅ Collider is enabled after delay
        }
    }

    private void AssignVisuals()
    {
        MinedObjectCategory category = GetCategory(minedObjectType);
        sprite.color = assignedTrack.trackColor;
        switch (category)
        {
            case MinedObjectCategory.NoteModifier:
                AssignModifierVisuals();
                break;

            case MinedObjectCategory.TrackUtility:
                AssignUtilityVisuals();
                break;

            case MinedObjectCategory.NoteSpawner:
                AssignSpawnerVisuals();
                break;

            default:
                Debug.LogWarning($"No visuals assigned for category {category}");
                break;
        }
    }
    private void AssignModifierVisuals()
    {
        switch (minedObjectType)
        {
            case MinedObjectType.ChordChange:
                StartCoroutine(RotateEffect());
                break;
            case MinedObjectType.RootShift:
                StartCoroutine(BounceEffect());
                break;
            case MinedObjectType.NoteBehaviorChange:
                StartCoroutine(PulseEffect());
                break;
            case MinedObjectType.RhythmStyleChange:
                StartCoroutine(PulseGlow());
                break;
        }
    }
    private void AssignUtilityVisuals()
    {
        switch (minedObjectType)
        {
            case MinedObjectType.TrackClear:
                StartCoroutine(FadeOutGlow());
                break;
            case MinedObjectType.LoopExpansion:
                StartCoroutine(PulseExpand());
                break;
            case MinedObjectType.AntiNoteSpawner:
                StartCoroutine(JitterEffect());
                break;
        }
    }
    private void AssignSpawnerVisuals()
    {
        StartCoroutine(GlowPulseLoop()); // universal for all NoteSpawner types
    }

    private IEnumerator RotateEffect()
    {
        while (true)
        {
            transform.Rotate(0, 0, 30 * Time.deltaTime);
            yield return null;
        }
    }

    private IEnumerator BounceEffect()
    {
        while (true)
        {
            float newY = originalScale.y + 0.3f * Mathf.Sin(Time.time * 3f);
            transform.localScale = new Vector3(originalScale.x, newY, originalScale.z);
            yield return null;
        }
    }

    private IEnumerator PulseEffect()
    {
        while (true)
        {
            float scaleFactor = 1.1f + 0.05f * Mathf.Sin(Time.time * 5f);
            transform.localScale = originalScale * scaleFactor;
            yield return null;
        }
    }

    private IEnumerator PulseGlow()
    {
        while (true)
        {
            baseMaterial.SetFloat("_GlowStrength", 1.2f);
            yield return new WaitForSeconds(0.5f);
            baseMaterial.SetFloat("_GlowStrength", 0.8f);
            yield return new WaitForSeconds(0.5f);
        }
    }

public void ApplyEffect()
{
    if (assignedTrack == null)
    {
        Debug.LogWarning($"{gameObject.name} - No track assigned.");
        return;
    }

    MinedObjectCategory category = GetCategory(minedObjectType);

    switch (category)
    {
        case MinedObjectCategory.NoteSpawner:
            NoteSpawnerMinedObject spawner = GetComponent<NoteSpawnerMinedObject>();
            if (spawner != null)
            {
                spawner.OnCollected();
            }
            else
            {
                Debug.LogWarning("NoteSpawnerMinedObject missing from NoteSpawner.");
            }
            break;

        case MinedObjectCategory.TrackUtility:
            TrackUtilityMinedObject utility = GetComponent<TrackUtilityMinedObject>();
            if (utility != null)
            {
                utility.OnCollected();
            }
            else
            {
                Debug.LogWarning("TrackUtilityMinedObject missing from TrackUtility item.");
            }
            break;

        case MinedObjectCategory.NoteModifier:
            NoteSet activeNoteSet = assignedTrack.GetCurrentNoteSet();
            if (activeNoteSet == null)
            {
                Debug.LogWarning("No active NoteSet to modify.");
                return;
            }

            switch (minedObjectType)
            {
                case MinedObjectType.ChordChange:
                    activeNoteSet.AdvanceChord();
                    break;

                case MinedObjectType.RootShift:
                    activeNoteSet.rootMidi += (Random.Range(0, 2) == 0) ? -12 : 12;
                    break;

                case MinedObjectType.NoteBehaviorChange:
                    activeNoteSet.noteBehavior = GetRandomNoteBehavior();
                    break;

                case MinedObjectType.RhythmStyleChange:
                    activeNoteSet.rhythmStyle = GetRandomRhythmStyle();
                    break;

                default:
                    Debug.LogWarning("Unhandled NoteModifier type: " + minedObjectType);
                    break;
            }
            break;

        default:
            Debug.LogWarning("Unknown MinedObjectCategory: " + category);
            break;
    }
}

private IEnumerator FadeOutGlow()
{
    SpriteRenderer sr = sprite;
    Color original = sr.color;
    float duration = 0.75f;

    for (float t = 0f; t < duration; t += Time.deltaTime)
    {
        float alpha = Mathf.Lerp(original.a, 0f, t / duration);
        sr.color = new Color(original.r, original.g, original.b, alpha);
        yield return null;
    }

    sr.color = new Color(original.r, original.g, original.b, 0f);
}
private IEnumerator PulseExpand()
{
    float duration = 0.4f;
    Vector3 start = transform.localScale;
    Vector3 expanded = start * 1.4f;

    for (float t = 0f; t < duration; t += Time.deltaTime)
    {
        float scale = Mathf.SmoothStep(1f, 1.4f, Mathf.Sin(t / duration * Mathf.PI));
        transform.localScale = start * scale;
        yield return null;
    }

    transform.localScale = start;
}
private IEnumerator JitterEffect()
{
    Vector3 originalPos = transform.localPosition;
    float duration = 0.3f;
    float magnitude = 0.05f;

    for (float t = 0f; t < duration; t += Time.deltaTime)
    {
        float offsetX = Random.Range(-magnitude, magnitude);
        float offsetY = Random.Range(-magnitude, magnitude);
        transform.localPosition = originalPos + new Vector3(offsetX, offsetY, 0);
        yield return null;
    }

    transform.localPosition = originalPos;
}
private IEnumerator GlowPulseLoop()
{
    SpriteRenderer sr = sprite;
    float duration = 1f;
    float pulseSpeed = 2f;

    while (true)
    {
        float pulse = (Mathf.Sin(Time.time * pulseSpeed) + 1f) / 2f; // 0–1
        float alpha = Mathf.Lerp(0.5f, 1f, pulse);
        sr.color = new Color(sr.color.r, sr.color.g, sr.color.b, alpha);
        yield return null;
    }
}

private NoteBehavior GetRandomNoteBehavior()
{
    var values = System.Enum.GetValues(typeof(NoteBehavior));
    return (NoteBehavior)values.GetValue(Random.Range(0, values.Length));
}

private RhythmStyle GetRandomRhythmStyle()
{
    var values = System.Enum.GetValues(typeof(RhythmStyle));
    return (RhythmStyle)values.GetValue(Random.Range(0, values.Length));
}


    private void OnTriggerEnter2D(Collider2D coll)
    {
        if (coll.gameObject.GetComponent<Vehicle>())
        {
            ApplyEffect();
            Destroy(gameObject);
        }
    }
}
