using System;
using UnityEngine;
using System.Collections;
using Gameplay.Mining;
using UnityEngine.Rendering.Universal;
using Random = UnityEngine.Random;
[Serializable]

public class MinedObject : MonoBehaviour
{
    public SpriteRenderer sprite;
    public InstrumentTrack assignedTrack;
    private Collider2D minedCollider;
    private int hitsRequired = 3;
    public MinedObjectType minedObjectType;
    public TrackModifierType? trackModifierType;
    public MusicalRole musicalRole;
    [SerializeField] private NoteSetSeries noteSetSeries;
    private NoteSet currentNoteSet;
    private bool wasCollected = false;
    
    private void Start()
    {
        if (minedObjectType == MinedObjectType.NoteSpawner)
        {
            sprite.enabled = false;
        }
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
        AssignVisuals();

    }

    void OnDestroy()
    {
        if (assignedTrack.drumTrack != null)
        {
            assignedTrack.drumTrack.UnregisterMinedObject(this);
        }
    }

    public void EnableColliderAfterDelay(float delay)
    {
        Collider2D collider2D = GetComponent<Collider2D>();
        if(collider2D != null)
        StartCoroutine(EnableColliderCoroutine(collider2D, delay));
    }

    private IEnumerator EnableColliderCoroutine(Collider2D col, float delay)
    {
        yield return new WaitForSeconds(delay);
        col.enabled = true; // ✅ Collider is enabled after delay
    }
    public void AssignVisuals()
    {
        sprite.color = assignedTrack.trackColor;
        switch (minedObjectType)
        {
            case MinedObjectType.NoteSpawner:
                sprite.enabled = false;
                break;
        }

    }
    public void Initialize(MinedObjectType type, InstrumentTrack track, NoteSetSeries noteSetSeries = null, TrackModifierType? modifier = null)
    {
        minedObjectType = type;
        assignedTrack = track;
        this.noteSetSeries = noteSetSeries;
        this.trackModifierType = modifier;
        var explode = GetComponent<Explode>();
        if (explode != null)
        {
            explode.ApplyLifetimeProfile(LifetimeProfile.GetProfile(MinedObjectType.TrackUtility));
        }

        TrackUtilityMinedObject trackUtilityMinedObject = GetComponent<TrackUtilityMinedObject>();
        NoteSpawnerMinedObject noteSpawnerMinedObject = GetComponent<NoteSpawnerMinedObject>();

        if (noteSpawnerMinedObject != null)
        {
            NoteSet selectedNoteSet = noteSetSeries.GetRandomOrCuratedNoteSet();
            noteSpawnerMinedObject.Initialize(assignedTrack, selectedNoteSet, noteSetSeries);
        }
        
        MinedObjectVisualEffectController visualEffectController = GetComponent<MinedObjectVisualEffectController>();
        if (visualEffectController != null)
        {
            visualEffectController.Initialize(this);
        }            

        AssignVisuals();
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


    protected virtual void OnCollected(Vehicle vehicle)
    {


        // Default behavior
        Destroy(gameObject);
    }

    
}
