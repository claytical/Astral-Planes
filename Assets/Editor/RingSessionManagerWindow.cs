using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Browse, inspect, delete and repair recorded ring sessions (ring_{phase}_{motif}.json in
/// persistentDataPath/RingSessions). Mirrors RingSessionStore's file layout; unlike the store,
/// "Fix" writes re-homed rings back to disk.
/// </summary>
public class RingSessionManagerWindow : EditorWindow
{
    private enum RowStatus { Ok, Legacy, Orphan, NameMismatch, Corrupt }
    private enum SortMode { Slot, Date }

    private class Row
    {
        public string path;
        public MotifSnapshot snap;
        public string error;
        public RowStatus status;
        public bool checkedForAction;
        public DateTime SavedLocal => new DateTime(snap != null && snap.SavedAtUtcTicks > 0
            ? snap.SavedAtUtcTicks : File.GetLastWriteTimeUtc(path).Ticks, DateTimeKind.Utc).ToLocalTime();
    }

    private static string SaveDirectory => Application.persistentDataPath + "/RingSessions/";
    private static string MockDirectory => Path.Combine(Application.streamingAssetsPath, "RingSessions");

    private readonly List<Row> _rows = new();
    private PhaseLibrary _library;
    private Row _selected;
    private Vector2 _listScroll, _detailScroll;
    private SortMode _sort = SortMode.Slot;
    private int _filter; // 0 = all, else (RowStatus)(_filter - 1)
    private bool _showJson;
    private string _rawJson;
    private int _assignPhase, _assignMotif;

    [MenuItem("Tools/Astral Planes/Ring Session Manager")]
    public static void Open()
    {
        var w = GetWindow<RingSessionManagerWindow>("Ring Sessions");
        w.minSize = new Vector2(760, 380);
    }

    private void OnEnable() => Refresh();
    private void OnFocus() => Refresh();

    // ---------------------------------------------------------------------------------------------
    // Data
    // ---------------------------------------------------------------------------------------------

    private void Refresh()
    {
        string selectedPath = _selected?.path;
        _rows.Clear();
        _selected = null;
        _library = Resources.Load<PhaseLibrary>("Phase Library");

        if (Directory.Exists(SaveDirectory))
        {
            foreach (var file in Directory.GetFiles(SaveDirectory, "ring_*.json"))
            {
                var row = new Row { path = file };
                try { row.snap = JsonUtility.FromJson<MotifSnapshot>(File.ReadAllText(file)); }
                catch (Exception e) { row.error = e.Message; }
                if (row.snap == null && row.error == null) row.error = "Empty or invalid JSON";
                row.status = Classify(row);
                _rows.Add(row);
                if (file == selectedPath) _selected = row;
            }
        }
        _rawJson = null;
        Repaint();
    }

    private bool SlotResolves(MotifSnapshot s)
    {
        var phases = _library?.phases;
        if (phases == null) return true; // can't judge without a library
        return phases.Any(p => p != null && p.phaseId == s.PhaseId && p.motifs != null
                               && p.motifs.Any(m => m != null && m.motifId == s.MotifId));
    }

    private RowStatus Classify(Row r)
    {
        if (r.snap == null) return RowStatus.Corrupt;
        if (string.IsNullOrEmpty(r.snap.PhaseId)) return RowStatus.Legacy;
        if (!SlotResolves(r.snap)) return RowStatus.Orphan;
        string expected = $"ring_{r.snap.PhaseIndex}_{r.snap.MotifIndex}.json";
        return Path.GetFileName(r.path) == expected ? RowStatus.Ok : RowStatus.NameMismatch;
    }

    private IEnumerable<Row> VisibleRows()
    {
        var q = _rows.Where(r => _filter == 0 || r.status == (RowStatus)(_filter - 1));
        return _sort == SortMode.Slot
            ? q.OrderBy(r => r.snap?.PhaseIndex ?? int.MaxValue).ThenBy(r => r.snap?.MotifIndex ?? 0)
            : q.OrderByDescending(r => r.snap != null ? r.SavedLocal : DateTime.MinValue);
    }

    // ---------------------------------------------------------------------------------------------
    // GUI
    // ---------------------------------------------------------------------------------------------

    private void OnGUI()
    {
        DrawToolbar();

        if (Directory.Exists(MockDirectory) && Directory.GetFiles(MockDirectory, "ring_*.json").Length > 0)
            EditorGUILayout.HelpBox("StreamingAssets/RingSessions has rings, so the Editor game reads those instead of the folder shown here.", MessageType.Info);
        if (_library == null)
            EditorGUILayout.HelpBox("Resources/Phase Library not found; orphan detection and fixes are unavailable.", MessageType.Warning);

        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(position.width * 0.55f)))
                DrawList();
            using (new EditorGUILayout.VerticalScope())
                DrawDetail();
        }
    }

    private void DrawToolbar()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(60))) Refresh();
            if (GUILayout.Button("Reveal Folder", EditorStyles.toolbarButton, GUILayout.Width(90)))
            {
                Directory.CreateDirectory(SaveDirectory);
                EditorUtility.RevealInFinder(SaveDirectory);
            }

            GUILayout.Space(8);
            _filter = EditorGUILayout.Popup(_filter,
                new[] { "All", "Ok", "Legacy", "Orphan", "Name mismatch", "Corrupt" },
                EditorStyles.toolbarPopup, GUILayout.Width(110));
            _sort = (SortMode)EditorGUILayout.EnumPopup(_sort, EditorStyles.toolbarPopup, GUILayout.Width(70));

            GUILayout.FlexibleSpace();

            int checkedCount = _rows.Count(r => r.checkedForAction);
            using (new EditorGUI.DisabledScope(checkedCount == 0))
            {
                if (GUILayout.Button($"Fix Checked ({checkedCount})", EditorStyles.toolbarButton, GUILayout.Width(110)))
                    FixRows(_rows.Where(r => r.checkedForAction).ToList());
                if (GUILayout.Button($"Delete Checked ({checkedCount})", EditorStyles.toolbarButton, GUILayout.Width(120)))
                    DeleteRows(_rows.Where(r => r.checkedForAction).ToList());
            }
        }
    }

    private void DrawList()
    {
        var visible = VisibleRows().ToList();
        EditorGUILayout.LabelField($"{visible.Count} of {_rows.Count} rings  —  {SaveDirectory}", EditorStyles.miniLabel);

        _listScroll = EditorGUILayout.BeginScrollView(_listScroll);
        foreach (var r in visible)
        {
            var rect = EditorGUILayout.BeginHorizontal(GUILayout.Height(20));
            if (r == _selected) EditorGUI.DrawRect(rect, new Color(0.24f, 0.48f, 0.9f, 0.35f));

            r.checkedForAction = EditorGUILayout.Toggle(r.checkedForAction, GUILayout.Width(16));
            string slot = r.snap != null ? $"{r.snap.PhaseIndex}_{r.snap.MotifIndex}" : "?";
            GUILayout.Label(slot, GUILayout.Width(36));
            GUILayout.Label(r.snap?.MotifId ?? Path.GetFileName(r.path), GUILayout.Width(110));
            GUILayout.Label(r.snap != null ? $"{r.snap.Bpm:0} bpm" : "", GUILayout.Width(60));
            GUILayout.Label(r.snap != null ? $"{r.snap.CollectedNotes.Count} notes" : "", GUILayout.Width(60));
            GUILayout.Label(r.snap != null ? r.SavedLocal.ToString("MM-dd HH:mm") : "", GUILayout.Width(80));

            var prev = GUI.contentColor;
            GUI.contentColor = StatusColor(r.status);
            GUILayout.Label(r.status.ToString());
            GUI.contentColor = prev;
            EditorGUILayout.EndHorizontal();

            if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition)
                && Event.current.mousePosition.x > rect.x + 20)
            {
                _selected = r;
                _rawJson = null;
                GUI.FocusControl(null);
                Event.current.Use();
                Repaint();
            }
        }
        EditorGUILayout.EndScrollView();
    }

    private static Color StatusColor(RowStatus s) => s switch
    {
        RowStatus.Ok => new Color(0.5f, 0.85f, 0.5f),
        RowStatus.Corrupt => new Color(1f, 0.4f, 0.4f),
        RowStatus.Orphan => new Color(1f, 0.65f, 0.3f),
        _ => new Color(0.95f, 0.85f, 0.4f),
    };

    private void DrawDetail()
    {
        if (_selected == null)
        {
            EditorGUILayout.HelpBox("Select a ring to inspect it.", MessageType.None);
            return;
        }

        var r = _selected;
        _detailScroll = EditorGUILayout.BeginScrollView(_detailScroll);

        EditorGUILayout.LabelField(Path.GetFileName(r.path), EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Reveal File")) EditorUtility.RevealInFinder(r.path);
            if (GUILayout.Button("Open File")) EditorUtility.OpenWithDefaultApp(r.path);
            if (GUILayout.Button("Delete")) DeleteRows(new List<Row> { r });
        }

        if (r.snap == null)
        {
            EditorGUILayout.HelpBox("Could not parse: " + r.error, MessageType.Error);
            EditorGUILayout.EndScrollView();
            return;
        }

        var s = r.snap;
        Field("Status", r.status.ToString());
        Field("Phase", $"{s.PhaseIndex}  ({(string.IsNullOrEmpty(s.PhaseId) ? "no id" : s.PhaseId)}){PhaseNameSuffix(s)}");
        Field("Motif", $"{s.MotifIndex}  ({s.MotifId})");
        Field("Saved", r.SavedLocal.ToString("yyyy-MM-dd HH:mm:ss"));
        Field("Pattern", s.Pattern.ToString());
        Field("BPM / step sec", $"{s.Bpm:0.##} / {s.StepDurationSec:0.####}");
        Field("Total steps / bins", $"{s.TotalSteps} / {s.LeaderBins}");
        Field("Key root MIDI", s.MotifKeyRootMidi.ToString());
        Field("Notes", $"{s.CollectedNotes.Count}  ({s.CollectedNotes.Count(n => n.IsMatched)} matched)");
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.PrefixLabel("Color");
            EditorGUI.DrawRect(GUILayoutUtility.GetRect(40, 16, GUILayout.Width(40)), s.Color);
        }

        if (r.status == RowStatus.Orphan || r.status == RowStatus.Legacy || r.status == RowStatus.NameMismatch)
            DrawFixControls(r);

        if (s.SerializedTrackScores.Count > 0)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Role scores", EditorStyles.boldLabel);
            foreach (var sc in s.SerializedTrackScores) Field(sc.Role.ToString(), sc.Score.ToString("0.00"));
        }

        if (s.TrackBins.Count > 0)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Track bins", EditorStyles.boldLabel);
            foreach (var g in s.TrackBins.GroupBy(b => b.Role))
                Field(g.Key.ToString(), $"{g.Count(b => b.IsFilled)}/{g.Count()} filled, {g.Sum(b => b.MatchedNoteCount)} matched / {g.Sum(b => b.UnmatchedNoteCount)} unmatched");
        }

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Note sketch (step × pitch)", EditorStyles.boldLabel);
        DrawNoteSketch(s);

        EditorGUILayout.Space(4);
        _showJson = EditorGUILayout.Foldout(_showJson, "Raw JSON", true);
        if (_showJson)
        {
            _rawJson ??= File.ReadAllText(r.path);
            EditorGUILayout.TextArea(_rawJson, GUILayout.MaxHeight(220));
        }

        EditorGUILayout.EndScrollView();
    }

    private string PhaseNameSuffix(MotifSnapshot s)
    {
        var ph = _library?.phases?.FirstOrDefault(p => p != null && p.phaseId == s.PhaseId);
        return ph != null ? $"  \"{ph.phaseName}\"" : "";
    }

    private static void Field(string label, string value)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label(label, GUILayout.Width(120));
            EditorGUILayout.SelectableLabel(value, GUILayout.Height(EditorGUIUtility.singleLineHeight));
        }
    }

    private static void DrawNoteSketch(MotifSnapshot s)
    {
        var rect = GUILayoutUtility.GetRect(10, 110, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(rect, new Color(0.1f, 0.1f, 0.12f));
        if (s.CollectedNotes.Count == 0 || s.TotalSteps <= 0) return;

        int min = s.CollectedNotes.Min(n => n.Note), max = s.CollectedNotes.Max(n => n.Note);
        float pitchSpan = Mathf.Max(1, max - min + 1);
        float cellW = rect.width / s.TotalSteps, cellH = Mathf.Min(10f, rect.height / pitchSpan);

        foreach (var n in s.CollectedNotes)
        {
            float x = rect.x + (n.Step % s.TotalSteps) * cellW;
            float y = rect.yMax - (n.Note - min + 1) * (rect.height / pitchSpan);
            var c = n.TrackColor; c.a = n.IsMatched ? 1f : 0.45f;
            EditorGUI.DrawRect(new Rect(x, y, Mathf.Max(2f, cellW - 1f), cellH), c);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Fix / assign
    // ---------------------------------------------------------------------------------------------

    private void DrawFixControls(Row r)
    {
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Repair", EditorStyles.boldLabel);

        if (GUILayout.Button(r.status switch
            {
                RowStatus.Legacy => "Backfill PhaseId from PhaseIndex",
                RowStatus.NameMismatch => "Rename file to match its slot",
                _ => "Auto re-home (first slot with this MotifId)",
            }))
            FixRows(new List<Row> { r });

        var phases = _library?.phases;
        if (phases == null || phases.Count == 0) return;

        _assignPhase = Mathf.Clamp(_assignPhase, 0, phases.Count - 1);
        _assignPhase = EditorGUILayout.Popup("Assign to phase",
            _assignPhase, phases.Select((p, i) => $"{i}: {p?.phaseName}").ToArray());
        var motifs = phases[_assignPhase]?.motifs ?? new List<MotifProfile>();
        if (motifs.Count == 0) return;
        _assignMotif = Mathf.Clamp(_assignMotif, 0, motifs.Count - 1);
        _assignMotif = EditorGUILayout.Popup("Assign to motif",
            _assignMotif, motifs.Select((m, i) => $"{i}: {m?.motifId}").ToArray());

        if (GUILayout.Button("Assign to slot"))
        {
            var s = r.snap;
            s.PhaseId = phases[_assignPhase].phaseId;
            s.PhaseIndex = _assignPhase;
            s.MotifId = motifs[_assignMotif].motifId;
            s.MotifIndex = _assignMotif;
            if (MoveToSlot(r)) Refresh();
        }
    }

    private void FixRows(List<Row> rows)
    {
        int fixedCount = 0;
        foreach (var r in rows)
        {
            if (r.snap == null || r.status == RowStatus.Ok) continue;
            var s = r.snap;

            if (r.status == RowStatus.Legacy)
            {
                var phases = _library?.phases;
                if (phases == null || s.PhaseIndex < 0 || s.PhaseIndex >= phases.Count) continue;
                s.PhaseId = phases[s.PhaseIndex]?.phaseId;
                if (string.IsNullOrEmpty(s.PhaseId)) continue;
            }
            else if (r.status == RowStatus.Orphan)
            {
                if (!RingSessionStore.RehomeOrphan(s, _library)) continue;
            }

            if (MoveToSlot(r)) fixedCount++;
        }
        Debug.Log($"[RingSessionManager] Fixed {fixedCount} of {rows.Count} rings.");
        Refresh();
    }

    /// <summary>Writes r.snap to the ring_{p}_{m}.json its fields now name, removing the old file.</summary>
    private bool MoveToSlot(Row r)
    {
        string target = Path.Combine(SaveDirectory, $"ring_{r.snap.PhaseIndex}_{r.snap.MotifIndex}.json");

        if (!string.Equals(target, r.path, StringComparison.Ordinal) && File.Exists(target))
        {
            var existing = JsonUtility.FromJson<MotifSnapshot>(File.ReadAllText(target));
            long existingTicks = existing != null && existing.SavedAtUtcTicks > 0
                ? existing.SavedAtUtcTicks : File.GetLastWriteTimeUtc(target).Ticks;
            string existingDate = new DateTime(existingTicks, DateTimeKind.Utc).ToLocalTime().ToString("MM-dd HH:mm");

            int choice = EditorUtility.DisplayDialogComplex("Slot already occupied",
                $"{Path.GetFileName(target)} already exists (saved {existingDate}). Replace it with this ring?",
                "Replace", "Cancel", "Keep Newer");
            if (choice == 1) return false;
            if (choice == 2 && existingTicks >= r.snap.SavedAtUtcTicks)
            {
                // The existing ring is newer: drop this one.
                File.Delete(r.path);
                return true;
            }
        }

        File.WriteAllText(target, JsonUtility.ToJson(r.snap, true));
        if (!string.Equals(target, r.path, StringComparison.Ordinal)) File.Delete(r.path);
        return true;
    }

    private void DeleteRows(List<Row> rows)
    {
        if (rows.Count == 0) return;
        string names = string.Join("\n", rows.Take(8).Select(r => Path.GetFileName(r.path)))
                       + (rows.Count > 8 ? $"\n…and {rows.Count - 8} more" : "");
        if (!EditorUtility.DisplayDialog("Delete ring sessions?", names + "\n\nThis cannot be undone.", "Delete", "Cancel"))
            return;

        foreach (var r in rows)
        {
            try { File.Delete(r.path); }
            catch (Exception e) { Debug.LogWarning($"[RingSessionManager] Failed to delete {r.path}: {e.Message}"); }
        }
        Refresh();
    }
}
