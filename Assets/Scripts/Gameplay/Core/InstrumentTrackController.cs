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
        //MidiPlayerGlobal.OnEventPresetLoaded.AddListener(EndLoadingSF);
        //MidiPlayerGlobal.MPTK_LoadLiveSF("file:///Users/clayewing/Documents/Moonlight.sf2");
            if (!GameFlowManager.Instance.ReadyToPlay())
            {
                return;
            }
    }
    public void EndLoadingSF()
    {
        // when SoundFont is dynamically loaded, MidiPlayerGlobal.ImSFCurrent.SoundFontName not contains name - TBD
     
        Debug.LogFormat($"End loading: '{MidiPlayerGlobal.ImSFCurrent?.SoundFontName}' Status: {MidiPlayerGlobal.MPTK_StatusLastSoundFontLoaded}");
        Debug.Log("Load statistique");
        Debug.Log($"   Time To Download SF:     {Math.Round(MidiPlayerGlobal.MPTK_TimeToDownloadSoundFont.TotalSeconds, 3)} second");
        Debug.Log($"   Time To Load SoundFont:  {Math.Round(MidiPlayerGlobal.MPTK_TimeToLoadSoundFont.TotalSeconds, 3)} second");
        Debug.Log($"   Time To Load Samples:    {Math.Round(MidiPlayerGlobal.MPTK_TimeToLoadWave.TotalSeconds, 3).ToString()} second");
        Debug.Log($"   Presets Loaded: {MidiPlayerGlobal.MPTK_CountPresetLoaded}");
        Debug.Log($"   Samples Loaded: {MidiPlayerGlobal.MPTK_CountWaveLoaded}");
    }

    public int GetMaxLoopMultiplier()
    {
        return tracks.Max(track => track.loopMultiplier);
    }
    public InstrumentTrack ClearAndReturnTrack(Vehicle vehicle)
    {
        var nonEmptyTracks = tracks.Where(t => t.GetNoteDensity() > 0).ToList();
        if (nonEmptyTracks.Count == 0) return null;

        var track = nonEmptyTracks[Random.Range(0, nonEmptyTracks.Count)];
        track.ClearLoopedNotes(TrackClearType.EnergyRestore, vehicle);
        return track;
    }

    
    
    public void ConfigureTracksFromShips(List<ShipMusicalProfile> selectedShips, GameObject notesetPrefab)
    {
        
        ShipTrackAssigner.AssignShipsToTracks(selectedShips, tracks.ToList(), notesetPrefab);
        UpdateVisualizer();
    }
//TODO: NEEDED?
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
    public InstrumentTrack FindRandomTrackByRole(MusicalRole role)
    {
        var matching = tracks.Where(t => t.assignedRole == role).ToList();
        
        if (matching.Count == 0) return null;
        return matching[Random.Range(0, matching.Count)];
    }

}
