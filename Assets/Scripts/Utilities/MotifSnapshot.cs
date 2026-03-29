using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// =============================================================================
// SerializableColor
// =============================================================================
// Surrogate for UnityEngine.Color. JsonUtility cannot serialize Color directly.
// Use this everywhere a Color needs to survive disk serialization.
// Runtime code that needs a real Color calls .ToColor().
// =============================================================================

[System.Serializable]
public struct SerializableColor
{
    public float r, g, b, a;

    public SerializableColor(Color c) { r = c.r; g = c.g; b = c.b; a = c.a; }

    public Color ToColor() => new Color(r, g, b, a);

    public static implicit operator SerializableColor(Color c) => new SerializableColor(c);
    public static implicit operator Color(SerializableColor c) => c.ToColor();
}

// =============================================================================
// SerializableMusicalRoleScore
// =============================================================================
// Flat surrogate for one entry in Dictionary<MusicalRole, float>.
// JsonUtility cannot serialize dictionaries; we store as a List of these
// and reconstruct the dictionary on demand via TrackScores property.
// =============================================================================

[System.Serializable]
public struct SerializableMusicalRoleScore
{
    public MusicalRole Role;
    public float       Score;
}

// =============================================================================
// MotifSnapshot
// =============================================================================
// Captures everything needed to reconstruct coral geometry and musical context
// for one completed motif. Fully serializable via JsonUtility / JSON file.
//
// RUNTIME ACCESS PATTERN
// ─────────────────────
// During gameplay: construct via the static Build() factory which accepts
// the runtime Color/Dictionary types and converts internally.
//
// For reading: use the typed properties (Color, TrackScores) which
// reconstruct from the serializable backing fields on demand.
//
// For persistence: pass the whole MotifSnapshot to JsonUtility.ToJson()
// directly — all backing fields are [Serializable]-safe.
// =============================================================================

[System.Serializable]
public class MotifSnapshot
{
    // -------------------------------------------------------------------------
    // Core identity
    // -------------------------------------------------------------------------

    public MazeArchetype Pattern;

    /// <summary>Serializable backing field. Use the Color property at runtime.</summary>
    public SerializableColor SerializedColor;

    public float Timestamp;

    /// <summary>Total drum-track steps at commit time. Normalises NoteEntry.Step → 0–1
    /// bridge time in GrowTracksAnimated.</summary>
    public int TotalSteps;

    /// <summary>MIDI root note of the motif (default C4 = 60). Gives root-note buds
    /// a distinct visual treatment.</summary>
    public int MotifKeyRootMidi;

    // -------------------------------------------------------------------------
    // Runtime-typed properties (reconstruct from serializable backing)
    // -------------------------------------------------------------------------

    /// <summary>Primary motif color. Reconstructed from SerializedColor at runtime.</summary>
    [System.NonSerialized]
    private Color? _color;
    public Color Color
    {
        get
        {
            if (!_color.HasValue) _color = SerializedColor.ToColor();
            return _color.Value;
        }
        set
        {
            _color = value;
            SerializedColor = value;
        }
    }

    // -------------------------------------------------------------------------
    // Track scores
    // -------------------------------------------------------------------------

    /// <summary>Serializable backing for TrackScores. One entry per role.</summary>
    public List<SerializableMusicalRoleScore> SerializedTrackScores = new();

    /// <summary>Per-role performance scores. Reconstructed from SerializedTrackScores.
    /// Mutations write through to the backing list.</summary>
    [System.NonSerialized]
    private Dictionary<MusicalRole, float> _trackScores;

    public Dictionary<MusicalRole, float> TrackScores
    {
        get
        {
            if (_trackScores == null)
                RebuildTrackScoresCache();
            return _trackScores;
        }
    }

    /// <summary>Write a role score. Updates both the runtime cache and the
    /// serializable backing list so disk state stays in sync.</summary>
    private void SetTrackScore(MusicalRole role, float score)
    {
        // Update cache
        if (_trackScores == null) RebuildTrackScoresCache();
        _trackScores[role] = score;

        // Update backing list (replace existing entry or append)
        for (int i = 0; i < SerializedTrackScores.Count; i++)
        {
            if (SerializedTrackScores[i].Role == role)
            {
                SerializedTrackScores[i] = new SerializableMusicalRoleScore
                    { Role = role, Score = score };
                return;
            }
        }
        SerializedTrackScores.Add(new SerializableMusicalRoleScore
            { Role = role, Score = score });
    }

