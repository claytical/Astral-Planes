using System;
using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
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
                    noteSet.Initialize(track.GetTotalSteps());
//                    track.SpawnCollectables(noteSet);
                    
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
            noteSet.Initialize(track.GetTotalSteps());
                //track.SpawnCollectables(noteSet);

        }
    }
}

public class InstrumentTrackController : MonoBehaviour
{
    public InstrumentTrack[] tracks;
    public InstrumentTrack activeTrack;
    public NoteVisualizer noteVisualizer;
    public GameObject noteSetPrefab;

    void Start()
    {
            if (!GameFlowManager.Instance.ReadyToPlay())
            {
                return;
            }
    }
    public int GetMaxLoopMultiplier()
    {
        return tracks.Max(track => track.loopMultiplier);
    }
    public void ConfigureTracksFromShips(List<ShipMusicalProfile> selectedShips, GameObject notesetPrefab)
    {
        
        ShipTrackAssigner.AssignShipsToTracks(selectedShips, tracks.ToList(), notesetPrefab);
        UpdateVisualizer();
    }

    public void UpdateVisualizer()
    {
       // noteVisualizer.DisplayNotes(tracks.ToList());
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

    public InstrumentTrack FindTrackByRole(MusicalRole role)
    {
        return tracks.FirstOrDefault(t => t.assignedRole == role);
    }
    public void PruneSparseTracks(int threshold = 1)
    {
        foreach (var track in tracks.Where(t => t.CollectedNotesCount <= threshold))
        {
            track.ClearLoopedNotes();
        }
    }
    
    public InstrumentTrack FindRandomTrackByRole(MusicalRole role)
    {
        var matching = tracks.Where(t => t.assignedRole == role).ToList();
        
        if (matching.Count == 0) return null;
        return matching[Random.Range(0, matching.Count)];
    }

    public string GenerateCrypticIntroPhrase()
    {
        Dictionary<int, string> presetDescriptors = new()
        {
            // Bass
            { 32, "subterranean warmth" }, { 33, "low-frequency prayer" }, { 34, "slow thunder" }, { 35, "molten anchors" },
            { 36, "pulsating love" }, { 37, "heartquake" }, { 38, "magnetic tide" }, { 39, "deep machine breath" },

            // Lead
            { 24, "glittering teeth" }, { 25, "slicing wind" }, { 26, "blazing fingers" },
            { 80, "synthetic longing" }, { 81, "electric fire" }, { 82, "sapphire spirals" }, { 83, "neon needles" },
            { 84, "sky-cutting melody" }, { 85, "liquid blaze" }, { 86, "signal pulse" }, { 87, "hyperlight beams" },
            { 73, "hollow flute" }, { 74, "ghost pipe" }, { 75, "bamboo wind" },

            // Harmony
            { 0, "gentle collisions" }, { 1, "stained glass echoes" }, { 2, "room-sized memory" }, { 3, "twilight bloom" },
            { 48, "wooden bloom" }, { 49, "threaded harmonies" }, { 50, "woven dusk" }, { 51, "velvet architecture" },
            { 52, "drifting arcs" }, { 53, "bowed ether" },
            { 88, "cloud breath" }, { 89, "fog lace" }, { 90, "woolen shimmer" }, { 91, "electric wool" },
            { 92, "celestial fog" }, { 93, "horizon wash" },
            { 16, "pipe pulse" }, { 17, "chord cathedral" }, { 18, "light organ" },

            // Groove
            { 115, "mechanical twitch" }, { 116, "bone clack" }, { 117, "granular clockwork" }, { 118, "ritual static" }, { 119, "rattling chrome" },
            { 12, "hammered tone" }, { 13, "sparked crystal" }, { 14, "glass pulse" },
            { 27, "twang shimmer" }, { 28, "metallic jangle" },
            { 56, "spark-driven limbs" }, { 57, "piston swing" }, { 58, "sync in steel" }
        };

        List<string> fragments = new();
        foreach (var track in tracks)
        {
            if (presetDescriptors.TryGetValue(track.preset, out var phrase))
            {
                fragments.Add(phrase);
            }
            else
            {
                fragments.Add("an unnamed current");
            }
        }

        // Shuffle for variation
        fragments = fragments.OrderBy(_ => UnityEngine.Random.value).ToList();

        // Template library
        string[] templates = new[]
        {
            $"Where {fragments[0]} meets {fragments[1]}, and {fragments[2]} drifts through {fragments[3]}, a signal awakens.",
            $"In the space between {fragments[0]}, {fragments[1]}, {fragments[2]}, and {fragments[3]}, the groove begins.",
            $"You’ll hear {fragments[0]}. Then {fragments[1]}. Then {fragments[2]} and {fragments[3]}. And still not understand what’s coming.",
            $"{fragments[0]}, {fragments[1]}, {fragments[2]}, and {fragments[3]}—their harmony foretells the unknown.",
            $"Threads of {fragments[0]} wrap around {fragments[1]}, while {fragments[2]} and {fragments[3]} echo through time.",
            $"All four converge: {fragments[0]}, {fragments[1]}, {fragments[2]}, and {fragments[3]}. Hold on."
        };

        int index = UnityEngine.Random.Range(0, templates.Length);
        return templates[index];
    }


}
