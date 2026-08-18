using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public class MidiToRiffImporterWindow : EditorWindow
{
    private int _tab;

    // ---------------- Single-clip tab ----------------
    private DefaultAsset _midiFile;
    private string _riffId = "riff";
    private int _authoredRootMidi = 60;  // C4 default
    private int _stepsPerBar = 16;

    private int _channelFilter = -1;    // -1 = any
    private bool _clampToBar = false;   // only import within first bar (0..15)
    private bool _dedupeSameStepSameNoteKeepLoudest = true;

    private bool _clampDurToNextOnset = true;
    private bool _verboseLog = false;

    // ---------------- Batch tab ----------------
    private DefaultAsset _batchFolder;
    private int _motifRootMidi = 60;    // target root for the whole motif — pitch class is what matters
    private int _batchStepsPerBar = 16;
    private int _batchChannelFilter = -1;
    private bool _batchClampToBar = false;
    private bool _batchClampDurToNextOnset = true;
    private bool _batchDedupe = true;
    private bool _batchVerboseLog = false;
    private bool _batchScanned;
    private Vector2 _batchScroll;
    private int _batchReferenceIndex = -1; // index into _batchClips designated as the true root/bass anchor
    private int _lastAppliedReferenceIndex = int.MinValue; // sentinel distinct from -1 so the first post-scan frame always applies once
    private readonly List<BatchClipEntry> _batchClips = new List<BatchClipEntry>();

    // Optional second stage: also create+wire a RoleMotifNoteSetConfig per riff into this motif.
    // Leave null to only create RiffAssets, matching the previous (pre-config-wiring) behavior.
    private MotifProfile _batchTargetMotif;
    private MusicalRoleProfile _batchBulkRoleProfile;

    // Per-subfolder role quick-apply. Must be persistent instance state (not a local dictionary
    // rebuilt each OnGUI) so each folder's picked MusicalRoleProfile survives across redraws while
    // the user is still choosing it in the object field, before they click that folder's Apply button.
    private readonly Dictionary<string, MusicalRoleProfile> _batchFolderRolePickers = new Dictionary<string, MusicalRoleProfile>();

    // Per-subfolder octave nudge. octaveShift is a musical register choice (how far above/below the
    // shared chord root this role should sit), not something derivable from raw MIDI bytes the way
    // authoredRootMidi is — different roles routinely need different values (bass near 0, lead often
    // +1/+2), so this must be settable per role, not just the single batch-wide reference-clip value.
    private readonly Dictionary<string, int> _batchFolderOctaveShiftPickers = new Dictionary<string, int>();

    private static readonly string[] NoteNames =
        { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    private class BatchClipEntry
    {
        public string midiAssetPath;
        public string riffId;
        public string subfolder; // immediate parent folder name — lets clips be organized/role-assigned per subfolder
        public bool parseOk;
        public int detectedFirstNoteMidi = -1;
        public bool pitchClassMatch;
        public int octaveShift;

        // Second-stage config wiring (only used if _batchTargetMotif is assigned).
        public MusicalRoleProfile roleProfile;
        public int order; // within-role append order — matters for ByVoice roles (voice 0 = first appended)
    }

    [MenuItem("Tools/Astral Planes/MIDI → Riff Importer")]
    public static void Open()
    {
        GetWindow<MidiToRiffImporterWindow>("MIDI → Riff");
    }

    private void OnGUI()
    {
        _tab = GUILayout.Toolbar(_tab, new[] { "Single Clip", "Batch (Folder)" });
        GUILayout.Space(8);

        if (_tab == 0) DrawSingleTab();
        else DrawBatchTab();
    }

    // ============================================================================================
    // Single-clip tab
    // ============================================================================================

    private void DrawSingleTab()
    {
        EditorGUILayout.LabelField("MIDI → Riff Importer (16-step bar)", EditorStyles.boldLabel);

        _midiFile = (DefaultAsset)EditorGUILayout.ObjectField("MIDI File (.mid)", _midiFile, typeof(DefaultAsset), false);

        _riffId = EditorGUILayout.TextField("Riff ID", _riffId);
        _authoredRootMidi = EditorGUILayout.IntSlider("Authored Root MIDI", _authoredRootMidi, 0, 127);

        _stepsPerBar = EditorGUILayout.IntField("Steps/Bar", _stepsPerBar);
        EditorGUILayout.HelpBox("Assumes 4/4 time throughout this pipeline. Steps/Bar sets quantization resolution only (16 = 16th notes, 32 = 32nd notes).", MessageType.Info);

        _channelFilter = EditorGUILayout.IntSlider("Channel Filter (-1 = any)", _channelFilter, -1, 15);

        _clampToBar = EditorGUILayout.ToggleLeft("Clamp to first bar (0..15 steps)", _clampToBar);
        _clampDurToNextOnset = EditorGUILayout.ToggleLeft("Clamp duration to next onset (monophonic-friendly)", _clampDurToNextOnset);
        _dedupeSameStepSameNoteKeepLoudest = EditorGUILayout.ToggleLeft("Dedupe same step+note (keep loudest)", _dedupeSameStepSameNoteKeepLoudest);
        _verboseLog = EditorGUILayout.ToggleLeft("Verbose log", _verboseLog);

        GUILayout.Space(10);

        GUI.enabled = _midiFile != null;
        if (GUILayout.Button("Import → Create RiffAsset"))
        {
            try
            {
                ImportAndCreateAsset();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MIDI→RIFF] Import failed: {ex}");
            }
        }
        GUI.enabled = true;
    }

    private void ImportAndCreateAsset()
    {
        string assetPath = AssetDatabase.GetAssetPath(_midiFile);
        if (string.IsNullOrEmpty(assetPath) || !assetPath.EndsWith(".mid", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Selected asset is not a .mid file: {assetPath}");

        var riff = BuildRiff(
            assetPath, _riffId, _authoredRootMidi, octaveShift: 0, stepsPerBar: _stepsPerBar,
            channelFilter: _channelFilter, clampToBar: _clampToBar, clampDurToNextOnset: _clampDurToNextOnset,
            dedupeSameStepSameNote: _dedupeSameStepSameNoteKeepLoudest, verboseLog: _verboseLog,
            ticksPerQuarterUsed: out int ticksPerQuarter);

        var asset = CreateRiffAsset(assetPath, _riffId, riff);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Selection.activeObject = asset;

        Debug.Log($"[MIDI→RIFF] Created {AssetDatabase.GetAssetPath(asset)} events={riff.events?.Count ?? 0} tq={ticksPerQuarter} stepsPerBar={_stepsPerBar} loopSteps={riff.loopSteps} clampToBar={_clampToBar}");
        if (riff.events != null)
            foreach (var ev in riff.events)
                Debug.Log($"[MIDI→RIFF]   note={ev.midiNote} step={ev.step} dur={ev.durSteps} vel={ev.velocity01:F2}");
        if (_stepsPerBar > 64)
            Debug.LogWarning($"[MIDI→RIFF] Steps/Bar={_stepsPerBar} seems high. For a standard 4/4 grid: 16 = 16th notes, 32 = 32nd notes.");
    }

    // ============================================================================================
    // Batch tab — one folder of .mid clips → many RiffAssets for one MotifProfile.
    //
    // Each clip's authored root is read straight off its own first MIDI note (real byte data from
    // the exported file — never a hand-typed note name), so DAW-specific octave-label conventions
    // (e.g. Ableton Live's middle-C labeling vs this project's C4=60) can't introduce a wrong root.
    // The octave correction is NOT inferred by voting across every clip that happens to share the
    // target's pitch class — different voices (a subwoofer root vs. a chord-tone a register above)
    // can legitimately sit an octave or more apart despite sharing a letter name, so that heuristic
    // is unreliable. Instead the user designates exactly one scanned clip as the root/bass reference;
    // its own detected-root-vs-target delta becomes the single Riff.octaveShift applied uniformly
    // across every clip in the folder.
    // ============================================================================================

    private void DrawBatchTab()
    {
        EditorGUILayout.LabelField("Batch Import (folder → RiffAssets for one MotifProfile)", EditorStyles.boldLabel);

        _batchFolder = (DefaultAsset)EditorGUILayout.ObjectField("Source Folder", _batchFolder, typeof(DefaultAsset), false);
        _motifRootMidi = EditorGUILayout.IntSlider("Motif Root MIDI (target)", _motifRootMidi, 0, 127);
        EditorGUILayout.HelpBox(
            "Each clip's authored root is auto-detected from its own first MIDI note — no manual note-name conversion needed. " +
            $"After scanning, pick exactly one clip below (typically your bass/anchor voice) as the Root reference — its detected " +
            $"root vs. the target ({NoteNames[((_motifRootMidi % 12) + 12) % 12]}) determines the single octave shift applied to " +
            "every clip in the folder via Riff.octaveShift. Other clips sharing the target's pitch class are flagged as a hint " +
            "only — they are not averaged or voted on, since different voices can legitimately sit in different octaves.",
            MessageType.Info);

        using (new EditorGUILayout.HorizontalScope())
        {
            _batchStepsPerBar = EditorGUILayout.IntField("Steps/Bar", _batchStepsPerBar);
            _batchChannelFilter = EditorGUILayout.IntSlider("Channel Filter", _batchChannelFilter, -1, 15);
        }
        _batchClampToBar = EditorGUILayout.ToggleLeft("Clamp to first bar (0..15 steps)", _batchClampToBar);
        _batchClampDurToNextOnset = EditorGUILayout.ToggleLeft("Clamp duration to next onset (monophonic-friendly)", _batchClampDurToNextOnset);
        _batchDedupe = EditorGUILayout.ToggleLeft("Dedupe same step+note (keep loudest)", _batchDedupe);
        _batchVerboseLog = EditorGUILayout.ToggleLeft("Verbose log", _batchVerboseLog);

        GUILayout.Space(10);
        EditorGUILayout.LabelField("Optional: wire into a MotifProfile", EditorStyles.boldLabel);
        _batchTargetMotif = (MotifProfile)EditorGUILayout.ObjectField("Target MotifProfile", _batchTargetMotif, typeof(MotifProfile), false);
        EditorGUILayout.HelpBox(
            "If set, Import Batch also creates a RoleMotifNoteSetConfig per riff (using the role you assign per row below) and " +
            "appends it to this motif's roleNoteConfigs. Leave unassigned to only create RiffAssets, as before. " +
            "Within-role append order matters for ByVoice roles (voice 0 = first appended) — adjust the Order column if filename order doesn't match your intended voice order.",
            MessageType.Info);

        GUILayout.Space(10);
        GUI.enabled = _batchFolder != null;
        if (GUILayout.Button("Scan Folder"))
        {
            try { ScanBatchFolder(); }
            catch (Exception ex) { Debug.LogError($"[MIDI→RIFF Batch] Scan failed: {ex}"); }
        }
        GUI.enabled = true;

        if (!_batchScanned || _batchClips.Count == 0)
            return;

        // Applies the reference-derived octave shift as a starting default for every clip ONLY when
        // the reference selection actually changes — not every GUI frame, which would otherwise
        // silently clobber any per-row/per-folder manual overrides set below on every repaint.
        if (_batchReferenceIndex != _lastAppliedReferenceIndex)
        {
            int shift = ComputeGlobalOctaveShift();
            foreach (var e in _batchClips)
                e.octaveShift = shift;
            foreach (var folder in _batchFolderOctaveShiftPickers.Keys.ToList())
                _batchFolderOctaveShiftPickers[folder] = shift;
            _lastAppliedReferenceIndex = _batchReferenceIndex;
        }

        GUILayout.Space(8);
        string refLabel = (_batchReferenceIndex >= 0 && _batchReferenceIndex < _batchClips.Count)
            ? _batchClips[_batchReferenceIndex].riffId
            : "none selected";
        EditorGUILayout.LabelField($"Found {_batchClips.Count} clip(s) — root reference: {refLabel}", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "The root reference only sets a starting octaveShift default for every clip. The correct register for a role " +
            "relative to the shared chord root (e.g. Lead sitting an octave above the bass) is a musical choice the raw MIDI " +
            "data can't tell us — tune octaveShift per subfolder or per row below by ear.",
            MessageType.Info);
        if (_batchReferenceIndex < 0)
            EditorGUILayout.HelpBox("Select a Root reference clip below to compute the starting octave shift (defaults to 0 until one is chosen).", MessageType.Warning);

        if (_batchTargetMotif != null)
        {
            if (_batchFolderRolePickers.Count > 1) // a flat (single-folder) scan gets no benefit over "Apply to all rows" below
            {
                EditorGUILayout.LabelField("Set role + octave shift per subfolder (applies immediately):", EditorStyles.boldLabel);
                foreach (var folder in _batchFolderRolePickers.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList())
                {
                    int count = _batchClips.Count(e => e.subfolder == folder);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField($"{folder} ({count})", GUILayout.Width(160));
                        var picked = (MusicalRoleProfile)EditorGUILayout.ObjectField(_batchFolderRolePickers[folder], typeof(MusicalRoleProfile), false, GUILayout.Width(160));
                        if (picked != _batchFolderRolePickers[folder])
                        {
                            _batchFolderRolePickers[folder] = picked;
                            foreach (var e in _batchClips.Where(e => e.subfolder == folder))
                                e.roleProfile = picked;
                        }
                        if (RoleMismatchesFolder(folder, picked))
                            EditorGUILayout.LabelField($"⚠ role={picked.role} ≠ '{folder}'", GUILayout.Width(150));

                        GUILayout.Space(10);
                        EditorGUILayout.LabelField("octave shift", GUILayout.Width(75));
                        int newShift = EditorGUILayout.IntField(_batchFolderOctaveShiftPickers[folder], GUILayout.Width(40));
                        if (newShift != _batchFolderOctaveShiftPickers[folder])
                        {
                            _batchFolderOctaveShiftPickers[folder] = newShift;
                            foreach (var e in _batchClips.Where(e => e.subfolder == folder))
                                e.octaveShift = newShift;
                        }
                    }
                }
                GUILayout.Space(4);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                _batchBulkRoleProfile = (MusicalRoleProfile)EditorGUILayout.ObjectField("Bulk-set role:", _batchBulkRoleProfile, typeof(MusicalRoleProfile), false);
                if (GUILayout.Button("Apply to all rows", GUILayout.Width(140)))
                    foreach (var e in _batchClips)
                        e.roleProfile = _batchBulkRoleProfile;
            }
        }

        _batchScroll = EditorGUILayout.BeginScrollView(_batchScroll, GUILayout.MaxHeight(260));
        for (int i = 0; i < _batchClips.Count; i++)
        {
            var e = _batchClips[i];
            using (new EditorGUILayout.HorizontalScope("box"))
            {
                bool canBeReference = e.parseOk && e.detectedFirstNoteMidi >= 0;
                GUI.enabled = canBeReference;
                bool isReference = _batchReferenceIndex == i;
                bool toggled = EditorGUILayout.ToggleLeft("Root", isReference, GUILayout.Width(50));
                if (toggled && !isReference) _batchReferenceIndex = i;
                else if (!toggled && isReference) _batchReferenceIndex = -1;
                GUI.enabled = true;

                EditorGUILayout.LabelField($"{e.subfolder}/{Path.GetFileName(e.midiAssetPath)}", GUILayout.Width(180));
                e.riffId = EditorGUILayout.TextField(e.riffId, GUILayout.Width(120));

                if (!e.parseOk)
                    EditorGUILayout.LabelField("parse failed — see console", GUILayout.Width(220));
                else if (e.detectedFirstNoteMidi < 0)
                    EditorGUILayout.LabelField("no notes found", GUILayout.Width(220));
                else
                {
                    string matchLabel = e.pitchClassMatch ? " (pitch class match)" : "";
                    EditorGUILayout.LabelField(
                        $"root={e.detectedFirstNoteMidi} ({NoteNames[((e.detectedFirstNoteMidi % 12) + 12) % 12]}){matchLabel}",
                        GUILayout.Width(190));

                    EditorGUILayout.LabelField("oct", GUILayout.Width(24));
                    e.octaveShift = EditorGUILayout.IntField(e.octaveShift, GUILayout.Width(30));
                }

                if (_batchTargetMotif != null)
                {
                    e.roleProfile = (MusicalRoleProfile)EditorGUILayout.ObjectField(e.roleProfile, typeof(MusicalRoleProfile), false, GUILayout.Width(130));
                    EditorGUILayout.LabelField("order", GUILayout.Width(35));
                    e.order = EditorGUILayout.IntField(e.order, GUILayout.Width(30));
                    if (RoleMismatchesFolder(e.subfolder, e.roleProfile))
                        EditorGUILayout.LabelField($"⚠ role={e.roleProfile.role} ≠ folder '{e.subfolder}'", GUILayout.Width(160));
                }
            }
        }
        EditorGUILayout.EndScrollView();

        GUILayout.Space(6);
        int importable = _batchClips.Count(c => c.parseOk && c.detectedFirstNoteMidi >= 0);
        int configurable = _batchTargetMotif != null ? _batchClips.Count(c => c.parseOk && c.detectedFirstNoteMidi >= 0 && c.roleProfile != null) : 0;
        string buttonLabel = _batchTargetMotif != null
            ? $"Import Batch → Create {importable} RiffAsset(s), wire {configurable} into {_batchTargetMotif.name}"
            : $"Import Batch → Create {importable} RiffAsset(s)";
        GUI.enabled = importable > 0;
        if (GUILayout.Button(buttonLabel))
        {
            try { ImportBatch(); }
            catch (Exception ex) { Debug.LogError($"[MIDI→RIFF Batch] Import failed: {ex}"); }
        }
        GUI.enabled = true;
    }

    private void ScanBatchFolder()
    {
        _batchClips.Clear();
        _batchScanned = false;

        string folderPath = AssetDatabase.GetAssetPath(_batchFolder);
        if (string.IsNullOrEmpty(folderPath) || !AssetDatabase.IsValidFolder(folderPath))
        {
            Debug.LogError("[MIDI→RIFF Batch] Select a valid project folder first.");
            return;
        }

        string fullFolder = Path.GetFullPath(folderPath);
        // Recursive: clips are commonly staged in per-role subfolders (Bass/, Groove/, Harmony/, ...).
        // OrdinalIgnoreCase sort on the full path naturally groups results by subfolder (path-prefix
        // sort), keeping each role's clips contiguous for the default within-role Order value below.
        var midiFiles = Directory.GetFiles(fullFolder, "*.mid", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (midiFiles.Count == 0)
        {
            Debug.LogWarning($"[MIDI→RIFF Batch] No .mid files found in {folderPath} or its subfolders.");
            return;
        }

        string dataPath = Application.dataPath.Replace("\\", "/");
        int targetPitchClass = ((_motifRootMidi % 12) + 12) % 12;

        foreach (var fullPath in midiFiles)
        {
            string normalized = fullPath.Replace("\\", "/");
            string assetPath = "Assets" + normalized.Substring(dataPath.Length);

            var entry = new BatchClipEntry
            {
                midiAssetPath = assetPath,
                riffId = Path.GetFileNameWithoutExtension(assetPath),
                subfolder = Path.GetFileName(Path.GetDirectoryName(assetPath)) ?? "",
                order = _batchClips.Count // default to scan/filename order; edit per-row if that doesn't match intended voice order
            };

            if (TryLoadMidiEvents(assetPath, out var rawEvents, out _))
            {
                entry.parseOk = true;
                int? firstNote = DetectFirstNoteMidi(rawEvents, _batchChannelFilter);
                if (firstNote.HasValue)
                {
                    entry.detectedFirstNoteMidi = firstNote.Value;
                    int pitchClass = ((firstNote.Value % 12) + 12) % 12;
                    // Diagnostic only — sharing a pitch class with the target does NOT mean a clip
                    // belongs in the target's exact octave (e.g. a chord-tone voice legitimately
                    // sits an octave above the bass root). Never used to derive a shift automatically;
                    // see _batchReferenceIndex / ComputeGlobalOctaveShift.
                    entry.pitchClassMatch = pitchClass == targetPitchClass;
                }
            }

            _batchClips.Add(entry);
        }

        // Default the reference selection to the first pitch-class match as a starting suggestion —
        // the user confirms or reassigns it by hand in the table below. This only seeds an initial
        // octaveShift default for every clip (applied once — see DrawBatchTab's change-detection);
        // it is a starting point for per-role tuning, not a final answer, since the "right" register
        // for a role relative to the chord root is a musical choice, not something the raw MIDI data
        // alone can tell us.
        _batchReferenceIndex = _batchClips.FindIndex(e => e.pitchClassMatch);
        _lastAppliedReferenceIndex = int.MinValue;
        if (_batchReferenceIndex < 0)
            Debug.LogWarning($"[MIDI→RIFF Batch] No clip's first note matched the target pitch class ({NoteNames[targetPitchClass]}). Select a Root reference clip manually before importing, or the octave shift will default to 0.");

        _batchFolderRolePickers.Clear();
        _batchFolderOctaveShiftPickers.Clear();
        foreach (var folder in _batchClips.Select(e => e.subfolder).Distinct())
        {
            _batchFolderRolePickers[folder] = null;
            _batchFolderOctaveShiftPickers[folder] = 0;
        }

        _batchScanned = true;
    }

    /// <summary>
    /// The octave shift is derived from exactly one user-designated reference clip (typically the
    /// bass/anchor voice), not a vote across every clip sharing the target's pitch class — different
    /// voices can legitimately sit an octave or more apart while sharing a letter name.
    /// </summary>
    private int ComputeGlobalOctaveShift()
    {
        if (_batchReferenceIndex < 0 || _batchReferenceIndex >= _batchClips.Count) return 0;
        var refEntry = _batchClips[_batchReferenceIndex];
        if (!refEntry.parseOk || refEntry.detectedFirstNoteMidi < 0) return 0;
        return Mathf.RoundToInt((_motifRootMidi - refEntry.detectedFirstNoteMidi) / 12f);
    }

    /// <summary>
    /// Cheap sanity check, not a guarantee: flags when an assigned profile's role enum name doesn't
    /// appear in the source folder name (e.g. folder "Bass" given a profile whose role is Groove) —
    /// catches the common mistake of dragging the wrong MusicalRoleProfile asset, or a freshly-created
    /// profile that was never given a role and is silently sitting at the enum's first value (Bass).
    /// A clean pass here is not proof the assignment is right; it only means the two names don't clash.
    /// </summary>
    private static bool RoleMismatchesFolder(string folder, MusicalRoleProfile profile)
    {
        if (profile == null || string.IsNullOrEmpty(folder)) return false;
        return folder.IndexOf(profile.role.ToString(), StringComparison.OrdinalIgnoreCase) < 0;
    }

    private void ImportBatch()
    {
        int created = 0;
        int wired = 0;
        var createdAssets = new List<UnityEngine.Object>();
        // Grouped by role so within-role order (Order column) can be applied before appending —
        // this is what determines voice index for ByVoice roles, not scan/creation order overall.
        var newConfigsByRole = new Dictionary<MusicalRoleProfile, List<(int order, RoleMotifNoteSetConfig cfg)>>();

        foreach (var e in _batchClips)
        {
            if (!e.parseOk || e.detectedFirstNoteMidi < 0) continue;

            var riff = BuildRiff(
                e.midiAssetPath, e.riffId, e.detectedFirstNoteMidi, e.octaveShift, _batchStepsPerBar,
                _batchChannelFilter, _batchClampToBar, _batchClampDurToNextOnset, _batchDedupe, _batchVerboseLog,
                ticksPerQuarterUsed: out _);

            var riffAsset = CreateRiffAsset(e.midiAssetPath, e.riffId, riff);
            createdAssets.Add(riffAsset);
            created++;

            Debug.Log($"[MIDI→RIFF Batch] Created {AssetDatabase.GetAssetPath(riffAsset)} authoredRootMidi={riff.authoredRootMidi} octaveShift={riff.octaveShift} events={riff.events?.Count ?? 0} loopSteps={riff.loopSteps}");

            if (_batchTargetMotif != null && e.roleProfile != null)
            {
                var cfg = CreateRoleMotifNoteSetConfig(riffAsset, e.riffId, e.roleProfile);
                createdAssets.Add(cfg);
                wired++;

                if (!newConfigsByRole.TryGetValue(e.roleProfile, out var list))
                {
                    list = new List<(int order, RoleMotifNoteSetConfig cfg)>();
                    newConfigsByRole[e.roleProfile] = list;
                }
                list.Add((e.order, cfg));
            }
        }

        if (_batchTargetMotif != null)
        {
            foreach (var kv in newConfigsByRole)
            {
                foreach (var (_, cfg) in kv.Value.OrderBy(t => t.order))
                    _batchTargetMotif.roleNoteConfigs.Add(cfg);

                Debug.Log($"[MIDI→RIFF Batch] Appended {kv.Value.Count} config(s) for role '{kv.Key.role}' ({kv.Key.name}) to {_batchTargetMotif.name}, in Order-column sequence.");
            }
            EditorUtility.SetDirty(_batchTargetMotif);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        if (createdAssets.Count > 0)
            Selection.objects = createdAssets.ToArray();

        string wiredMsg = _batchTargetMotif != null ? $", wired {wired} into {_batchTargetMotif.name}" : "";
        Debug.Log($"[MIDI→RIFF Batch] Done. Created {created}/{_batchClips.Count} RiffAsset(s){wiredMsg}.");
    }

    private static RoleMotifNoteSetConfig CreateRoleMotifNoteSetConfig(RiffAsset riffAsset, string riffId, MusicalRoleProfile roleProfile)
    {
        var cfg = ScriptableObject.CreateInstance<RoleMotifNoteSetConfig>();
        cfg.id = riffId;
        cfg.riff = riffAsset;
        cfg.roleProfile = roleProfile;

        string dir = Path.GetDirectoryName(AssetDatabase.GetAssetPath(riffAsset))?.Replace("\\", "/") ?? "Assets";
        string outPath = AssetDatabase.GenerateUniqueAssetPath($"{dir}/{riffId}-NoteSetConfig.asset");
        AssetDatabase.CreateAsset(cfg, outPath);
        return cfg;
    }

    // ============================================================================================
    // Shared build/create logic (used by both tabs)
    // ============================================================================================

    private static Riff BuildRiff(
        string midiAssetPath, string riffId, int authoredRootMidi, int octaveShift, int stepsPerBar,
        int channelFilter, bool clampToBar, bool clampDurToNextOnset, bool dedupeSameStepSameNote,
        bool verboseLog, out int ticksPerQuarterUsed)
    {
        if (stepsPerBar <= 0)
            throw new InvalidOperationException("Steps/Bar must be > 0 (e.g., 16).");

        if (!TryLoadMidiEvents(midiAssetPath, out var rawEvents, out int ticksPerQuarter))
            throw new InvalidOperationException($"Could not load MIDI events from '{midiAssetPath}'. See console log for details.");

        if (ticksPerQuarter <= 0) ticksPerQuarter = 480; // safe fallback; many files use 480
        ticksPerQuarterUsed = ticksPerQuarter;

        // Assumes 4/4 throughout this pipeline (NoteSetFactory does the same): ticksPerBar = PPQ * 4.
        float ticksPerStepF = (ticksPerQuarter * 4f) / stepsPerBar;

        var notes = ExtractNotePairs(rawEvents, ticksPerStepF, channelFilter, stepsPerBar, clampToBar, verboseLog);

        int loopSteps = stepsPerBar;
        if (!clampToBar && notes != null && notes.Count > 0)
        {
            int maxEndExclusive = 0;
            for (int i = 0; i < notes.Count; i++)
            {
                var n = notes[i];
                int endExclusive = n.step + Mathf.Max(1, n.durSteps);
                if (endExclusive > maxEndExclusive) maxEndExclusive = endExclusive;
            }

            int bars = Mathf.CeilToInt(maxEndExclusive / (float)stepsPerBar);
            loopSteps = Mathf.Max(stepsPerBar, bars * stepsPerBar);
        }

        if (clampDurToNextOnset)
            ClampDurationsToNextOnset(notes, loopSteps);

        if (dedupeSameStepSameNote)
            notes = DedupeSameStepSameNote(notes);

        return new Riff
        {
            id = riffId,
            authoredRootMidi = authoredRootMidi,
            loopSteps = loopSteps,
            stepsPerBar = stepsPerBar,
            events = notes
                .OrderBy(n => n.step)
                .ThenBy(n => n.midiNote)
                .ToList(),
            overlapPolicy = clampDurToNextOnset ? RiffOverlapPolicy.ClampToNextOnset : RiffOverlapPolicy.AllowOverlap,
            octaveShift = octaveShift
        };
    }

    private static RiffAsset CreateRiffAsset(string sourceMidiAssetPath, string riffId, Riff riff)
    {
        string dir = Path.GetDirectoryName(sourceMidiAssetPath)?.Replace("\\", "/") ?? "Assets";
        string outPath = AssetDatabase.GenerateUniqueAssetPath($"{dir}/{riffId}.asset");

        var asset = ScriptableObject.CreateInstance<RiffAsset>();
        asset.riff = riff;

        AssetDatabase.CreateAsset(asset, outPath);
        return asset;
    }

    /// <summary>
    /// Earliest NoteOn in the clip (by tick; ties broken to the lowest pitch — the likely bass/root
    /// note of a chord voicing). This is real MIDI byte data, unaffected by whatever octave-label
    /// convention a DAW's piano roll UI happens to use.
    /// </summary>
    private static int? DetectFirstNoteMidi(List<MidiEvt> rawEvents, int channelFilter)
    {
        int? bestNote = null;
        long bestTick = long.MaxValue;

        foreach (var e in rawEvents)
        {
            if (!e.isNoteOn) continue;
            if (channelFilter >= 0 && e.channel != channelFilter) continue;

            if (e.tick < bestTick || (e.tick == bestTick && (bestNote == null || e.note < bestNote)))
            {
                bestTick = e.tick;
                bestNote = e.note;
            }
        }

        return bestNote;
    }

    // --------------------------------------------------------------------------------------------
    // MIDI loading (Standard MIDI File parser) — no external dependency
    // --------------------------------------------------------------------------------------------

    private struct MidiEvt
    {
        public long tick;     // absolute tick in file
        public int note;      // 0..127
        public int velocity;  // 0..127
        public int channel;   // 0..15
        public bool isNoteOn; // true for NoteOn with vel>0, false for NoteOff or NoteOn vel=0
    }

    private static bool TryLoadMidiEvents(string midiAssetPath, out List<MidiEvt> events, out int ticksPerQuarter)
    {
        events = new List<MidiEvt>();
        ticksPerQuarter = -1;

        try
        {
            // AssetDatabase path → absolute filesystem path
            string fullPath = Path.GetFullPath(midiAssetPath);
            if (!File.Exists(fullPath))
            {
                Debug.LogError($"[MIDI→RIFF] File not found: {fullPath}");
                return false;
            }

            byte[] data = File.ReadAllBytes(fullPath);
            if (data == null || data.Length < 14)
            {
                Debug.LogError("[MIDI→RIFF] File too small to be a MIDI file.");
                return false;
            }

            var reader = new SmfReader(data);

            // Header
            if (!reader.TryReadChunkId(out string hdrId) || hdrId != "MThd")
            {
                Debug.LogError("[MIDI→RIFF] Missing MThd header.");
                return false;
            }

            uint hdrLen = reader.ReadU32BE();
            if (hdrLen < 6)
            {
                Debug.LogError($"[MIDI→RIFF] Invalid header length: {hdrLen}");
                return false;
            }

            ushort format = reader.ReadU16BE();
            ushort nTracks = reader.ReadU16BE();
            ushort division = reader.ReadU16BE();

            // Skip any extra header bytes beyond 6
            int extra = (int)hdrLen - 6;
            if (extra > 0) reader.Skip(extra);

            // Division: if high bit set, it's SMPTE timecode — not supported here.
            if ((division & 0x8000) != 0)
            {
                Debug.LogError("[MIDI→RIFF] SMPTE time division MIDI not supported by this importer.");
                return false;
            }

            ticksPerQuarter = division;
            if (ticksPerQuarter <= 0) ticksPerQuarter = 480;

            // Tracks
            for (int t = 0; t < nTracks; t++)
            {
                if (!reader.TryReadChunkId(out string trkId) || trkId != "MTrk")
                {
                    Debug.LogError($"[MIDI→RIFF] Missing MTrk chunk at track {t}.");
                    return false;
                }

                uint trkLen = reader.ReadU32BE();
                int trackEnd = reader.Pos + (int)trkLen;

                long absTick = 0;
                int runningStatus = -1;

                // Active note-ons (channel,note) → stack of (tick, vel)
                // (Supports overlapping same note)
                var active = new Dictionary<(int ch, int note), Stack<(long tick, int vel)>>();

                while (reader.Pos < trackEnd)
                {
                    long delta = reader.ReadVarLen();
                    absTick += delta;

                    int status = reader.PeekU8();

                    if (status < 0x80)
                    {
                        // Running status: reuse prior status byte
                        if (runningStatus < 0)
                        {
                            // Malformed stream, try to recover by skipping byte
                            reader.ReadU8();
                            continue;
                        }
                        status = runningStatus;
                    }
                    else
                    {
                        status = reader.ReadU8();
                        runningStatus = status;
                    }

                    // Meta event
                    if (status == 0xFF)
                    {
                        int metaType = reader.ReadU8();
                        long len = reader.ReadVarLen();
                        reader.Skip((int)len);

                        // Running status is undefined across meta events in some interpretations,
                        // but most parsers keep it; we'll keep it.
                        continue;
                    }

                    // SysEx
                    if (status == 0xF0 || status == 0xF7)
                    {
                        long len = reader.ReadVarLen();
                        reader.Skip((int)len);
                        continue;
                    }

                    int cmd = status & 0xF0;
                    int chn = status & 0x0F;

                    switch (cmd)
                    {
                        case 0x80: // Note Off: key, vel
                        {
                            int note = reader.ReadU8();
                            int vel  = reader.ReadU8(); // unused
                            HandleNoteOff(active, events, absTick, chn, note);
                            break;
                        }
                        case 0x90: // Note On: key, vel (vel=0 => NoteOff)
                        {
                            int note = reader.ReadU8();
                            int vel  = reader.ReadU8();
                            if (vel == 0)
                                HandleNoteOff(active, events, absTick, chn, note);
                            else
                                HandleNoteOn(active, absTick, chn, note, vel);
                            break;
                        }
                        case 0xA0: // Poly aftertouch: key, pressure
                        case 0xB0: // CC: controller, value
                        case 0xE0: // Pitch bend: lsb, msb
                        {
                            reader.Skip(2);
                            break;
                        }
                        case 0xC0: // Program change: program
                        case 0xD0: // Channel pressure: pressure
                        {
                            reader.Skip(1);
                            break;
                        }
                        default:
                        {
                            // Unknown/unsupported status. Best effort: bail out of this track chunk.
                            // Prevent infinite loop by skipping one byte.
                            reader.Skip(1);
                            break;
                        }
                    }
                }

                // Seek to end of track chunk if we overshot
                if (reader.Pos != trackEnd)
                    reader.Pos = trackEnd;
            }

            // Sort by tick so downstream pairing/quantization is deterministic
            events.Sort((a, b) => a.tick.CompareTo(b.tick));
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MIDI→RIFF] SMF parse error: {ex}");
            return false;
        }
    }

    private static void HandleNoteOn(
        Dictionary<(int ch, int note), Stack<(long tick, int vel)>> active,
        long tick,
        int ch,
        int note,
        int vel)
    {
        var key = (ch, note);
        if (!active.TryGetValue(key, out var st))
        {
            st = new Stack<(long tick, int vel)>();
            active[key] = st;
        }
        st.Push((tick, vel));
    }

    private static void HandleNoteOff(
        Dictionary<(int ch, int note), Stack<(long tick, int vel)>> active,
        List<MidiEvt> outEvents,
        long offTick,
        int ch,
        int note)
    {
        var key = (ch, note);
        if (!active.TryGetValue(key, out var st) || st.Count == 0)
            return;

        var (onTick, onVel) = st.Pop();

        // Emit a NoteOn + NoteOff pair as two MidiEvts so ExtractNotePairs() continues to work
        outEvents.Add(new MidiEvt
        {
            tick = onTick,
            note = note,
            velocity = onVel,
            channel = ch,
            isNoteOn = true
        });

        outEvents.Add(new MidiEvt
        {
            tick = offTick,
            note = note,
            velocity = 0,
            channel = ch,
            isNoteOn = false
        });
    }

    // --------------------------------------------------------------------------------------------
    // Minimal SMF binary reader
    // --------------------------------------------------------------------------------------------

    private sealed class SmfReader
    {
        private readonly byte[] _data;
        public int Pos { get; set; }

        public SmfReader(byte[] data)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            Pos = 0;
        }

        public bool TryReadChunkId(out string id)
        {
            id = null;
            if (Pos + 4 > _data.Length) return false;
            id = System.Text.Encoding.ASCII.GetString(_data, Pos, 4);
            Pos += 4;
            return true;
        }

        public void Skip(int count)
        {
            Pos = Mathf.Clamp(Pos + count, 0, _data.Length);
        }

        public int PeekU8()
        {
            if (Pos >= _data.Length) return -1;
            return _data[Pos];
        }

        public int ReadU8()
        {
            if (Pos >= _data.Length) throw new EndOfStreamException();
            return _data[Pos++];
        }

        public ushort ReadU16BE()
        {
            int b0 = ReadU8();
            int b1 = ReadU8();
            return (ushort)((b0 << 8) | b1);
        }

        public uint ReadU32BE()
        {
            int b0 = ReadU8();
            int b1 = ReadU8();
            int b2 = ReadU8();
            int b3 = ReadU8();
            return (uint)((b0 << 24) | (b1 << 16) | (b2 << 8) | b3);
        }

        public long ReadVarLen()
        {
            long value = 0;
            for (int i = 0; i < 4; i++)
            {
                int b = ReadU8();
                value = (value << 7) | (b & 0x7F);
                if ((b & 0x80) == 0) break;
            }
            return value;
        }
    }

    // --------------------------------------------------------------------------------------------
    // Note pairing + quantization
    // --------------------------------------------------------------------------------------------

    private static List<RiffNoteEvent> ExtractNotePairs(
        List<MidiEvt> events,
        float ticksPerStep,
        int channelFilter,
        int stepsPerBar,
        bool clampToBar,
        bool verbose)
    {
        // Sort by tick for deterministic pairing.
        events.Sort((a, b) => a.tick.CompareTo(b.tick));

        // Active notes keyed by (channel,note) -> stack of start ticks (supports overlapping same note).
        var active = new Dictionary<(int ch, int note), Stack<(long tick, int vel)>>();

        var output = new List<RiffNoteEvent>();

        int StepFromTick(long tick)
        {
            // Quantize to nearest *absolute* step index.
            // If clampToBar==true, collapse into a single bar [0..stepsPerBar-1].
            int s = Mathf.RoundToInt((float)tick / ticksPerStep);

            if (clampToBar)
                return Mathf.Clamp(s, 0, stepsPerBar - 1);

            // Multi-bar import: KEEP absolute step index (0..N).
            return Mathf.Max(0, s);
        }

        int StepsFromDurTicks(long durTicks)
        {
            // Duration in steps (no longer capped to 1 bar).
            int ds = Mathf.RoundToInt((float)durTicks / ticksPerStep);
            return Mathf.Max(1, ds);
        }

        // If importing only 1 bar, ignore any note whose ONSET is beyond bar length.
        // Bar length in ticks = stepsPerBar * ticksPerStep.
        long barLenTicks = (long)Mathf.RoundToInt(stepsPerBar * ticksPerStep);

        foreach (var e in events)
        {
            if (channelFilter >= 0 && e.channel != channelFilter) continue;

            var key = (e.channel, e.note);

            if (e.isNoteOn)
            {
                if (!active.TryGetValue(key, out var st))
                {
                    st = new Stack<(long tick, int vel)>();
                    active[key] = st;
                }
                st.Push((e.tick, e.velocity));
            }
            else
            {
                // NoteOff: match to most recent NoteOn
                if (!active.TryGetValue(key, out var st) || st.Count == 0)
                    continue;

                var (onTick, onVel) = st.Pop();

                if (clampToBar && onTick >= barLenTicks)
                    continue; // onset outside the first bar

                long durTicks = Math.Max(1, e.tick - onTick);

                int step = StepFromTick(onTick);
                int durSteps = StepsFromDurTicks(durTicks);

                // If clamping to bar, also clamp duration so it can't exceed remaining bar space.
                if (clampToBar)
                {
                    int maxDur = Mathf.Max(1, stepsPerBar - step);
                    durSteps = Mathf.Clamp(durSteps, 1, maxDur);
                }

                output.Add(new RiffNoteEvent
                {
                    step = step,
                    midiNote = e.note,
                    durSteps = durSteps,
                    velocity01 = Mathf.Clamp01(onVel / 127f)
                });

                if (verbose)
                    Debug.Log($"[MIDI→RIFF] note={e.note} ch={e.channel} onTick={onTick} offTick={e.tick} step={step} durSteps={durSteps} vel={onVel}");
            }
        }

        return output;
    }

    private static void ClampDurationsToNextOnset(List<RiffNoteEvent> notes, int loopSteps)
    {
        if (notes == null || notes.Count == 0) return;
        if (loopSteps <= 0) return;

        // Build per-pitch lists of indices (stable, unambiguous).
        var byPitch = new Dictionary<int, List<int>>();
        for (int i = 0; i < notes.Count; i++)
        {
            int pitch = notes[i].midiNote;
            if (!byPitch.TryGetValue(pitch, out var list))
            {
                list = new List<int>();
                byPitch[pitch] = list;
            }
            list.Add(i);
        }

        foreach (var kv in byPitch)
        {
            var idxs = kv.Value;

            // Sort indices by the note's step (stable tie-break by index).
            idxs.Sort((a, b) =>
            {
                int sa = notes[a].step;
                int sb = notes[b].step;
                int c = sa.CompareTo(sb);
                return (c != 0) ? c : a.CompareTo(b);
            });

            for (int i = 0; i < idxs.Count; i++)
            {
                int idx = idxs[i];
                var n = notes[idx];

                int nextStep = (i < idxs.Count - 1) ? notes[idxs[i + 1]].step : loopSteps; // end of loop
                int remaining = Mathf.Max(1, loopSteps - n.step);
                int maxDur = Mathf.Clamp(nextStep - n.step, 1, remaining);

                if (n.durSteps > maxDur)
                {
                    n.durSteps = maxDur;
                    notes[idx] = n;
                }
            }
        }
    }

    private static List<RiffNoteEvent> DedupeSameStepSameNote(List<RiffNoteEvent> notes)
    {
        // If a MIDI chord has stacked duplicate on same pitch (rare), keep loudest.
        return notes
            .GroupBy(n => (n.step, n.midiNote))
            .Select(g =>
            {
                var best = g.OrderByDescending(x => x.velocity01).First();
                // If multiple, take the longest duration among those near loudest
                best.durSteps = g.Max(x => x.durSteps);
                return best;
            })
            .ToList();
    }
}
