using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public class MidiToRiffImporterWindow : EditorWindow
{
    private DefaultAsset _midiFile;
    private string _riffId = "riff";
    private int _authoredRootMidi = 60;  // C4 default
    private int _stepsPerBar = 16;
    private int _beatsPerBar = 4;

    private int _channelFilter = -1;    // -1 = any
    private bool _clampToBar = true;    // only import within first bar (0..15)
    private bool _dedupeSameStepSameNoteKeepLoudest = true;

    private bool _clampDurToNextOnset = true;
    private bool _verboseLog = false;

    [MenuItem("Tools/Astral Planes/MIDI → Riff Importer")]
    public static void Open()
    {
        GetWindow<MidiToRiffImporterWindow>("MIDI → Riff");
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("MIDI → Riff Importer (16-step bar)", EditorStyles.boldLabel);

        _midiFile = (DefaultAsset)EditorGUILayout.ObjectField("MIDI File (.mid)", _midiFile, typeof(DefaultAsset), false);

        _riffId = EditorGUILayout.TextField("Riff ID", _riffId);
        _authoredRootMidi = EditorGUILayout.IntSlider("Authored Root MIDI", _authoredRootMidi, 0, 127);

        using (new EditorGUILayout.HorizontalScope())
        {
            _beatsPerBar = EditorGUILayout.IntField("Beats/Bar", _beatsPerBar);
            _stepsPerBar = EditorGUILayout.IntField("Steps/Bar", _stepsPerBar);
        }
        EditorGUILayout.HelpBox("Assumes 4/4-style bar quantization: stepsPerBeat = stepsPerBar / beatsPerBar.", MessageType.Info);

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

        GUILayout.Space(6);
        EditorGUILayout.HelpBox(
            "Loader note: this uses MidiPlayerTK via reflection. If your MidiPlayerTK version exposes a different loader API, " +
            "the error log will tell you what type/method was missing, and you can patch the 'TryLoadMidiEvents' method accordingly.",
            MessageType.None);
    }

    private void ImportAndCreateAsset()
    {
        string assetPath = AssetDatabase.GetAssetPath(_midiFile);
        if (string.IsNullOrEmpty(assetPath) || !assetPath.EndsWith(".mid", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Selected asset is not a .mid file: {assetPath}");

        if (_stepsPerBar <= 0 || _beatsPerBar <= 0 || (_stepsPerBar % _beatsPerBar) != 0)
            throw new InvalidOperationException("Steps/Bar must be divisible by Beats/Bar (e.g., 16/4).");

        if (!TryLoadMidiEvents(assetPath, out var rawEvents, out int ticksPerQuarter))
            throw new InvalidOperationException("Could not load MIDI events via MidiPlayerTK reflection. See console log for details.");

        if (ticksPerQuarter <= 0) ticksPerQuarter = 480; // safe fallback; many files use 480

        int stepsPerBeat = _stepsPerBar / _beatsPerBar;
        float ticksPerStepF = ticksPerQuarter / (float)stepsPerBeat;

        // Convert raw midi events into note pairs.
        var notes = ExtractNotePairs(rawEvents, ticksPerStepF, _channelFilter, _stepsPerBar, _clampToBar, _verboseLog);

        // Optional: clamp duration to next onset per pitch (monophonic-ish articulation)
        if (_clampDurToNextOnset)
            ClampDurationsToNextOnset(notes, _stepsPerBar);

        // Optional: dedupe identical (step,note) collisions
        if (_dedupeSameStepSameNoteKeepLoudest)
            notes = DedupeSameStepSameNote(notes);

        // Build riff
        var riff = new Riff
        {
            id = _riffId,
            authoredRootMidi = _authoredRootMidi,
            loopSteps = 16, // lock to 16 regardless of UI; your system is 16-step bins
            events = notes
                .OrderBy(n => n.step)
                .ThenBy(n => n.midiNote)
                .ToList(),
            overlapPolicy = _clampDurToNextOnset ? RiffOverlapPolicy.ClampToNextOnset : RiffOverlapPolicy.AllowOverlap,
            clampToTrackRange = false,
            octaveShift = 0
        };

        // Create asset next to MIDI file
        string dir = Path.GetDirectoryName(assetPath)?.Replace("\\", "/") ?? "Assets";
        string outPath = AssetDatabase.GenerateUniqueAssetPath($"{dir}/{_riffId}.asset");

        var asset = ScriptableObject.CreateInstance<RiffAsset>();
        asset.riff = riff;

        AssetDatabase.CreateAsset(asset, outPath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Selection.activeObject = asset;

        Debug.Log($"[MIDI→RIFF] Created {outPath} events={riff.events?.Count ?? 0} tq={ticksPerQuarter}");
    }

    // --------------------------------------------------------------------------------------------
    // MIDI loading via MidiPlayerTK reflection
    // --------------------------------------------------------------------------------------------

    private struct MidiEvt
    {
        public long tick;     // absolute tick in file
        public int note;      // 0..127
        public int velocity;  // 0..127
        public int channel;   // 0..15
        public bool isNoteOn; // true for NoteOn with vel>0, false for NoteOff or NoteOn vel=0
    }

// --------------------------------------------------------------------------------------------
// MIDI loading (Standard MIDI File parser) — NO MidiPlayerTK dependency
// --------------------------------------------------------------------------------------------

private bool TryLoadMidiEvents(string midiAssetPath, out List<MidiEvt> events, out int ticksPerQuarter)
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
                        // Prevent infinite loop.
                        // (You can loosen this later if needed.)
                        // Debug.LogWarning($"[MIDI→RIFF] Unknown status 0x{status:X2} at tick {absTick}.");
                        // Attempt to recover by skipping one byte.
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

    // Emit a NoteOn + NoteOff pair as two MidiEvts so your existing ExtractNotePairs() continues to work
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
    private static int? ReadIntProperty(object obj, Type type, string propName)
    {
        var p = type.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
        if (p == null) return null;
        try
        {
            var v = p.GetValue(obj);
            if (v is int i) return i;
        }
        catch { }
        return null;
    }

    private bool TryConvertMptkEvent(object mptkEventObj, out MidiEvt evt)
    {
        evt = default;

        if (mptkEventObj == null) return false;
        var t = mptkEventObj.GetType();

        // MPTKEvent fields/properties we need: Tick, Value (note), Velocity, Channel, Command
        long tick = ReadLong(t, mptkEventObj, "Tick") ?? ReadLong(t, mptkEventObj, "tick") ?? -1;
        int note = ReadInt(t, mptkEventObj, "Value") ?? ReadInt(t, mptkEventObj, "value") ?? -1;
        int vel  = ReadInt(t, mptkEventObj, "Velocity") ?? ReadInt(t, mptkEventObj, "velocity") ?? 0;
        int ch   = ReadInt(t, mptkEventObj, "Channel") ?? ReadInt(t, mptkEventObj, "channel") ?? 0;

        object cmdObj = ReadObj(t, mptkEventObj, "Command") ?? ReadObj(t, mptkEventObj, "command");
        if (tick < 0 || note < 0 || cmdObj == null) return false;

        string cmdName = cmdObj.ToString(); // typically "NoteOn" / "NoteOff"
        bool isNoteOn = cmdName.Contains("NoteOn", StringComparison.OrdinalIgnoreCase) && vel > 0;
        bool isNoteOff = cmdName.Contains("NoteOff", StringComparison.OrdinalIgnoreCase) ||
                         (cmdName.Contains("NoteOn", StringComparison.OrdinalIgnoreCase) && vel == 0);

        if (!isNoteOn && !isNoteOff) return false;

        evt = new MidiEvt
        {
            tick = tick,
            note = note,
            velocity = vel,
            channel = ch,
            isNoteOn = isNoteOn
        };
        return true;
    }

    private static long? ReadLong(Type t, object o, string name)
    {
        var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (p != null)
        {
            var v = p.GetValue(o);
            if (v is long l) return l;
            if (v is int i) return i;
        }
        var f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
        if (f != null)
        {
            var v = f.GetValue(o);
            if (v is long l) return l;
            if (v is int i) return i;
        }
        return null;
    }

    private static int? ReadInt(Type t, object o, string name)
    {
        var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (p != null)
        {
            var v = p.GetValue(o);
            if (v is int i) return i;
        }
        var f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
        if (f != null)
        {
            var v = f.GetValue(o);
            if (v is int i) return i;
        }
        return null;
    }

    private static object ReadObj(Type t, object o, string name)
    {
        var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (p != null) return p.GetValue(o);
        var f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
        if (f != null) return f.GetValue(o);
        return null;
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
            // Quantize to nearest step index within bar.
            int s = Mathf.RoundToInt((float)tick / ticksPerStep);
            if (clampToBar) s = Mathf.Clamp(s, 0, stepsPerBar - 1);
            else s = ((s % stepsPerBar) + stepsPerBar) % stepsPerBar;
            return s;
        }

        int StepsFromDurTicks(long durTicks)
        {
            int ds = Mathf.RoundToInt((float)durTicks / ticksPerStep);
            return Mathf.Clamp(ds, 1, stepsPerBar); // allow full bar hold
        }

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
                long durTicks = Math.Max(1, e.tick - onTick);

                int step = StepFromTick(onTick);
                int durSteps = StepsFromDurTicks(durTicks);

                // If clamping to bar and note starts beyond bar, ignore
                if (clampToBar && (onTick > (long)(ticksPerStep * (stepsPerBar - 0.5f))))
                    continue;

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

    private static void ClampDurationsToNextOnset(List<RiffNoteEvent> notes, int stepsPerBar)
    {
        // Clamp per pitch independently (simple, predictable for bass/lead).
        // If you want "global monophonic", clamp against next onset regardless of pitch instead.
        var byNote = notes.GroupBy(n => n.midiNote);
        foreach (var g in byNote)
        {
            var ordered = g.OrderBy(n => n.step).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                var n = ordered[i];
                int nextStep = (i < ordered.Count - 1) ? ordered[i + 1].step : stepsPerBar; // end of bar
                int maxDur = Mathf.Clamp(nextStep - n.step, 1, stepsPerBar);
                if (n.durSteps > maxDur)
                {
                    n.durSteps = maxDur;
                    // write back into original list (match by step+note+vel; stable enough for importer use)
                    int idx = notes.FindIndex(x => x.step == ordered[i].step && x.midiNote == ordered[i].midiNote && Mathf.Approximately(x.velocity01, ordered[i].velocity01));
                    if (idx >= 0) notes[idx] = n;
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