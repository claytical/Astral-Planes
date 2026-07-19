using UnityEngine;

public partial class NoteVisualizer
{
    public void ScheduleFirstPlayConfirm(Transform source, InstrumentTrack track, int step, double dspTime, Color color, float noteDuration)
    {
        if (track == null || source == null) return;
        if (GameFlowManager.VerboseLogging) Debug.Log($"[CONFIRM_SCHED] track={track.name} step={step} dsp={dspTime:F6} now={AudioSettings.dspTime:F6} dt={(dspTime-AudioSettings.dspTime):F4}");
        _firstPlayRequests.Add(new FirstPlayConfirmRequest
        {
            source = source,
            track = track,
            step = step,
            dspTime = dspTime,
            color = color,
            duration = noteDuration,
            spawned = false
        });
    }

    private void ProcessFirstPlayConfirmFx()
    {
        if (firstPlayConfirmOrbPrefab == null) return;
        if (_firstPlayRequests.Count == 0) return;

        double now = AudioSettings.dspTime;

        for (int i = 0; i < _firstPlayRequests.Count; i++)
        {
            var r = _firstPlayRequests[i];
            if (r.spawned) continue;

            if (r.dspTime <= now + 0.0001)
            {
                r.spawned = true;
                _firstPlayRequests[i] = r;
                continue;
            }

            Vector3 endWorld;
            if (noteMarkers != null &&
                noteMarkers.TryGetValue((r.track, r.step), out var markerTr) &&
                markerTr != null)
            {
                endWorld = markerTr.position;
            }
            else
            {
                endWorld = (playheadLine != null) ? playheadLine.position : transform.position;
            }

            Vector3 startWorld = r.source != null ? r.source.position : transform.position;

            var ps = Instantiate(
                firstPlayConfirmOrbPrefab,
                startWorld,
                Quaternion.identity,
                _uiParent ? _uiParent : transform
            );

            var main = ps.main;
            main.startColor = r.color;
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            ps.Play(true);
            ascensionDirector?.EnqueueFirstPlayTask(ps, startWorld, endWorld, r.color, r.duration);

            r.spawned = true;
            _firstPlayRequests[i] = r;
        }
    }

}
