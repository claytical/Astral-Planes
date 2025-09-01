#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;

public static class NoteSetConfigGenerator
{
    private const string ConfigRootPath = "Assets/NoteSetConfigs/";

    [MenuItem("Astral Planes/Generate All Note Set Configs")]
    public static void GenerateConfigs()
    {
        // Create folders
        if (!AssetDatabase.IsValidFolder(ConfigRootPath))
            AssetDatabase.CreateFolder("Assets", "NoteSetConfigs");

        var allConfigs = new List<RolePhaseNoteSetConfig>();

        foreach (MusicalPhase phase in System.Enum.GetValues(typeof(MusicalPhase)))
        {
            string phaseFolder = Path.Combine(ConfigRootPath, phase.ToString());
            if (!AssetDatabase.IsValidFolder(phaseFolder))
                AssetDatabase.CreateFolder(ConfigRootPath.TrimEnd('/'), phase.ToString());

            foreach (MusicalRole role in System.Enum.GetValues(typeof(MusicalRole)))
            {
                var config = ScriptableObject.CreateInstance<RolePhaseNoteSetConfig>();
                config.phase = phase;
                config.role = role;

                // Default behavior guess
                config.noteBehavior = GuessBehavior(role);

                // Fill in all values with full enum lists
                config.possibleScales = new List<ScaleType>((ScaleType[])System.Enum.GetValues(typeof(ScaleType)));
                config.possibleChordPatterns = new List<ChordPattern>((ChordPattern[])System.Enum.GetValues(typeof(ChordPattern)));
                config.possibleRhythmStyles = new List<RhythmStyle>((RhythmStyle[])System.Enum.GetValues(typeof(RhythmStyle)));

                string assetPath = Path.Combine(phaseFolder, $"{phase}_{role}.asset");
                AssetDatabase.CreateAsset(config, assetPath);
                allConfigs.Add(config);
            }
        }

        // Create or overwrite the central library
        var library = ScriptableObject.CreateInstance<NoteSetConfigLibrary>();
        library.configs = allConfigs;

        string libraryPath = Path.Combine(ConfigRootPath, "NoteSetConfigLibrary.asset");
        AssetDatabase.CreateAsset(library, libraryPath);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"✅ Created {allConfigs.Count} configs and NoteSetConfigLibrary.");
    }

    private static NoteBehavior GuessBehavior(MusicalRole role)
    {
        return role switch
        {
            MusicalRole.Bass => NoteBehavior.Bass,
            MusicalRole.Lead => NoteBehavior.Lead,
            MusicalRole.Harmony => NoteBehavior.Harmony,
            MusicalRole.Groove => NoteBehavior.Percussion,
            _ => NoteBehavior.Harmony
        };
    }
}
#endif
