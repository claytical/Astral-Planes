using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public partial class AstralPlanesToolSuiteWindow : EditorWindow
{
    private const string MenuRootPrefix = "Astral Planes/";
    private const string FallbackFolder = "Assets/Resources/Config/Config";

    private static readonly string[] GroupOrder =
        { "Music", "Dust", "Gameplay", "Phase", "Visualizers", "Platform" };

    private static readonly Dictionary<string, string> DefaultFolderByTypeName = new()
    {
        { "MusicalRoleProfile", "Assets/Resources/Profiles/Musical Roles" },
        { "MusicalRoleMovementProfile", "Assets/Resources/Profiles/Movement Archetypes" },
        { "VehicleConfig", "Assets/Resources/Profiles/Vehicles" },
        { "ChordProgressionProfile", "Assets/Resources/Profiles/Chord Progressions" },
        { "MotifProfile", "Assets/Resources/Profiles/Motifs" },
        { "RiffAsset", "Assets/Resources/Config/Riffs" },
        { "MazePatternConfig", "Assets/Resources/Mazes" },
        { "PhaseStarBehaviorProfile", "Assets/Resources/Profiles/Star Behavior" },
        { "WispConfig", "Assets/Resources" },
        { "SteamStatsExclusionList", "Assets/Audio" },
    };

    private class TypeEntry
    {
        public Type Type;
        public string MenuName;
        public string FileName;
        public string Group;
        public bool Foldout;
    }

    private readonly Dictionary<string, bool> _groupFoldouts = new();
    private List<TypeEntry> _entries = new();
    private Vector2 _scroll;

    [MenuItem("Tools/Astral Planes/Tool Suite")]
    public static void Open()
    {
        var window = GetWindow<AstralPlanesToolSuiteWindow>("Astral Planes Tool Suite");
        window.minSize = new Vector2(420, 500);
        window.RefreshTypes();
    }

    private void OnEnable()
    {
        RefreshTypes();
        RefreshUnusedAssets();
    }

    private void OnFocus()
    {
        RefreshTypes();
        RefreshUnusedAssets();
    }

    private void RefreshTypes()
    {
        _entries = TypeCache.GetTypesWithAttribute<CreateAssetMenuAttribute>()
            .Select(t => (Type: t, Attr: t.GetCustomAttribute<CreateAssetMenuAttribute>()))
            .Where(x => x.Attr != null
                && !string.IsNullOrEmpty(x.Attr.menuName)
                && x.Attr.menuName.StartsWith(MenuRootPrefix, StringComparison.Ordinal))
            .Select(x =>
            {
                var rest = x.Attr.menuName.Substring(MenuRootPrefix.Length);
                var slash = rest.IndexOf('/');
                var group = slash >= 0 ? rest.Substring(0, slash) : "Other";
                return new TypeEntry
                {
                    Type = x.Type,
                    MenuName = x.Attr.menuName,
                    FileName = string.IsNullOrEmpty(x.Attr.fileName) ? x.Type.Name : x.Attr.fileName,
                    Group = group,
                };
            })
            .OrderBy(e => GroupSortIndex(e.Group))
            .ThenBy(e => e.MenuName, StringComparer.Ordinal)
            .ToList();
    }

    private static int GroupSortIndex(string group)
    {
        var index = Array.IndexOf(GroupOrder, group);
        return index >= 0 ? index : GroupOrder.Length;
    }

    private void OnGUI()
    {
        EditorGUILayout.Space();
        DrawExistingTools();
        EditorGUILayout.Space();

        DrawUnusedAssetsSection();
        EditorGUILayout.Space();

        if (GUILayout.Button("Refresh", GUILayout.Width(80)))
            RefreshTypes();

        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        foreach (var group in _entries.GroupBy(e => e.Group))
        {
            _groupFoldouts.TryGetValue(group.Key, out var expanded);
            expanded = EditorGUILayout.Foldout(expanded, $"{group.Key} ({group.Count()})", true);
            _groupFoldouts[group.Key] = expanded;
            if (!expanded) continue;

            EditorGUI.indentLevel++;
            foreach (var entry in group)
                DrawTypeEntry(entry);
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.EndScrollView();
    }

    private void DrawExistingTools()
    {
        EditorGUILayout.LabelField("Existing Tools", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Generate Chord Progression Profiles"))
                EditorApplication.ExecuteMenuItem("Tools/Astral Planes/Generate Chord Progression Profiles (C Library)");

            if (GUILayout.Button("MIDI → Riff Importer"))
                GetWindow<MidiToRiffImporterWindow>("MIDI → Riff Importer");
        }
    }

    private void DrawTypeEntry(TypeEntry entry)
    {
        var guids = AssetDatabase.FindAssets($"t:{entry.Type.Name}");
        entry.Foldout = EditorGUILayout.Foldout(
            entry.Foldout,
            $"{ObjectNames.NicifyVariableName(entry.Type.Name)} ({guids.Length})",
            true);
        if (!entry.Foldout) return;

        EditorGUI.indentLevel++;

        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (GUILayout.Button(Path.GetFileNameWithoutExtension(path), EditorStyles.linkLabel))
            {
                var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                Selection.activeObject = obj;
                EditorGUIUtility.PingObject(obj);
            }
        }

        if (GUILayout.Button("Create New…", GUILayout.Width(120)))
            CreateNewAsset(entry);

        EditorGUI.indentLevel--;
    }

    private void CreateNewAsset(TypeEntry entry)
    {
        var defaultFolder = DefaultFolderByTypeName.TryGetValue(entry.Type.Name, out var folder)
            ? folder
            : FallbackFolder;

        EnsureFolder(defaultFolder);

        var path = EditorUtility.SaveFilePanelInProject(
            $"Create {entry.Type.Name}",
            entry.FileName,
            "asset",
            $"Choose a location for the new {entry.Type.Name}",
            defaultFolder);

        if (string.IsNullOrEmpty(path))
            return;

        var instance = ScriptableObject.CreateInstance(entry.Type);
        AssetDatabase.CreateAsset(instance, path);
        AssetDatabase.SaveAssets();
        Selection.activeObject = instance;
        EditorGUIUtility.PingObject(instance);
        RefreshTypes();
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path))
            return;

        var parts = path.Split('/');
        var current = parts[0];
        for (var i = 1; i < parts.Length; i++)
        {
            var next = $"{current}/{parts[i]}";
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }
    }
}
