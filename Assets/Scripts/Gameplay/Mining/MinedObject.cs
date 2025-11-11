using System;
using UnityEngine;
using System.Collections;
using Gameplay.Mining;
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
    public MusicalRoleProfile roleProfile;
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

    private void AssignVisuals()
    {
        sprite.color = assignedTrack.trackColor;
    }
    public void Initialize(MinedObjectType type, InstrumentTrack track, NoteSet noteSet = null, TrackModifierType? modifier = null)
    {
        Debug.Log($"Initializing mined object of type {type} on {track} with note set {(noteSet == null ? "null" : noteSet.ToString())}");        minedObjectType = type;
        assignedTrack = track;
        this.trackModifierType = modifier;
        currentNoteSet = noteSet;
        if (noteSet != null) 
            Debug.Log($"Note set track: {noteSet.assignedInstrumentTrack}");        
        var explode = GetComponent<Explode>();
        if (explode != null)
        {
            explode.ApplyLifetimeProfile(LifetimeProfile.GetProfile(type));
        }

        TrackUtilityMinedObject trackUtilityMinedObject = GetComponent<TrackUtilityMinedObject>();
        NoteSpawnerMinedObject noteSpawnerMinedObject = GetComponent<NoteSpawnerMinedObject>();

        if (noteSpawnerMinedObject != null) {
            Debug.Log($"Initializing {nameof(NoteSpawnerMinedObject)} on {track}"); 
            // Configure spawner even if noteSet is currently null; burst happens later.
            noteSpawnerMinedObject.Initialize(assignedTrack, noteSet);
        }
        AssignVisuals();
    }

    protected virtual void OnCollected(Vehicle vehicle)
    {
        // Default behavior
        Destroy(gameObject);
    }

    
}
