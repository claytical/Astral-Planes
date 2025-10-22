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
    public static void AssignShipsToTracks(List<ShipMusicalProfile> selectedShips, List<InstrumentTrack> tracks)
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
                    var noteSet = new NoteSet { assignedInstrumentTrack = track, noteBehavior = roleProfile.defaultBehavior}; 
                    noteSet.Initialize(track, track.GetTotalSteps());
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

            var noteSet = new NoteSet { assignedInstrumentTrack = track, noteBehavior = fallbackProfile.defaultBehavior };
                        noteSet.Initialize(track, track.GetTotalSteps());
        }
    }
}

public class InstrumentTrackController : MonoBehaviour
{
    public InstrumentTrack[] tracks;
    public NoteVisualizer noteVisualizer;
    private readonly Dictionary<InstrumentTrack, int> _loopHash = new();

    void Start()
    {
        if (!GameFlowManager.Instance.ReadyToPlay()) return;
        noteVisualizer?.Initialize(); // ← ensures playhead + mapping are active
        UpdateVisualizer();
    }
    public void ConfigureTracksFromShips(List<ShipMusicalProfile> selectedShips)
    {
        ShipTrackAssigner.AssignShipsToTracks(selectedShips, tracks.ToList());
        UpdateVisualizer();
    } 
    public InstrumentTrack GetAmbientContextTrack() {
        if (tracks == null || tracks.Length == 0) return null; 
        // Prefer Harmony → Groove → Bass → Lead (falls back to first that has a NoteSet)
        MusicalRole[] pref = { MusicalRole.Harmony, MusicalRole.Groove, MusicalRole.Bass, MusicalRole.Lead }; 
        foreach (var role in pref) {
            var t = tracks.FirstOrDefault(x => x != null && x.assignedRole == role && x.HasNoteSet()); if (t != null) return t;
        } 
        return tracks.FirstOrDefault(x => x != null && x.HasNoteSet()) ?? tracks[0];
    }
    public NoteSet GetGlobalContextNoteSet(){ var t = GetAmbientContextTrack(); 
        return t != null ? t.GetActiveNoteSet() : null;
        }
    public InstrumentTrack FindTrackByRole(MusicalRole role)
    {
        Debug.Log("Configured roles: " + 
                  string.Join(", ", tracks.Select(t => t.assignedRole)));
        return tracks.FirstOrDefault(t => t.assignedRole == role);
    }
    public void ApplySeedVisibility(List<InstrumentTrack> seeds)
    {
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
    public void UpdateVisualizer()
    {
        if (noteVisualizer == null) return;

        foreach (var track in tracks)
        {
            int h = ComputeLoopHash(track);
            if (_loopHash.TryGetValue(track, out var prev) && prev == h)
                continue; // no loop change → no work this frame
            _loopHash[track] = h;

            foreach (var (step, _, _, _) in track.GetPersistentLoopNotes())
                noteVisualizer.PlacePersistentNoteMarker(track, step); // lit=true default
        }
    }
    private static int ComputeLoopHash(InstrumentTrack t)
    {
        // order-independent hash of loop steps (cheap + stable)
        unchecked
        {
            int h = 17;
            foreach (var (step, _, _, _) in t.GetPersistentLoopNotes().OrderBy(n => n.Item1))
                h = h * 31 + step;
            return h;
        }
    }
    public int GetMaxActiveLoopMultiplier()
    {
        if (tracks == null || tracks.Length == 0) return 1;

        int maxMul = 1;
        foreach (var t in tracks)
        {
            if (t == null) continue;
            var loopNotes = t.GetPersistentLoopNotes();
            if (loopNotes != null && loopNotes.Count > 0)
                maxMul = Mathf.Max(maxMul, Mathf.Max(1, t.loopMultiplier));
        }
        return maxMul;
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
//    track.ClearLoopedNotes(TrackClearType.Remix);

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

    var fresh = new List<(int step,int note,int duration,float velocity)>();
    int seeds = 8; // a touch denser for lift-off; tweak to taste
    for (int i = 0; i < seeds; i++) { 
        int step = steps[UnityEngine.Random.Range(0, steps.Count)]; 
        int note = ns.GetNextArpeggiatedNote(step); 
        int dur  = track.CalculateNoteDuration(step, ns); 
        float vel = Mathf.Lerp(70f, 110f, UnityEngine.Random.value); 
        fresh.Add((step, note, dur, vel));
    } 
    if (fresh.Count < 6) { 
        int topUp = 6 - fresh.Count; 
        for (int i = 0; i < topUp; i++) {
            int step = steps[UnityEngine.Random.Range(0, steps.Count)]; 
            int note = ns.GetNextArpeggiatedNote(step); 
            int dur  = track.CalculateNoteDuration(step, ns); 
            float vel = Mathf.Lerp(80f, 115f, UnityEngine.Random.value); 
            fresh.Add((step, note, dur, vel));
        }
    }
    // Atomic swap: no blast visuals, no collapse-to-x1
    track.SoftReplaceLoop(fresh);
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
