using UnityEngine;

public class NoteSetFactory : MonoBehaviour
{
    public NoteSetConfigLibrary configLibrary;
    public GameObject noteSetPrefab; // Empty prefab with NoteSet + InstrumentTrack assigned later

// ---- helpers (same class) ----

private int ChooseRootForRole(InstrumentTrack track)
{
    // Keep inside the allowed range but bias by role so registers make sense.
    int lo = track.lowestAllowedNote;
    int hi = track.highestAllowedNote;
    int mid = (lo + hi) / 2;

    return track.assignedRole switch
    {
        MusicalRole.Bass    => Mathf.Clamp(lo + 7, lo, hi),      // low/bottom heavy
        MusicalRole.Groove  => Mathf.Clamp(mid - 5, lo, hi),     // low–mid
        MusicalRole.Harmony => Mathf.Clamp(mid, lo, hi),         // mid
        MusicalRole.Lead    => Mathf.Clamp(hi - 7, lo, hi),      // high/soars
        _                   => Mathf.Clamp(mid, lo, hi),
    };
}

private ScaleType DefaultScaleForRole(MusicalRole role)
{
    return role switch
    {
        MusicalRole.Bass    => ScaleType.Minor,       // thicker foundation
        MusicalRole.Groove  => ScaleType.Dorian,      // funk-friendly
        MusicalRole.Harmony => ScaleType.Major,       // lush chord beds
        MusicalRole.Lead    => ScaleType.Mixolydian,  // lead-friendly
        _                   => ScaleType.Major,
    };
}

private ChordPattern DefaultChordForRole(MusicalRole role)
{
    return role switch
    {
        MusicalRole.Bass    => ChordPattern.Fifths,       // root+5th works great for bass
        MusicalRole.Groove  => ChordPattern.Fifths,
        MusicalRole.Harmony => ChordPattern.RootTriad,    // triads/sevenths for harmony
        MusicalRole.Lead    => ChordPattern.Arpeggiated,  // melodic motion
        _                   => ChordPattern.RootTriad,
    };
}

private RhythmStyle DefaultRhythmForRole(MusicalRole role)
{
    return role switch
    {
        MusicalRole.Bass    => RhythmStyle.FourOnTheFloor, // steady foundation
        MusicalRole.Groove  => RhythmStyle.Breakbeat,      // movement/drive
        MusicalRole.Harmony => RhythmStyle.Steady,         // sustained, supportive
        MusicalRole.Lead    => RhythmStyle.Syncopated,     // playfulness up top
        _                   => RhythmStyle.Steady,
    };
}

private int? DominantSuggestion(int rootMidi, ScaleType scale, InstrumentTrack track)
{
    // Pick a musically safe dominant (5th above the root) if it fits the track range.
    int fifth = rootMidi + 7;
    if (fifth >= track.lowestAllowedNote && fifth <= track.highestAllowedNote)
        return fifth;
    return null;
}

    public NoteSet Generate(InstrumentTrack track, MusicalPhase phase, RemixUtility remixUtility = null)
    {
        var config = configLibrary.GetConfig(track.assignedRole, phase);
        if (config == null)
        {
            Debug.LogWarning($"No config found for {track.assignedRole} in {phase}");
            return null;
        }

        var noteSetGO = Instantiate(noteSetPrefab);
        var noteSet = noteSetGO.GetComponent<NoteSet>();

        noteSet.assignedInstrumentTrack = track;
        noteSet.noteBehavior = config.noteBehavior;
        noteSet.scale = config.GetRandomScale();
        noteSet.chordPattern = config.GetRandomChordPattern();
        noteSet.rhythmStyle = config.GetRandomRhythmStyle();
        noteSet.rootMidi = 60;//track.GetDefaultRoot(); // You may randomize if allowed
        noteSet.remixUtility = remixUtility;
        int steps = track.drumTrack.totalSteps;
        noteSet.Initialize(track, steps);

        return noteSet;
    }
}
