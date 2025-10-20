using System;
using UnityEngine;
using System.Collections.Generic;
using Random = UnityEngine.Random;

[CreateAssetMenu(fileName = "RemixUtility", menuName = "Astral Planes/Remix Utility")]
[System.Serializable]
//NOT CURRENTLY INTEGRATED.
//Could be used with chord progression rings: "upgraded" when duplicate existing track utility collected. 
public class RemixUtility : ScriptableObject
{
    public MusicalRole targetRole;
    public MusicalPhase targetPhase;

    public PatternStrategy patternStrategy = PatternStrategy.Arpeggiated;
    public RhythmStyle rhythmStyleOverride = RhythmStyle.Steady;
    public NoteBehavior noteBehaviorOverride = NoteBehavior.Sustain;
    public bool overrideDefaults = false;

    [Range(1, 16)]
    public int notesPerLoop = 8;

    public float minVelocity = 60f;
    public float maxVelocity = 100f;

    public List<int> GeneratePhrase(NoteSet noteSet)
    {
        var use = noteSet != null && Enum.IsDefined(typeof(PatternStrategy), noteSet.patternStrategy) ? noteSet.patternStrategy : patternStrategy; return GenerateFromPattern(noteSet, use);
    }

    public List<(int step, int note, int dur, float vel)> Remix(List<int> collectedNotes, NoteSet context, int totalSteps)
    {
        List<(int step, int note, int dur, float vel)> result = new();

        for (int i = 0; i < totalSteps && collectedNotes.Count > 0; i++)
        {
            int step = i;
            int note = SelectNoteFromStrategy(collectedNotes, context, i);
            int dur = Random.Range(1, totalSteps); // could customize later
            float vel = Random.Range(minVelocity, maxVelocity);

            result.Add((step, note, dur, vel));
        }

        return result;
    }

    private List<int> GenerateFromPattern(NoteSet noteSet, PatternStrategy strategy)
    {
        List<int> phrase = new();

        switch (strategy)
        {
            case PatternStrategy.Arpeggiated:
                var arp = noteSet.GetSortedNoteList();
                for (int i = 0; i < notesPerLoop; i++)
                    phrase.Add(arp[i % arp.Count]);
                break;

            case PatternStrategy.Drone:
                int root = noteSet.GetRootNote();
                for (int i = 0; i < notesPerLoop; i++)
                    phrase.Add(root);
                break;

            case PatternStrategy.Randomized:
                var pool = noteSet.GetNoteList();
                for (int i = 0; i < notesPerLoop; i++)
                    phrase.Add(pool[Random.Range(0, pool.Count)]);
                break;

            case PatternStrategy.StaticRoot:
                int staticRoot = noteSet.GetRootNote();
                for (int i = 0; i < notesPerLoop; i++)
                    phrase.Add(staticRoot);
                break;

            case PatternStrategy.WalkingBass:
                var walk = noteSet.GetSortedNoteList();
                int dir = Random.value > 0.5f ? 1 : -1;
                int current = walk.Count / 2;
                for (int i = 0; i < notesPerLoop; i++)
                {
                    phrase.Add(walk[Mathf.Clamp(current, 0, walk.Count - 1)]);
                    current += dir;
                    if (current < 0 || current >= walk.Count) dir *= -1;
                }
                break;

            default:
                phrase = noteSet.GetNoteList();
                break;
        }

        return phrase;
    }
// RemixUtility.cs  (add anywhere inside the class)
    public static void ApplyPhase(MusicalPhaseProfile profile)
    {
        var gm = GameFlowManager.Instance;
        var drums = gm != null ? gm.activeDrumTrack : null;
        if (drums == null || profile == null) return;

        // Queue the phase; DrumTrack will advance at a musical boundary
        drums.QueuedPhase = profile.phase;

        // Restructure parts for the new phase (your existing remix logic)
        drums.RestructureTracksWithRemixLogic();

        // Swap the drum loop at the end of the current loop
        var clip = MusicalPhaseLibrary.GetRandomClip(profile.phase);
        drums.SchedulePhaseAndLoopChange(profile.phase);
    }

    private int SelectNoteFromStrategy(List<int> collected, NoteSet context, int index)
    {
        switch (patternStrategy)
        {
            case PatternStrategy.Arpeggiated:
            case PatternStrategy.WalkingBass:
                return collected[index % collected.Count];

            case PatternStrategy.Randomized:
                return collected[Random.Range(0, collected.Count)];

            case PatternStrategy.Drone:
            case PatternStrategy.StaticRoot:
                return context.GetRootNote();

            default:
                return collected[index % collected.Count];
        }
    }
}
