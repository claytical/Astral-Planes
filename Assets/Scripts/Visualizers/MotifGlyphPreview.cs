using System;
using System.Collections.Generic;
using UnityEngine;
using Random = System.Random;

// ─────────────────────────────────────────────────────────────────────────────
//  MotifGlyphPreview
//
//  Drop alongside a GlyphApplicator. Configure fake per-role data in the
//  Inspector, then right-click → "Preview Glyph" to see the result.
//
//  Toggle "Live Preview" to re-render every time any inspector value changes.
// ─────────────────────────────────────────────────────────────────────────────

[Serializable]
public class RolePreviewData
{
    public bool        Active    = false;
    public MusicalRole Role      = MusicalRole.Bass;
    [Range(0, 32)]  public int   NoteCount  = 8;
    [Range(1, 8)]   public int   BinCount   = 2;
    [Range(0, 8)]   public int   FilledBins = 2;
    [Range(0f, 1f)] public float MatchRatio = 0.7f;
    [Range(0f, 1f)] public float Velocity   = 0.6f;
    public Color TrackColor = Color.white;
}

public class MotifGlyphPreview : MonoBehaviour
{
    [Header("Target")]
    public GlyphApplicator Target;

    [Header("Snapshot globals")]
    public MazeArchetype Pattern     = MazeArchetype.Windows;
    public Color         MotifColor  = new Color(0.4f, 0.8f, 1f);
    [Range(8, 64)]  public int TotalSteps   = 16;
    [Range(0, 127)] public int KeyRootMidi  = 60;

    [Header("Roles")]
    public RolePreviewData Bass    = new() { Active = true,  Role = MusicalRole.Bass,    TrackColor = new Color(0.3f, 0.5f, 1f) };
    public RolePreviewData Harmony = new() { Active = false, Role = MusicalRole.Harmony, TrackColor = new Color(0.3f, 1f, 0.5f) };
    public RolePreviewData Lead    = new() { Active = false, Role = MusicalRole.Lead,    TrackColor = new Color(1f, 0.8f, 0.2f) };
    public RolePreviewData Groove  = new() { Active = false, Role = MusicalRole.Groove,  TrackColor = new Color(1f, 0.3f, 0.4f) };

    [Header("Options")]
    [Tooltip("Re-render every time any Inspector value changes (edit-mode only).")]
    public bool LivePreview = false;

    // -------------------------------------------------------------------------

    [ContextMenu("Preview Glyph")]
    public void PreviewGlyph()
    {
        if (Target == null) Target = GetComponent<GlyphApplicator>();
        if (Target == null) { Debug.LogError("[GlyphPreview] No GlyphApplicator on this GameObject."); return; }
        Target.Apply(BuildSnapshot());
    }

    [ContextMenu("Clear Glyph")]
    public void ClearGlyph()
    {
        if (Target == null) Target = GetComponent<GlyphApplicator>();
        Target?.Clear();
    }

    private void OnValidate()
    {
        if (LivePreview)
            PreviewGlyph();
    }

    // -------------------------------------------------------------------------
    //  Snapshot construction
    // -------------------------------------------------------------------------

    private MotifSnapshot BuildSnapshot()
    {
        var snap = new MotifSnapshot
        {
            Pattern          = Pattern,
            Color            = MotifColor,
            Timestamp        = 0f,
            TotalSteps       = Mathf.Max(1, TotalSteps),
            MotifKeyRootMidi = KeyRootMidi,
        };

        foreach (var role in new[] { Bass, Harmony, Lead, Groove })
        {
            if (!role.Active) continue;
            AppendRoleData(snap, role);
        }

        return snap;
    }

    private void AppendRoleData(MotifSnapshot snap, RolePreviewData r)
    {
        int binCount   = Mathf.Max(1, r.BinCount);
        int filledBins = Mathf.Clamp(r.FilledBins, 0, binCount);
        int noteCount  = Mathf.Max(0, r.NoteCount);
        int binSize    = Mathf.Max(1, snap.TotalSteps / binCount);

        var rng = new Random((int)r.Role * 7919 + noteCount);

        // --- TrackBinData ---
        for (int b = 0; b < binCount; b++)
        {
            float fillDur = (float)(rng.NextDouble() * 4.0 + 0.5);
            snap.TrackBins.Add(new MotifSnapshot.TrackBinData
            {
                Role               = r.Role,
                BinIndex           = b,
                IsFilled           = b < filledBins,
                CompletionTime     = b < filledBins ? fillDur : 0f,
                MotifStartTime     = 0f,
                MatchedNoteCount   = Mathf.RoundToInt(noteCount / (float)binCount * r.MatchRatio),
                UnmatchedNoteCount = Mathf.RoundToInt(noteCount / (float)binCount * (1f - r.MatchRatio)),
                CollectedSteps     = new List<int>(),
                TrackColor         = r.TrackColor,
            });
        }

        // --- NoteEntry ---
        if (noteCount == 0) return;

        int rootPitch = KeyRootMidi % 12;
        for (int i = 0; i < noteCount; i++)
        {
            int binIndex  = (int)((i / (float)noteCount) * binCount);
            binIndex      = Mathf.Clamp(binIndex, 0, binCount - 1);
            int localStep = (int)(rng.NextDouble() * binSize);
            int globalStep = binIndex * binSize + localStep;

            // Pitch: mix of root-adjacent and chromatic, weighted by velocity
            int note = rootPitch + (int)(rng.NextDouble() * 12);
            note = Mathf.Clamp(note + 48, 36, 96); // keep in playable range

            bool isMatched = rng.NextDouble() < r.MatchRatio;
            float vel = Mathf.Clamp01(r.Velocity + (float)(rng.NextDouble() - 0.5) * 0.2f);

            snap.CollectedNotes.Add(new MotifSnapshot.NoteEntry(
                step:       globalStep,
                note:       note,
                velocity:   vel,
                trackColor: r.TrackColor,
                binIndex:   binIndex,
                isMatched:  isMatched
            ));
        }
    }
}