using MidiPlayerTK;
using UnityEngine;

/// <summary>
/// Writes a saved ring's final loop as a type-1 MIDI file: track 0 holds tempo/time signature/name,
/// then one track per active role with its preset (program change), channel gain (CC7) and notes.
///
/// Built from RingPlaybackPlan, so it matches what the carousel preview plays. One step = a 16th
/// note (120 ticks at 480 PPQ); the tempo comes from the ring's real step duration, so the file lines
/// up with recorded audio even when the motif's nominal BPM differs.
/// </summary>
public static class RingMidiExporter
{
    private const int Ppq          = 480;
    private const int TicksPerStep = Ppq / 4;

    public static bool Export(MotifSnapshot snap, PhaseLibrary library, string path, string title)
    {
        var plan = RingPlaybackPlan.Build(snap, library);
        if (plan == null)
        {
            Debug.LogWarning($"[RingMidiExporter] Could not build a playback plan for '{title}'.");
            return false;
        }

        int usPerQuarter = Mathf.Max(1, Mathf.RoundToInt(plan.StepDurationSec * 4f * 1_000_000f));
        float derivedBpm = 60f / (plan.StepDurationSec * 4f);

        var w = new MPTKWriter(Ppq, 1, Mathf.RoundToInt(derivedBpm));
        w.AddTempoChange(0, 0, usPerQuarter);
        w.AddTimeSignature(0, 0, 4, 2);
        w.AddText(0, 0, MPTKMeta.SequenceTrackName, title);
        w.AddText(0, 0, MPTKMeta.TextEvent,
            $"Astral Planes ring {snap.PhaseIndex}_{snap.MotifIndex} | saved bpm {plan.Bpm:0.##} | " +
            $"{plan.TotalSteps} steps ({plan.StepsPerLoop}/bin) | loop {plan.LoopLengthSec:0.###}s");

        int track = 1;
        foreach (var role in plan.ActiveRoles)
        {
            int ch = RingPlaybackPlan.ChannelFor(role);
            w.AddText(track, 0, MPTKMeta.SequenceTrackName, role.ToString());
            w.AddChangePreset(track, 0, ch, plan.ChannelPresets[ch]);
            w.AddControlChange(track, 0, ch, MPTKController.VOLUME_MSB,
                Mathf.Clamp(Mathf.RoundToInt(plan.TrimFor(role) * 100f), 0, 127));

            for (int s = 0; s < plan.TotalSteps; s++)
                foreach (var n in plan.Steps[s])
                {
                    if (n.role != role) continue;
                    int len = Mathf.Max(1, Mathf.RoundToInt(n.durationMs / 1000f / plan.StepDurationSec * TicksPerStep));
                    w.AddNote(track, (long)s * TicksPerStep, n.channel, n.note, n.vel127, len);
                }
            track++;
        }

        // Marks the loop length so a DAW can set its loop brace to it.
        w.AddText(0, (long)plan.TotalSteps * TicksPerStep, MPTKMeta.Marker, "Loop End");

        w.StableSortEvents();
        if (!w.WriteToFile(path))
        {
            Debug.LogWarning($"[RingMidiExporter] Failed to write {path}");
            return false;
        }
        Debug.Log($"[RingMidiExporter] Wrote {path} ({plan.ActiveRoles.Count} tracks, {derivedBpm:0.##} bpm)");
        return true;
    }
}
