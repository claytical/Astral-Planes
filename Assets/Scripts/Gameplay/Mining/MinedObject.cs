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
    public SpriteRenderer sprite;
  
    public InstrumentTrack assignedTrack;
    private NoteSet targetNoteSet;
    private Collider2D minedCollider;

    private void Start()
    {
        sprite.enabled = false;
        minedCollider = GetComponent<Collider2D>();
        if (minedCollider != null)
        {
            minedCollider.enabled = false; // ✅ Start with disabled collider
        }
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

            case MinedObjectType.TrackUtility:
                return MinedObjectCategory.TrackUtility;

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
            case MinedObjectCategory.TrackUtility:
                AssignUtilityVisuals();
                break;

            case MinedObjectCategory.NoteSpawner:
                break;

            default:
                Debug.LogWarning($"No visuals assigned for category {category}");
                break;
        }
    }
    private void AssignUtilityVisuals()
    {
        var trackUtility = GetComponent<TrackUtilityMinedObject>();
        if (trackUtility != null)
        {
            switch (trackUtility.type)
            {
                case TrackModifierType.Clear:
                    StartCoroutine(FadeOutGlow());
                    break;
                case  TrackModifierType.Expansion:
                    StartCoroutine(PulseExpand());
                    break;
                case  TrackModifierType.Contract:
                    StartCoroutine(JitterEffect());
                    break;
            }
        }
    }

    private void ApplyEffect()
    {
        if (assignedTrack == null) {
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
    //    Vector3 expanded = start * 1.4f;

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

    private void OnTriggerEnter2D(Collider2D coll)
    {
        Vehicle vehicle = coll.gameObject.GetComponent<Vehicle>();
        if (vehicle != null)
        {
            ApplyEffect();
            vehicle.TeleportToRandomCell();
      
        }
    }
    


}
