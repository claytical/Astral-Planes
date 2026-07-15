using UnityEngine;
using System.Collections.Generic;

public class NoteSet
{
    public int rootMidi;
    public InstrumentTrack assignedInstrumentTrack;
    public List<Chord> chordRegion;
    public List<(int step, int note, int duration, float vel, int authoredRootMidi)> persistentTemplate;
    public int expireAfterLoops = 0;

    private readonly Dictionary<int, (int note, int dur, float vel, int authoredRootMidi)> _templateByStep
        = new Dictionary<int, (int note, int dur, float vel, int authoredRootMidi)>();

    private List<int> _allowedSteps = new List<int>();
    private List<int> _notes = new List<int>();

    public void Initialize(InstrumentTrack track, int totalSteps)
    {
        BuildTemplateLookup(track, totalSteps);
        BuildAllowedStepsFromTemplate(totalSteps);
        BuildNotesFromTemplate(track);
    }

    private void BuildTemplateLookup(InstrumentTrack track, int totalSteps)
    {
        _templateByStep.Clear();

        if (persistentTemplate == null) return;

        for (int i = 0; i < persistentTemplate.Count; i++)
        {
            var t = persistentTemplate[i];
            int step = t.step;

            if (step < 0 || step >= totalSteps)
                continue;

            int note = t.note;
            if (track != null)
                note = Mathf.Clamp(note, track.lowestAllowedNote, track.highestAllowedNote);

            if (_templateByStep.TryGetValue(step, out var existing))
            {
                if (t.vel > existing.vel)
                    _templateByStep[step] = (note, t.duration, t.vel, t.authoredRootMidi);
            }
            else
            {
                _templateByStep.Add(step, (note, t.duration, t.vel, t.authoredRootMidi));
            }
        }
    }

    private void BuildAllowedStepsFromTemplate(int totalSteps)
    {
        _allowedSteps.Clear();
        var steps = new List<int>(_templateByStep.Keys);
        steps.Sort();
        for (int i = 0; i < steps.Count; i++)
        {
            int s = steps[i];
            if (s >= 0 && s < totalSteps)
                _allowedSteps.Add(s);
        }
    }

    private void BuildNotesFromTemplate(InstrumentTrack track)
    {
        _notes.Clear();
        foreach (var kvp in _templateByStep)
        {
            int n = kvp.Value.note;
            if (track != null)
                n = Mathf.Clamp(n, track.lowestAllowedNote, track.highestAllowedNote);

            bool exists = false;
            for (int i = 0; i < _notes.Count; i++)
            {
                if (_notes[i] == n) { exists = true; break; }
            }
            if (!exists) _notes.Add(n);
        }
        _notes.Sort();
    }

    public bool TryGetTemplateTimingAtStep(int step, out int durationTicks, out float velocity)
    {
        if (_templateByStep.TryGetValue(step, out var tpl))
        {
            durationTicks = tpl.dur;
            velocity = tpl.vel;
            return true;
        }
        durationTicks = 0;
        velocity = 0f;
        return false;
    }

    public int GetNoteForPhaseAndRole(InstrumentTrack track, int step)
    {
        if (_templateByStep.TryGetValue(step, out var e))
            return e.note;

        // Nearest earlier authored step, then first authored.
        int bestStep = int.MinValue;
        foreach (var k in _templateByStep.Keys)
        {
            if (k <= step && k > bestStep) bestStep = k;
        }
        if (bestStep != int.MinValue)
            return _templateByStep[bestStep].note;

        foreach (var k in _templateByStep.Keys)
            return _templateByStep[k].note;

        return rootMidi;
    }

    public int GetAuthoredRootMidi(int step)
    {
        if (_templateByStep.TryGetValue(step, out var e))
            return e.authoredRootMidi;
        return int.MinValue;
    }

    public List<int> GetNoteList() => _notes;

    public bool TryGetNoteRange(out int minNote, out int maxNote)
    {
        if (_notes.Count == 0) { minNote = maxNote = rootMidi; return false; }
        minNote = _notes[0];
        maxNote = _notes[_notes.Count - 1];
        return true;
    }

    public List<int> GetStepList() => _allowedSteps;
}
