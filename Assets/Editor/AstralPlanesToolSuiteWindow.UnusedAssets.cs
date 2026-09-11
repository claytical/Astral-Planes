using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public partial class AstralPlanesToolSuiteWindow
{
    private class UnusedAssetEntry
    {
        public UnityEngine.Object Asset;
        public string DisplayName;
        public string Path;
        public bool Selected;
    }

    private readonly List<UnusedAssetEntry> _orphanedMotifs = new();
    private readonly List<UnusedAssetEntry> _unusedNoteConfigs = new();
    private readonly List<UnusedAssetEntry> _unusedRiffs = new();

    private bool _unusedSectionFoldout;
    private bool _orphanedMotifsFoldout = true;
    private bool _unusedNoteConfigsFoldout = true;
    private bool _unusedRiffsFoldout = true;

    private Vector2 _motifsScroll, _noteConfigsScroll, _riffsScroll;
    private string _motifsFilter = "", _noteConfigsFilter = "", _riffsFilter = "";

    private static List<T> LoadAllOfType<T>() where T : UnityEngine.Object
    {
        var result = new List<T>();
        var guids = AssetDatabase.FindAssets($"t:{typeof(T).Name}");
        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset != null)
                result.Add(asset);
        }
        return result;
    }

    private void RefreshUnusedAssets()
    {
        var allLibraries = LoadAllOfType<PhaseLibrary>();
        var allMotifs = LoadAllOfType<MotifProfile>();
        var allNoteConfigs = LoadAllOfType<RoleMotifNoteSetConfig>();
        var allRiffs = LoadAllOfType<RiffAsset>();

        // Each category checks direct references against the FULL parent pool (not just
        // "live" parents), so the three scans are independent, not cascading. Deleting an
        // orphaned Motif can surface newly-unused NoteConfigs/Riffs on the next rescan —
        // that's the intended top-to-bottom workflow, not automated here.

        var referencedMotifs = new HashSet<MotifProfile>(
            allLibraries
                .SelectMany(lib => lib.phases ?? new List<PhaseLibrary.Phase>())
                .Where(p => p != null)
                .SelectMany(p => p.motifs ?? new List<MotifProfile>())
                .Where(m => m != null));

        BuildEntries(_orphanedMotifs, allMotifs.Where(m => !referencedMotifs.Contains(m)));

        var referencedNoteConfigs = new HashSet<RoleMotifNoteSetConfig>(
            allMotifs
                .Where(m => m != null)
                .SelectMany(m => m.roleNoteConfigs ?? new List<RoleMotifNoteSetConfig>())
                .Where(c => c != null));

        BuildEntries(_unusedNoteConfigs, allNoteConfigs.Where(c => !referencedNoteConfigs.Contains(c)));

        var referencedRiffs = new HashSet<RiffAsset>(
            allNoteConfigs
                .Where(c => c != null)
                .Select(c => c.riff)
                .Where(r => r != null));

        BuildEntries(_unusedRiffs, allRiffs.Where(r => !referencedRiffs.Contains(r)));
    }

    private static void BuildEntries(List<UnusedAssetEntry> target, IEnumerable<UnityEngine.Object> assets)
    {
        target.Clear();
        foreach (var asset in assets.OrderBy(a => a.name, StringComparer.OrdinalIgnoreCase))
        {
            var path = AssetDatabase.GetAssetPath(asset);
            target.Add(new UnusedAssetEntry
            {
                Asset = asset,
                DisplayName = System.IO.Path.GetFileNameWithoutExtension(path),
                Path = path,
                Selected = false,
            });
        }
    }

    private void DrawUnusedAssetsSection()
    {
        _unusedSectionFoldout = EditorGUILayout.Foldout(_unusedSectionFoldout, "Unused Asset Cleanup", true, EditorStyles.foldoutHeader);
        if (!_unusedSectionFoldout) return;

        EditorGUI.indentLevel++;
        DrawUnusedCategory("Orphaned Motifs (not in any PhaseLibrary)", _orphanedMotifs,
            ref _orphanedMotifsFoldout, ref _motifsFilter, ref _motifsScroll);
        DrawUnusedCategory("Unused Role NoteSet Configs (not referenced by any Motif)", _unusedNoteConfigs,
            ref _unusedNoteConfigsFoldout, ref _noteConfigsFilter, ref _noteConfigsScroll);
        DrawUnusedCategory("Unused Riffs (not referenced by any NoteSet Config)", _unusedRiffs,
            ref _unusedRiffsFoldout, ref _riffsFilter, ref _riffsScroll);
        EditorGUI.indentLevel--;
    }

    private void DrawUnusedCategory(string title, List<UnusedAssetEntry> entries,
        ref bool foldout, ref string filter, ref Vector2 scroll)
    {
        foldout = EditorGUILayout.Foldout(foldout, $"{title} ({entries.Count})", true);
        if (!foldout) return;

        EditorGUI.indentLevel++;
        filter = EditorGUILayout.TextField("Filter", filter);
        var filterValue = filter;

        var visible = string.IsNullOrEmpty(filterValue)
            ? entries
            : entries.Where(e => e.DisplayName.IndexOf(filterValue, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Select All", GUILayout.Width(90)))
                foreach (var e in visible) e.Selected = true;
            if (GUILayout.Button("Select None", GUILayout.Width(90)))
                foreach (var e in visible) e.Selected = false;
        }

        scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MaxHeight(180));
        foreach (var e in visible)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                e.Selected = EditorGUILayout.Toggle(e.Selected, GUILayout.Width(20));
                if (GUILayout.Button(e.DisplayName, EditorStyles.linkLabel))
                {
                    Selection.activeObject = e.Asset;
                    EditorGUIUtility.PingObject(e.Asset);
                }
            }
            EditorGUILayout.LabelField(e.Path, EditorStyles.miniLabel);
        }
        EditorGUILayout.EndScrollView();

        var selectedCount = entries.Count(e => e.Selected);
        using (new EditorGUI.DisabledScope(selectedCount == 0))
        {
            if (GUILayout.Button($"Delete Selected ({selectedCount})"))
                DeleteSelected(entries);
        }
        EditorGUI.indentLevel--;
    }

    private void DeleteSelected(List<UnusedAssetEntry> entries)
    {
        var toDelete = entries.Where(e => e.Selected).ToList();
        if (toDelete.Count == 0) return;

        var preview = string.Join("\n", toDelete.Take(15).Select(e => e.DisplayName));
        if (toDelete.Count > 15)
            preview += $"\n… and {toDelete.Count - 15} more";

        var confirmed = EditorUtility.DisplayDialog(
            "Delete Unused Assets",
            $"Move {toDelete.Count} asset(s) to the OS trash? This can be undone via trash recovery, " +
            $"but not via Unity Undo.\n\n{preview}",
            "Move to Trash", "Cancel");

        if (!confirmed) return;

        foreach (var e in toDelete)
        {
            var path = AssetDatabase.GetAssetPath(e.Asset);
            AssetDatabase.MoveAssetToTrash(path);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        RefreshUnusedAssets();
        RefreshTypes();
    }
}
