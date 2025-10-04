using System;
using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using Gameplay.Mining;
using MidiPlayerTK;
using Random = UnityEngine.Random;
public static class ShipTrackAssigner
{
    public static void AssignShipsToTracks(List<ShipMusicalProfile> selectedShips, List<InstrumentTrack> tracks, GameObject noteSetPrefab)
    {
        List<InstrumentTrack> unassignedTracks = new List<InstrumentTrack>(tracks);

        // Step 1: Assign presets from ships
        foreach (var ship in selectedShips)
        {
            foreach (var track in unassignedTracks.ToList())
            {
                var roleProfile = MusicalRoleProfileLibrary.GetProfile(track.assignedRole);
                if (roleProfile == null || roleProfile.allowedMidiPresets == null) continue;

                if (ship.allowedMidiPresets.Any(p => roleProfile.allowedMidiPresets.Contains(p)))
                {
                    var validPresets = ship.allowedMidiPresets
                        .Where(p => roleProfile.allowedMidiPresets.Contains(p))
                        .ToList();

                    if (validPresets.Count == 0) continue;

                    int preset = validPresets[Random.Range(0, validPresets.Count)];
                    track.preset = preset;
                    GameObject noteSetGO = GameObject.Instantiate(noteSetPrefab);
                    NoteSet noteSet = noteSetGO.GetComponent<NoteSet>();
                    noteSet.assignedInstrumentTrack = track;
                    noteSet.noteBehavior = roleProfile.defaultBehavior;
                    noteSet.Initialize(track, track.GetTotalSteps());
                    Debug.Log($"Assigned track {track.name} to preset {preset} with noteset: {noteSet} for profile {roleProfile}");

                    unassignedTracks.Remove(track);
                    break;
                }
            }
        }

        // Step 2: Assign remaining tracks randomly
        foreach (var track in unassignedTracks)
        {
            var fallbackProfile = MusicalRoleProfileLibrary.GetProfile(track.assignedRole);
            if (fallbackProfile == null || fallbackProfile.allowedMidiPresets.Count == 0) continue;

            int randomPreset = fallbackProfile.allowedMidiPresets[Random.Range(0, fallbackProfile.allowedMidiPresets.Count)];
            track.preset = randomPreset;

            GameObject noteSetGO = GameObject.Instantiate(noteSetPrefab);
            NoteSet noteSet = noteSetGO.GetComponent<NoteSet>();

            noteSet.assignedInstrumentTrack = track;
            noteSet.noteBehavior = fallbackProfile.defaultBehavior;
            noteSet.Initialize(track, track.GetTotalSteps());
                //track.SpawnCollectables(noteSet);
                Debug.Log($"Falback assigned track {track.name} to preset {randomPreset} with noteset: {noteSet} for profile {fallbackProfile}");

        }
    }
}

public class InstrumentTrackController : MonoBehaviour
{
    public InstrumentTrack[] tracks;
    public NoteVisualizer noteVisualizer;
    public GameObject noteSetPrefab;
    private bool spawningEnabled = true;

    void Start()
    {
        if (!GameFlowManager.Instance.ReadyToPlay()) {
            return;
        }
    }
    public void ConfigureTracksFromShips(List<ShipMusicalProfile> selectedShips, GameObject notesetPrefab)
    {
        
        ShipTrackAssigner.AssignShipsToTracks(selectedShips, tracks.ToList(), notesetPrefab);
        UpdateVisualizer();
    }
    public InstrumentTrack FindTrackByRole(MusicalRole role)
    {
        Debug.Log("Configured roles: " + 
                  string.Join(", ", tracks.Select(t => t.assignedRole)));
        return tracks.FirstOrDefault(t => t.assignedRole == role);
    }
    public void ApplySeedVisibility(List<InstrumentTrack> seeds)
    {
        // Example: mute non-seeds briefly
        var seedSet = new HashSet<InstrumentTrack>(seeds ?? new List<InstrumentTrack>());
        foreach (var t in tracks)
        {
            bool on = seedSet.Contains(t);
            t.SetMuted(!on); // implement SetMuted on InstrumentTrack or via your mixer
        }
        // Optionally fade unmuted ones back in over ~0.5s
    }
    private IEnumerator FadeOutMidi(MidiStreamPlayer player, float duration)
    {
        float startVolume = player.MPTK_Volume;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            player.MPTK_Volume = Mathf.Lerp(startVolume, 0f, elapsed / duration);
            yield return null;
        }