    private void RebuildTrackScoresCache()
    {
        _trackScores = new Dictionary<MusicalRole, float>(SerializedTrackScores.Count);
        foreach (var entry in SerializedTrackScores)
            _trackScores[entry.Role] = entry.Score;
    }

    // -------------------------------------------------------------------------
    // Note entries
    // -------------------------------------------------------------------------

    public List<NoteEntry> CollectedNotes = new();

    // -------------------------------------------------------------------------
    // Coral geometry
    // -------------------------------------------------------------------------

    public List<TrackBinData> TrackBins = new();

    // =========================================================================
    // Factory
    // =========================================================================

    /// <summary>
    /// Construct a MotifSnapshot from runtime types. Use this instead of
    /// setting fields directly so Color and TrackScores serialize correctly.
    /// </summary>
    public static MotifSnapshot Build(
        MazeArchetype pattern,
        Color color,
        float timestamp,
        int totalSteps,
        int motifKeyRootMidi,
        Dictionary<MusicalRole, float> trackScores = null)
    {
        var snap = new MotifSnapshot
        {
            Pattern          = pattern,
            Timestamp        = timestamp,
            TotalSteps       = totalSteps,
            MotifKeyRootMidi = motifKeyRootMidi,
        };

        snap.Color = color; // writes through to SerializedColor

        if (trackScores != null)
            foreach (var kv in trackScores)
                snap.SetTrackScore(kv.Key, kv.Value);

        return snap;
    }

    // =========================================================================
    // NoteEntry
    // =========================================================================

    [System.Serializable]
    public class NoteEntry
    {
        public int   Step;
        public int   Note;
        public float Velocity;
        public int   BinIndex;
        public bool  IsMatched;

        /// <summary>
        /// Normalized collection speed for this note within its bin.
        /// 0 = collected first (fastest), 1 = collected last (slowest).
        /// Drives ring tug depth in MotifRingGlyphGenerator.
        /// </summary>
        public float CommitTime01;

        /// <summary>Serializable backing. Use TrackColor property at runtime.</summary>
        public SerializableColor SerializedTrackColor;

        [System.NonSerialized]
        private Color? _trackColor;
        public Color TrackColor
        {
            get
            {
                if (!_trackColor.HasValue) _trackColor = SerializedTrackColor.ToColor();
                return _trackColor.Value;
            }
            set
            {
                _trackColor = value;
                SerializedTrackColor = value;
            }
        }

        public NoteEntry() { }

        public NoteEntry(int step, int note, float velocity, Color trackColor,
                         int binIndex = 0, bool isMatched = false, float commitTime01 = 0f)
        {
            Step         = step;
            Note         = note;
            Velocity     = velocity;
            BinIndex     = binIndex;
            IsMatched    = isMatched;
            CommitTime01 = commitTime01;
            TrackColor   = trackColor; // writes through to SerializedTrackColor
        }
    }

    // =========================================================================
    // TrackBinData
    // =========================================================================

    [System.Serializable]
    public class TrackBinData
    {
        public MusicalRole Role;
        public int         BinIndex;
        public bool        IsFilled;

        /// <summary>Time.time when this bin was marked filled. -1 if not filled.</summary>
        public float CompletionTime;

        /// <summary>Time.time when the motif started. Used to compute fill duration.</summary>
        public float MotifStartTime;

        public int MatchedNoteCount;
        public int UnmatchedNoteCount;

        public List<int> CollectedSteps = new();

        /// <summary>Serializable backing. Use TrackColor property at runtime.</summary>
        public SerializableColor SerializedTrackColor;

        [System.NonSerialized]
        private Color? _trackColor;
        public Color TrackColor
        {
            get
            {
                if (!_trackColor.HasValue) _trackColor = SerializedTrackColor.ToColor();
                return _trackColor.Value;
            }
            set
            {
                _trackColor = value;
                SerializedTrackColor = value;
            }
        }

        public TrackBinData() { }

        public TrackBinData(Color trackColor)
        {
            TrackColor = trackColor;
        }

        /// <summary>How long this bin took to fill, in seconds. 0 if not filled.</summary>
        public float FillDurationSeconds =>
            IsFilled && CompletionTime >= MotifStartTime
                ? CompletionTime - MotifStartTime
                : 0f;

        /// <summary>Fraction of collected notes that matched the authored template [0..1].</summary>
        public float MatchRatio
        {
            get
            {
                int total = MatchedNoteCount + UnmatchedNoteCount;
                return total > 0 ? (float)MatchedNoteCount / total : 0f;
            }
        }
    }
}
