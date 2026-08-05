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

    public void RegisterNotePlayTint(InstrumentTrack track, Color color, float durationSeconds)
    {
        if (track == null || durationSeconds <= 0f) return;
        _activeNoteTints.Add(new ActiveNoteTint
        {
            track = track,
            color = color,
            endDsp = AudioSettings.dspTime + durationSeconds
        });
    }

    private void UpdateAmbientLineTint()
    {
        if (firstPlayConfirmOrbPrefab == null) return;

        double now = AudioSettings.dspTime;
        for (int i = _activeNoteTints.Count - 1; i >= 0; i--)
            if (_activeNoteTints[i].endDsp <= now) _activeNoteTints.RemoveAt(i);

        Color target;
        bool holdActive = _activeNoteTints.Count > 0;
        if (holdActive)
        {
            Vector3 sum = Vector3.zero;
            foreach (var t in _activeNoteTints)
                sum += new Vector3(t.color.r, t.color.g, t.color.b);
            Vector3 avg = sum / _activeNoteTints.Count;
            target = new Color(avg.x, avg.y, avg.z, 1f);
        }
        else
        {
            target = Color.white;
        }

        float speed = holdActive ? ambientSnapSpeed : ambientFadeToWhiteSpeed;
        _ambientTintColor = Color.Lerp(_ambientTintColor, target, 1f - Mathf.Exp(-speed * Time.deltaTime));

        var col = firstPlayConfirmOrbPrefab.colorOverLifetime;
        col.enabled = true;
        var g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(_ambientTintColor, 0f), new GradientColorKey(_ambientTintColor, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) }
        );
        col.color = g;
    }

}