        player.MPTK_Volume = 0f;
    }
    public void ApplyPhaseSeedOutcome(MusicalPhase nextPhase, List<InstrumentTrack> seeds)
    {
        var seedSet = new HashSet<InstrumentTrack>(seeds ?? new List<InstrumentTrack>());
        foreach (var t in tracks)
        {
            if (t == null) continue;
            if (seedSet.Contains(t))
                RemixSeedForPhase(t, nextPhase);     // keep + lightly remix for the new phase
            else
                ClearTrackForNewPhase(t);            // silence + clear objects so the new star can rebuild
        }
    }
    public void SetSpawningEnabled(bool enabled) { spawningEnabled = enabled; /* gate your spawn entry points */ }
    public void UpdateVisualizer()
    {
        if (noteVisualizer == null) return;

        foreach (var track in tracks)
        {
            foreach (var (step, _, _, _) in track.GetPersistentLoopNotes())
                noteVisualizer.PlacePersistentNoteMarker(track, step);
        }
    }
    public int GetMaxLoopMultiplier()
    {
        return tracks.Max(track => track.loopMultiplier);
    }
    public void BeginGameOverFade()
    {
        foreach (var track in tracks)
        {
            if (track == null) continue;

            var loopNotes = track.GetPersistentLoopNotes();
            for (int i = 0; i < loopNotes.Count; i++)
            {
                var (step, note, _, velocity) = loopNotes[i];
                int longDuration = 1920; // ≈4 beats (1 bar) at 480 ticks per beat
                loopNotes[i] = (step, note, longDuration, velocity);
            }
            // Start fading out this track's MIDI stream
            if (track.midiStreamPlayer != null)
            {
                track.StartCoroutine(FadeOutMidi(track.midiStreamPlayer, 2f));
            }
        }

        
    }

    private void ClearTrackForNewPhase(InstrumentTrack t)
{
    // 1) Soft audio fade/mute
    t.SetMuted(true);                        // your existing or stubbed mute

    // 2) Despawn/clear any lingering collectables/mined objects on this track
    //    (prevents "already perfect" and stale rot artifacts)
    if (t.spawnedCollectables != null)
    {
        for (int i = t.spawnedCollectables.Count - 1; i >= 0; i--)
        {
            var go = t.spawnedCollectables[i];
            if (go) Destroy(go);
            t.spawnedCollectables.RemoveAt(i);
        }
    }

    // 3) Reset per-track perfection bookkeeping (so the new star doesn't start perfect)
    t.ResetPerfectionFlagForPhase();         // add this tiny helper on InstrumentTrack (see below)

    // 4) Optional: visual nudge (e.g., dim ribbon color for this track)
    // NoteVisualizer can read t.IsMuted to dim rows, if you want.
}

    
public void RemixAllTracksForBridge(MusicalPhase phase, PhaseBridgeSignature sig)
{
    if (tracks == null) return;
    foreach (var t in tracks)
        RemixTrackForBridge(t, phase, sig);
}

