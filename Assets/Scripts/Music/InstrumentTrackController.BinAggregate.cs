using UnityEngine;

// Cross-track bin-content aggregate for the drum breadth axis (see DrumTrack.BeatIntensity.cs).
public partial class InstrumentTrackController
{
    // Fraction of allocated bins (across all live tracks) that are also filled.
    // Denominator is allocated-bin count, not maxLoopMultiplier*trackCount, because
    // allocation (current structure), not capacity, is what breadth measures fill against.
    public float GetBinCompletionFraction()
    {
        if (tracks == null || tracks.Length == 0) return 0f;

        int allocated = 0, filled = 0;
        foreach (var t in tracks)
        {
            if (t == null) continue;
            int hiAlloc = t.GetHighestAllocatedBin();
            for (int b = 0; b <= hiAlloc; b++)
            {
                allocated++;
                if (t.IsBinFilled(b)) filled++;
            }
        }
        return allocated > 0 ? Mathf.Clamp01(filled / (float)allocated) : 0f;
    }

    // Filled ÷ allocated bins for one role's track (0 if the role has no track this motif).
    // Drum intensity: Bass fill lifts the non-Bass ceiling (see DrumTrack.BeatIntensity.cs).
    public float GetRoleBinFill01(MusicalRole role)
    {
        var t = FindTrackForRole(role);
        if (t == null) return 0f;

        int allocated = 0, filled = 0;
        int hiAlloc = t.GetHighestAllocatedBin();
        for (int b = 0; b <= hiAlloc; b++)
        {
            allocated++;
            if (t.IsBinFilled(b)) filled++;
        }
        return allocated > 0 ? Mathf.Clamp01(filled / (float)allocated) : 0f;
    }

    public InstrumentTrack FindTrackForRole(MusicalRole role)
    {
        if (tracks == null) return null;
        foreach (var t in tracks)
            if (t != null && t.assignedRole == role) return t;
        return null;
    }
}
