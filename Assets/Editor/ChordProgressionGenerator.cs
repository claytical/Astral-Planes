using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

public static class ChordProgressionGenerator
{
    private const string DefaultFolder = "Assets/ChordProfiles";

    [MenuItem("Tools/Astral Planes/Generate Chord Progression Profiles (C Library)")]
    public static void GenerateChordProfiles_CLibrary()
    {
        EnsureFolder(DefaultFolder);

        // Library: C-based authoring (C4=60). All progressions are 4 chords.
        // Triads only, root position, intervals always start at 0.
        var defs = new List<ProgDef>
        {
            // ===== Major =====
            Prog("Happy · Lift · I–V–vi–IV",
                Maj("I",  60), Maj("V",  67), Min("vi", 69), Maj("IV", 65)),

            Prog("Happy · Open Road · I–IV–V–I",
                Maj("I",  60), Maj("IV", 65), Maj("V",  67), Maj("I",  60)),

            Prog("Resolution · Homecoming · I–IV–I–V",
                Maj("I",  60), Maj("IV", 65), Maj("I",  60), Maj("V",  67)),

            Prog("Resolution · Strong Cadence · I–V–I–I",
                Maj("I",  60), Maj("V",  67), Maj("I",  60), Maj("I",  60)),

            Prog("Bright · Rising · I–ii–V–I",
                Maj("I",  60), Min("ii", 62), Maj("V",  67), Maj("I",  60)),

            Prog("Pensive · Bittersweet · I–iii–IV–V",
                Maj("I",  60), Min("iii",64), Maj("IV", 65), Maj("V",  67)),

            Prog("Tension · Borrowed Color · I–bVI–IV–V",
                Maj("I",  60), Maj("bVI",68), Maj("IV", 65), Maj("V",  67)),

            Prog("Nostalgia · 50s Loop · I–vi–IV–V",
                Maj("I",  60), Min("vi", 69), Maj("IV", 65), Maj("V",  67)),

            // ===== Minor =====
            Prog("Sad · Falling · i–bVI–bIII–bVII",
                Min("i",  60), Maj("bVI",68), Maj("bIII",63), Maj("bVII",70)),

            Prog("Sad · Resolve · i–iv–V–i",
                Min("i",  60), Min("iv", 65), Maj("V",  67), Min("i",  60)),

            Prog("Pensive · Drift · i–bVII–IV–i",
                Min("i",  60), Maj("bVII",70), Maj("IV", 65), Min("i",  60)),

            Prog("Pensive · Dorian-ish · i–IV–i–bVII",
                Min("i",  60), Maj("IV", 65), Min("i",  60), Maj("bVII",70)),

            Prog("Uneasy · Half-step Door · i–bII–V–i",
                Min("i",  60), Maj("bII",61), Maj("V",  67), Min("i",  60)),

            Prog("Uneasy · Mirror · i–V–i–V",
                Min("i",  60), Maj("V",  67), Min("i",  60), Maj("V",  67)),

            Prog("Dark · Spiral · i–iv–bVI–V",
                Min("i",  60), Min("iv", 65), Maj("bVI",68), Maj("V",  67)),

            Prog("Sad · Heavy · i–bVI–iv–V",
                Min("i",  60), Maj("bVI",68), Min("iv", 65), Maj("V",  67)),
        };

        int created = 0;
        foreach (var def in defs)
        {
            if (def.chords == null || def.chords.Count != 4)
            {
                Debug.LogWarning($"[ChordProgressionGenerator] SKIP '{def.name}': chord count must be 4.");
                continue;
            }

            CreateOrReplaceProfile(def.name, def.chords, def.beatsPerChord, DefaultFolder);
            created++;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"✅ Generated {created} ChordProgressionProfile assets in '{DefaultFolder}'.");
    }

    // ------------------------------------------------------------
    // Data definition helpers
    // ------------------------------------------------------------

    private readonly struct ProgDef
    {
        public readonly string name;
        public readonly float beatsPerChord;
        public readonly List<Chord> chords;

        public ProgDef(string name, float beatsPerChord, List<Chord> chords)
        {
            this.name = name;
            this.beatsPerChord = beatsPerChord;
            this.chords = chords;
        }
    }

    private static ProgDef Prog(string name, params Chord[] chords)
        => new ProgDef(name, 4f, new List<Chord>(chords));

    private static Chord Maj(string label, int rootMidi)
        => MakeChord(label, rootMidi, new[] { 0, 4, 7 });

    private static Chord Min(string label, int rootMidi)
        => MakeChord(label, rootMidi, new[] { 0, 3, 7 });

    private static Chord Dim(string label, int rootMidi)
        => MakeChord(label, rootMidi, new[] { 0, 3, 6 });

    private static Chord MakeChord(string label, int root, int[] intervals)
    {
        // Intervals always start at 0 by design; we keep that invariant here.
        return new Chord
        {
            label = label,
            rootNote = root,
            intervals = new List<int>(intervals)
        };
    }

    // ------------------------------------------------------------
    // Asset creation helpers
    // ------------------------------------------------------------

    private static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder)) return;

        // Create nested folders if needed.
        string[] parts = folder.Split('/');
        string current = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }
    }

    private static void CreateOrReplaceProfile(string name, List<Chord> chords, float beatsPerChord, string folder)
    {
        var asset = ScriptableObject.CreateInstance<ChordProgressionProfile>();
        asset.progressionName = name;
        asset.chordSequence = chords;
        asset.beatsPerChord = beatsPerChord;

        string fileName = SanitizeFileName(name) + ".asset";
        string path = Path.Combine(folder, fileName).Replace("\\", "/");

        // Replace existing asset if present.
        var existing = AssetDatabase.LoadAssetAtPath<ChordProgressionProfile>(path);
        if (existing != null)
        {
            AssetDatabase.DeleteAsset(path);
        }

        AssetDatabase.CreateAsset(asset, path);
        EditorUtility.SetDirty(asset);
    }

    private static string SanitizeFileName(string s)
    {
        // stable, readable filenames
        s = s.Replace("–", "-");
        s = s.Replace("·", "-");
        s = s.Replace("—", "-");
        s = s.Trim();

        // collapse whitespace to underscores
        s = Regex.Replace(s, @"\s+", "_");

        // strip illegal characters
        foreach (char c in Path.GetInvalidFileNameChars())
            s = s.Replace(c.ToString(), "");

        // avoid super long filenames on some systems
        if (s.Length > 120) s = s.Substring(0, 120);

        return s;
    }
}