private void RemixTrackForBridge(InstrumentTrack track, MusicalPhase phase, PhaseBridgeSignature sig)
{
    if (track == null) return;

    // 1) Clear the loop so the bridge is audibly different
    track.ClearLoopedNotes(TrackClearType.Remix);

    // 2) Get / ensure a NoteSet
    var ns = track.GetActiveNoteSet();
    if (ns == null) return; // nothing to remix

    // 3) Phase/role defaults, then bridge overrides
    var (defBehavior, defRhythm) = GetDefaultStyleForPhaseAndRole(phase, track.assignedRole);

    ns.ChangeNoteBehavior(track, defBehavior);     // you already use this elsewhere
    ns.rhythmStyle = defRhythm;                    // rhythm impacts durations downstream

    // 4) Inline seeding: pick a few allowed steps and add arpeggiated notes
    var steps = ns.GetStepList();               // <-- you use this pattern already
    if (steps == null || steps.Count == 0) return;

    int seeds = 6;                               // similar to prior suggestion; tweak to taste
    for (int i = 0; i < seeds; i++)
    {
        int step = steps[UnityEngine.Random.Range(0, steps.Count)];
        int note = ns.GetNextArpeggiatedNote(step);                    // same pattern as DrumTrack’s helper
        int dur  = track.CalculateNoteDuration(step, ns);              // see InstrumentTrack.CalculateNoteDuration
        float vel = Mathf.Lerp(60f, 100f, UnityEngine.Random.value);   // modest velocity spread
        track.AddNoteToLoop(step, note, dur, vel);                     // adds + visualizes
    }

    // 5) If extremely sparse, top up a bit
    int density = track.GetNoteDensity();                               // existing API
    if (density < 4)
    {
        int toAdd = 4 - density;
        for (int i = 0; i < toAdd; i++)
        {
            int step = steps[UnityEngine.Random.Range(0, steps.Count)];
            int note = ns.GetNextArpeggiatedNote(step);
            int dur  = track.CalculateNoteDuration(step, ns);
            float vel = Mathf.Lerp(70f, 100f, UnityEngine.Random.value);
            track.AddNoteToLoop(step, note, dur, vel);
        }
    }
}

    private void RemixSeedForPhase(InstrumentTrack t, MusicalPhase phase)
{
    // Keep it audible
    t.SetMuted(false);

    // Nudge its pattern/behavior so the new phase has a recognizable seed
    var ns = t.GetActiveNoteSet();
    if (ns != null)
    {
        var (behavior, rhythm) = GetDefaultStyleForPhaseAndRole(phase, t.assignedRole);
        ns.ChangeNoteBehavior(t, behavior);
        ns.rhythmStyle = rhythm;

        // Optional: quick spice if you’re re-integrating “remix boost”
        // ns.RandomizeArpOrder();
        // ns.ShiftOctave(phase == MusicalPhase.Intensify ? +1 : 0);
    }

    // Also clear old collectables to avoid stale rot on seeds
    if (t.spawnedCollectables != null)
    {
        for (int i = t.spawnedCollectables.Count - 1; i >= 0; i--)
        {
            var go = t.spawnedCollectables[i];
            if (go) Destroy(go);
            t.spawnedCollectables.RemoveAt(i);
        }
    }

    t.ResetPerfectionFlagForPhase();
}
    private (NoteBehavior behavior, RhythmStyle rhythm) GetDefaultStyleForPhaseAndRole(MusicalPhase phase, MusicalRole role)
{
    switch (phase)
    {
        case MusicalPhase.Intensify:
            return role == MusicalRole.Groove
                ? (NoteBehavior.Percussion, RhythmStyle.Dense)
                : (NoteBehavior.Lead, RhythmStyle.Syncopated);

        case MusicalPhase.Release:
            return (NoteBehavior.Drone, RhythmStyle.Sparse);

        case MusicalPhase.Evolve:
            return (NoteBehavior.Lead, RhythmStyle.Steady);

        case MusicalPhase.Wildcard:
            return (NoteBehavior.Glitch, RhythmStyle.Triplet);

        case MusicalPhase.Pop:
            return (NoteBehavior.Harmony, RhythmStyle.Steady);

        default: // Establish
            return (NoteBehavior.Lead, RhythmStyle.Steady);
    }
}

}
