using System.Globalization;
using System.Text;

// Deterministic string form of a completed MotifSolution's committed musical state, for the
// Flight Plan API's `solution.canonical` field (version 1 — see TelemetryConfig/FlightPlanPayloadBuilder
// for why this format, once published, must never change meaning; a future incompatible format
// bumps to version 2 instead of mutating this one).
//
// Deliberately excludes PhaseId/MotifId/VisitOrder/RunId/timestamps/platform — none of those are
// part of "what the player's committed loop actually sounds like," they're context the server
// already has from the surrounding step/envelope.
//
// Bin-filled state IS included: per InstrumentTrack.BinState.cs, IsBinFilled is a first-class
// gate on whether a bin's notes are eligible to play back (and can be force-set true by a
// SuperNode instant-fill without new notes), not a derived/cosmetic progress flag — two voices
// with identical Notes but different Bins can sound different.
//
// authoredRootMidi (the 5th element of InstrumentTrack.GetPersistentLoopNotes()'s tuple) is
// deliberately NOT included: it only matters for re-quantizing pitch under a *different* chord
// progression than the one already applied. MotifSolution.Notes.MidiNote is already the final,
// already-retuned pitch that actually sounded for this completed motif, so it fully captures the
// committed result without it.
//
// Per-role instrument/timbre (MusicalRoleProfile.midiPreset, volumeMultiplier, outputMixerGroup)
// is deliberately NOT included: these are designer-authored mix/instrument settings, not something
// the player did. KNOWN LIMITATION: for a motif using MusicalRoleProfile's RandomPerBin config
// selection mode, the specific instrument config drawn is session-seeded (SessionGenome) and can
// differ between two playthroughs that otherwise produced an identical canonical string here —
// telemetry does not currently capture which config was drawn, so those two "identical" canonical
// solutions could still have sounded different. Flagged, not solved, in this pass.
public static class MotifSolutionCanonicalizer
{
    public const int Version = 1;

    public static string Canonicalize(MotifSolution solution)
    {
        if (solution == null || solution.Voices == null) return "";

        var sb = new StringBuilder();
        bool firstVoice = true;

        // MazeTelemetryRecorder.HandleMotifCompleted already orders Voices by Role then
        // VoiceIndex, and BuildVoice already orders Notes by StepIndex then MidiNote — both
        // preserved here rather than re-sorted, so this stays a pure serialization step.
        foreach (var voice in solution.Voices)
        {
            if (voice == null) continue;
            if (!firstVoice) sb.Append(';');
            firstVoice = false;

            sb.Append('R').Append(voice.Role)
              .Append('V').Append(voice.VoiceIndex);

            sb.Append("B[");
            if (voice.Bins != null)
            {
                for (int i = 0; i < voice.Bins.Count; i++)
                    sb.Append(voice.Bins[i] != null && voice.Bins[i].IsFilled ? '1' : '0');
            }
            sb.Append(']');

            sb.Append("N[");
            if (voice.Notes != null)
            {
                bool firstNote = true;
                foreach (var note in voice.Notes)
                {
                    if (note == null) continue;
                    if (!firstNote) sb.Append('|');
                    firstNote = false;

                    sb.Append(note.StepIndex).Append(':')
                      .Append(note.MidiNote).Append(':')
                      .Append(note.DurationTicks).Append(':')
                      // "G9" round-trips a float exactly (Microsoft-recommended format for
                      // Single) under InvariantCulture, so two identical velocity values always
                      // produce identical text regardless of platform/locale. Velocity itself is
                      // a continuous, physics-derived value (see Collectable.Collection.cs
                      // ComputeHitVelocity127) with no quantization anywhere in the capture path,
                      // so two genuinely different hits legitimately produce different (and
                      // different-looking) canonical strings — that's real musical variation, not
                      // serialization noise, and nothing here invents a rounding rule to hide it.
                      .Append(note.Velocity.ToString("G9", CultureInfo.InvariantCulture));
                }
            }
            sb.Append(']');
        }

        return sb.ToString();
    }
}
