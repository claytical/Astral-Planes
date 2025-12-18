using System;
using System.Collections.Generic;
using UnityEngine;
public enum BeatMood
{
    Ambient,
    Steady,
    Building,
    Intense
}

/// <summary>
/// Collection of MotifProfiles with helper methods to pick one at random.
/// </summary>
[CreateAssetMenu(
    fileName = "MotifLibrary",
    menuName = "Astral Planes/Motif/Motif Library")]
public class MotifLibrary : ScriptableObject
{
    public List<MotifProfile> motifs = new List<MotifProfile>();

    /// <summary>
    /// Uniform random choice among non-null motifs.
    /// </summary>
    public MotifProfile PickRandomUniform(System.Random rng)
    {
        if (motifs == null) return null;

        // Filter out null entries
        var valid = new List<MotifProfile>();
        for (int i = 0; i < motifs.Count; i++)
        {
            if (motifs[i] != null)
                valid.Add(motifs[i]);
        }

        if (valid.Count == 0) return null;
        int index = rng.Next(valid.Count);
        return valid[index];
    }

    /// <summary>
    /// Weighted random choice using MotifProfile.selectionWeight.
    /// If all weights are <= 0, falls back to uniform among non-null motifs.
    /// </summary>
    public MotifProfile PickRandomWeighted(System.Random rng)
    {
        if (motifs == null) return null;

        float total = 0f;
        for (int i = 0; i < motifs.Count; i++)
        {
            var m = motifs[i];
            if (m == null) continue;
            if (m.selectionWeight > 0f)
                total += m.selectionWeight;
        }

        // If no positive weights, fall back to uniform.
        if (total <= 0f)
            return PickRandomUniform(rng);

        float r = (float)rng.NextDouble() * total;
        float acc = 0f;

        for (int i = 0; i < motifs.Count; i++)
        {
            var m = motifs[i];
            if (m == null) continue;

            float w = Mathf.Max(0f, m.selectionWeight);
            if (w <= 0f) continue;

            acc += w;
            if (r <= acc)
                return m;
        }

        // Numerical safety: return last non-null motif.
        for (int i = motifs.Count - 1; i >= 0; i--)
        {
            if (motifs[i] != null)
                return motifs[i];
        }

        return null;
    }
}
