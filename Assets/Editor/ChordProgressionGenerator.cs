using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;

public static class ChordProgressionGenerator
{
    [MenuItem("Astral Planes/Generate Chord Progression Profiles")]
    public static void GenerateChordProfiles()
    {
        string folder = "Assets/ChordProfiles";
        if (!AssetDatabase.IsValidFolder(folder))
        {
            AssetDatabase.CreateFolder("Assets", "ChordProfiles");
        }

        CreateProfile("I–IV–V", new List<Chord> {
            MakeChord("I", 60, new[] {0, 4, 7}),
            MakeChord("IV", 65, new[] {0, 4, 7}),
            MakeChord("V", 67, new[] {0, 4, 7}),
        }, 4f, folder);

        CreateProfile("i–bVI–bVII", new List<Chord> {
            MakeChord("i", 57, new[] {0, 3, 7}),
            MakeChord("bVI", 65, new[] {0, 3, 7}),
            MakeChord("bVII", 67, new[] {0, 3, 7}),
        }, 4f, folder);

        CreateProfile("ii7–V7–Imaj7", new List<Chord> {
            MakeChord("ii7", 62, new[] {0, 3, 5, 10}),
            MakeChord("V7", 67, new[] {0, 4, 7, 10}),
            MakeChord("Imaj7", 60, new[] {0, 4, 7, 11}),
        }, 4f, folder);

        CreateProfile("Suspended", new List<Chord> {
            MakeChord("Csus2", 60, new[] {0, 2, 7}),
            MakeChord("Fsus4", 65, new[] {0, 5, 7}),
            MakeChord("Gsus2", 67, new[] {0, 2, 7}),
        }, 4f, folder);

        CreateProfile("Dorian i–ii–IV–v", new List<Chord> {
            MakeChord("i", 62, new[] {0, 3, 7}),
            MakeChord("ii", 64, new[] {0, 3, 7}),
            MakeChord("IV", 67, new[] {0, 4, 7}),
            MakeChord("v", 69, new[] {0, 3, 7}),
        }, 4f, folder);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("✅ Chord Progression Profiles generated.");
    }

    private static void CreateProfile(string name, List<Chord> chords, float beatsPerChord, string folder)
    {
        var asset = ScriptableObject.CreateInstance<ChordProgressionProfile>();
        asset.progressionName = name;
        asset.chordSequence = chords;
        asset.beatsPerChord = beatsPerChord;

        string path = Path.Combine(folder, name.Replace("–", "-").Replace(" ", "_") + ".asset");
        AssetDatabase.CreateAsset(asset, path);
    }

    private static Chord MakeChord(string label, int root, int[] intervals)
    {
        return new Chord {
            label = label,
            rootNote = root,
            intervals = new List<int>(intervals)
        };
    }
}
