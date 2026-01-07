using UnityEngine;

public class NoteSpawnerMinedObject : MonoBehaviour
{
    public MusicalRoleProfile musicalRole;
    public InstrumentTrack assignedTrack;
    public NoteSet selectedNoteSet;
    
    private PhaseStar _ownerStar;
    private bool _isDark;
    private bool _burstSpawned;
    public void BindPhaseStar(PhaseStar star) => _ownerStar = star;
    public void Initialize(InstrumentTrack track, NoteSet noteSet)
    {
        if (track == null) { Debug.LogWarning("NoteSpawnerMinedObject: track NULL"); return; }
        if (noteSet == null) noteSet = track.GetActiveNoteSet();
        if (noteSet == null) { Debug.LogWarning("NoteSpawnerMinedObject: no NoteSet available"); return; }

        Debug.Log($"Initializing Spawner Mined Object");
        assignedTrack = track;
        musicalRole = MusicalRoleProfileLibrary.GetProfile(track.assignedRole);
        selectedNoteSet = noteSet;
        track.SetNoteSet(noteSet);
        if (track == null || noteSet == null)
        {
            Debug.LogWarning("NoteSpawnerMinedObject initialized with missing track or NoteSet.");
            return;
        }

        if (selectedNoteSet != null)
        {
            Debug.Log($"Selected Note Set on Spawner is {selectedNoteSet} for {track} with {musicalRole}");
            selectedNoteSet.assignedInstrumentTrack = track;
            selectedNoteSet.Initialize(track, track.GetTotalSteps());
        }
        
        Explode explode = GetComponent<Explode>();
        if (explode != null)
        {
            explode.ApplyLifetimeProfile(LifetimeProfile.GetProfile(MinedObjectType.NoteSpawner));
        }
    }


    void OnCollisionEnter2D(Collision2D coll)
    {
        Debug.Log($"NoteSpawnerMinedObject.OnCollisionEnter2D: {coll.gameObject.name}");
//        CollectionSoundManager.Instance?.PlayNoteSpawnerSound(assignedTrack, selectedNoteSet);
    }
    public void SetDarkMode(bool detune, bool bitcrush, float gainDb)
            {
                _isDark = true;
                // TODO: Route to a "Dark" mixer bus, apply pitch offset or ring-mod/bitcrush,
                    // and set gain. Visual tint could also change here.
                        // Keep it subtle so it’s tense but not harsh.
                       }

        // Star may re-enable collider when entering cleanup hold.
    public void EnablePickup(bool on) {
            var col = GetComponent<Collider2D>();
            if (col) col.enabled = on;
            // Also toggle any glow/outline to signal “collect me now”
           }
    

}
