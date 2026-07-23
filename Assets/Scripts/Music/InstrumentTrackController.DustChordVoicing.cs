using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Dust-collision-triggered chord pluck: builds a role-appropriate voicing for the current
// chord and plays one note from it. Triggered by CosmicDust.Collision, not by the gravity
// void's own growth routine — shares no state with GravityVoidController.
public partial class InstrumentTrackController
{
    private InstrumentTrack GetTrackForRole(MusicalRole role)
    {
        if (role == MusicalRole.None) return null;
        if (tracks == null || tracks.Length == 0) return null;

        for (int i = 0; i < tracks.Length; i++)
        {
            var t = tracks[i];
            if (t != null && t.assignedRole == role)
                return t;
        }

        return null;
    }

    public void PlayDustChordPluck(
        MusicalRole role,
        float phrase01 = 0f,
        int chordSize = 4,
        int durTicks = 180,
        float vel127 = 24f)
    {
        try
        {
            if (role == MusicalRole.None)
                return;

            var track = GetTrackForRole(role);
            if (track == null)
                return;

            if (_gfm == null) _gfm = GameFlowManager.Instance;
            var harmony = _gfm?.harmony;
            if (harmony == null)
                return;

            int playheadBin = GetTransportFrame().playheadBin;
            int chordIdx = track.Harmony_GetChordIndexForBin(playheadBin);
            if (chordIdx < 0)
                return;

            if (!harmony.TryGetChordAt(chordIdx, out var chord))
                return;

            if (chord.intervals == null || chord.intervals.Count == 0)
                return;

            var notes = BuildGravityVoidVoicing(
                track.assignedRole,
                chord,
                Mathf.Max(1, chordSize),
                track.lowestAllowedNote,
                track.highestAllowedNote
            );

            if (notes == null || notes.Count == 0)
                return;

            phrase01 = Mathf.Clamp01(phrase01);

            int noteIndex = Mathf.Clamp(
                Mathf.RoundToInt(phrase01 * (notes.Count - 1)),
                0,
                notes.Count - 1
            );

            int midi = notes[noteIndex];

            track.PlayNote127(midi, durTicks, vel127);
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[DUST] PlayDustChordPluck EXCEPTION: {ex}");
        }
    }

    private List<int> BuildGravityVoidVoicing(
        MusicalRole role,
        Chord chord,
        int targetCount,
        int low,
        int high)
    {
        // Build pitch classes from chord
        var pcs = chord.intervals
            .Select(i => (chord.rootNote + i) % 12)
            .Distinct()
            .ToList();

        int rootPC  = chord.rootNote % 12;
        int thirdPC = pcs.FirstOrDefault(pc => (pc - rootPC + 12) % 12 is 3 or 4);
        int fifthPC = pcs.FirstOrDefault(pc => (pc - rootPC + 12) % 12 == 7);
        int seventhPC = pcs.FirstOrDefault(pc => (pc - rootPC + 12) % 12 is 10 or 11);

        List<int> priorityPCs = role switch
        {
            // --------------------------------------------------
            // Bass: guide tones first, avoid root dominance
            // --------------------------------------------------
            MusicalRole.Bass => new()
            {
                thirdPC,
                seventhPC,
                fifthPC,
                rootPC,
                thirdPC
            },

            // --------------------------------------------------
            // Harmony: classic shell → full stack
            // --------------------------------------------------
            MusicalRole.Harmony => new()
            {
                thirdPC,
                seventhPC,
                rootPC,
                fifthPC,
                seventhPC
            },

            // --------------------------------------------------
            // Lead: color tones, higher tension
            // --------------------------------------------------
            MusicalRole.Lead => new()
            {
                seventhPC,
                thirdPC,
                fifthPC,
                rootPC,
                seventhPC
            },

            // --------------------------------------------------
            // Groove / mid-perc tonal
            // --------------------------------------------------
            MusicalRole.Groove => new()
            {
                thirdPC,
                fifthPC,
                seventhPC,
                rootPC,
                thirdPC
            },

            _ => pcs
        };

        var result = new List<int>();
        int octaveAnchor = (low + high) / 2;

        foreach (int pc in priorityPCs)
        {
            if (result.Count >= targetCount) break;

            int note = FitPitchClassToRange(pc, octaveAnchor, low, high);
            if (!result.Contains(note))
                result.Add(note);
        }

        return result;
    }

    private int FitPitchClassToRange(int pc, int anchor, int low, int high)
    {
        int note = anchor - ((anchor - pc + 120) % 12);
        while (note < low)  note += 12;
        while (note > high) note -= 12;
        return Mathf.Clamp(note, low, high);
    }
}
