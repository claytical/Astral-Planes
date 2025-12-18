using UnityEngine;

public static class BeatIntensityUtility
{
    /// <summary>
    /// Computes a normalized intensity [0..1] based on how many notes
    /// were collected in a fuse window vs how many were possible.
    ///
    /// totalNotesInWindow  = how many notes could have been collected
    /// collectedInWindow   = how many were actually collected
    ///
    /// minIntensity / maxIntensity let you keep the floor above 0 if you
    /// never want the system to feel "dead" even when nothing is collected.
    /// </summary>
    public static float ComputeCollectionIntensityOverFuseWindow(
        int totalNotesInWindow,
        int collectedInWindow,
        float minIntensity = 0f,
        float maxIntensity = 1f)
    {
        // Degenerate case: nothing to collect in this window.
        if (totalNotesInWindow <= 0)
            return Mathf.Clamp01(minIntensity);

        // Fraction of the window successfully harvested.
        float fraction = Mathf.Clamp01((float)collectedInWindow / totalNotesInWindow);

        // Optionally, you can curve this (e.g. quadratic) if you want
        // to emphasize high-completion windows:
        // fraction = fraction * fraction;

        return Mathf.Lerp(minIntensity, maxIntensity, fraction);
    }
}