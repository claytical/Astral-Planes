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
}
