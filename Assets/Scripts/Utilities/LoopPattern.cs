using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// LoopPattern is responsible for maintaining the derived loop cache (bin/localStep mapping)
/// from InstrumentTrack.persistentLoopNotes, and providing helpers for bin playback queries.
/// </summary>
[DisallowMultipleComponent]
public class LoopPattern : MonoBehaviour
{
    /// <summary>
    /// Rebuild the track's loop cache if the track marked it dirty.
    /// This is essentially your existing RebuildLoopCacheIfDirty moved out.
    /// </summary>
    public void RebuildLoopCacheIfDirty(InstrumentTrack track)
    {
        if (track == null) return;
        if (!track.LoopCacheDirtyPending) return;

        track.MutableLoopNotes.Clear();

        int binSize = Mathf.Max(1, track.BinSize());
        int maxBins = Mathf.Max(1, track.maxLoopMultiplier);
        int maxStepExclusive = maxBins * binSize;

        var src = track.MutablePersistentLoopNotes;

        for (int i = 0; i < src.Count; i++)
        {
            var (stepIndex, note, duration, velocity, authoredRootMidi) = src[i];
            if (stepIndex < 0 || stepIndex >= maxStepExclusive)
            {
                Debug.LogWarning(
                    $"[TRK:LOOPCACHE] track={track.name} DROP step={stepIndex} maxStep={maxStepExclusive} " +
                    $"(maxBins={maxBins} binSize={binSize})"
                );
                continue;
            }

            int bin = stepIndex / binSize;
            int local = stepIndex % binSize;

            track.MutableLoopNotes.Add(new InstrumentTrack.LoopNotePublic
            {
                bin = bin,
                localStep = local,
                note = note,
                duration = duration,
                velocity = velocity,
                authoredRootMidi = authoredRootMidi
            });
        }

        // Debug counts per bin (optional)
        int[] binCounts = new int[maxBins];
        for (int i = 0; i < track.MutableLoopNotes.Count; i++)
        {
            int b = track.MutableLoopNotes[i].bin;
            if (b >= 0 && b < binCounts.Length) binCounts[b]++;
        }

        Debug.Log($"[TRK:LOOPCACHE] {track.name} bins=" + string.Join(",", binCounts));

        if (src.Count > 0 && track.MutableLoopNotes.Count == 0)
        {
            Debug.LogWarning(
                $"[TRK:LOOPCACHE_EMPTY] track={track.name} persistentCount={src.Count} " +
                $"maxLoopMultiplier={track.maxLoopMultiplier} loopMultiplier={track.loopMultiplier} binSize={binSize}\n" +
                Environment.StackTrace
            );
        }

        track.LoopCacheDirtyPending = false;
    }

    /// <summary>
    /// Returns notes that should play at (bin, localStep). Gain is applied to velocity.
    /// </summary>
    public void GetNotesAt(
        InstrumentTrack track,
        int bin,
        int localStep,
        float gain,
        List<(int note, int duration, float velocity)> outNotes)
    {
        outNotes.Clear();
        if (track == null) return;

        RebuildLoopCacheIfDirty(track);

        var cache = track.MutableLoopNotes;
        for (int i = 0; i < cache.Count; i++)
        {
            var n = cache[i];
            if (n.bin != bin) continue;
            if (n.localStep != localStep) continue;

            // n.velocity is 0..127
            float vel127 = Mathf.Clamp(n.velocity, 60f, 127f) * Mathf.Clamp01(gain);

            outNotes.Add((n.note, n.duration, vel127));

            Debug.Log($"[LP/GET] {track.name} bin={bin} step={localStep} note={n.note} durTicks={n.duration} " +
                      $"storedVel={n.velocity:F2} outVel127={vel127:F2} gain={gain:F2}");
        }
    }

}
