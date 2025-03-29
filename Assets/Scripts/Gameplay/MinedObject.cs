using UnityEngine;
using System.Collections;

public enum MinedObjectType { ChordChange, RootShift, NoteBehavior, RhythmStyle }

public class MinedObject : MonoBehaviour
{
    public MinedObjectType minedObjectType;
    public GameObject[] noteSetPrefab;
    public ParticleSystem visualEffect;
    public Material baseMaterial;
    public SpriteRenderer sprite;
  
    private InstrumentTrack assignedTrack;
    private NoteSet targetNoteSet;
//    private DrumTrack drumTrack;
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
        AssignVisuals();
        
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

    public Color SetTracks(DrumTrack drums)
    {
        if (assignedTrack == null)
        {
            assignedTrack = drums.trackController.GetRandomTrack();
            drums.trackController.SetActiveTrack(assignedTrack);
// ✅ Create & Assign a new NoteSet
            GameObject spawnedNoteSet = Instantiate(noteSetPrefab[0], transform.position, Quaternion.identity);
            targetNoteSet = spawnedNoteSet.GetComponent<NoteSet>();
            targetNoteSet.assignedInstrumentTrack = assignedTrack;
            assignedTrack.SetCurrentNoteSet(targetNoteSet);
            return assignedTrack.trackColor;
        }
        return Color.white;
    }

    private void AssignVisuals()
    {
        switch (minedObjectType)
        {
            case MinedObjectType.ChordChange:
                StartCoroutine(RotateEffect());
                break;
            case MinedObjectType.RootShift:
                StartCoroutine(BounceEffect());
                break;
            case MinedObjectType.NoteBehavior:
                StartCoroutine(PulseEffect());
                break;
            case MinedObjectType.RhythmStyle:
                StartCoroutine(PulseGlow());
                break;
        }
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
            Debug.LogError($"{gameObject.name} has no assigned instrument track.");
            return;
        }
        assignedTrack.RemoveAllCollectables();
        assignedTrack.ResetTrackGridBehavior();
       
        
        // ✅ Apply effect based on type
        switch (minedObjectType)
        {
            case MinedObjectType.ChordChange:
                targetNoteSet.AdvanceChord();
                break;
            case MinedObjectType.RootShift:
                targetNoteSet.rootMidi += Random.Range(0, 2) == 0 ? -12 : 12;
                break;
            case MinedObjectType.NoteBehavior:
                targetNoteSet.noteBehavior = (NoteBehavior)Random.Range(0, System.Enum.GetValues(typeof(NoteBehavior)).Length);
                break;
            case MinedObjectType.RhythmStyle:
                targetNoteSet.rhythmStyle = (RhythmStyle)Random.Range(0, System.Enum.GetValues(typeof(RhythmStyle)).Length);
                break;
        }
        assignedTrack.BuildNoteSet();
        sprite.color = assignedTrack.trackColor;
        assignedTrack.SpawnCollectables();
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
