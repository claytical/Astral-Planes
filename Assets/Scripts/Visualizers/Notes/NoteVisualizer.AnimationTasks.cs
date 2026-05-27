using System;
using System.Collections.Generic;
using UnityEngine;

public partial class NoteVisualizer
{
    private void TickAnimationTasks()
    {
        for (int i = _blastTasks.Count - 1; i >= 0; i--)
        {
            var task = _blastTasks[i];
            if (!task.go) { _blastTasks.RemoveAt(i); continue; }

            task.t += Time.deltaTime / Mathf.Max(0.0001f, task.dur);
            float u = Mathf.Clamp01(task.t);

            task.go.transform.position = task.startPos + task.dir * u;
            task.go.transform.localScale = Vector3.one * Mathf.Lerp(task.startScale, task.endScale, u);

            if (u >= 1f)
            {
                try { task.onDone?.Invoke(); } catch (System.Exception e) { Debug.LogException(e); }
                if (task.go) Destroy(task.go);
                _blastTasks.RemoveAt(i);
            }
            else
            {
                _blastTasks[i] = task;
            }
        }

        for (int i = _rushTasks.Count - 1; i >= 0; i--)
        {
            var task = _rushTasks[i];
            if (!task.go || !task.target) { _rushTasks.RemoveAt(i); continue; }

            task.t += Time.deltaTime / Mathf.Max(0.0001f, task.dur);
            float u = Mathf.Clamp01(task.t);

            task.go.transform.position = Vector3.Lerp(task.startPos, task.target.position, u);

            if (u >= 1f)
            {
                try { task.onArrive?.Invoke(); } catch (System.Exception e) { Debug.LogException(e); }
                if (task.go) Destroy(task.go);
                _rushTasks.RemoveAt(i);
            }
            else
            {
                _rushTasks[i] = task;
            }
        }
    }

    public void ScheduleFirstPlayConfirm(Transform source, InstrumentTrack track, int step, double dspTime, Color color, float noteDuration)
    {
        if (track == null || source == null) return;
        Debug.Log($"[CONFIRM_SCHED] track={track.name} step={step} dsp={dspTime:F6} now={AudioSettings.dspTime:F6} dt={(dspTime-AudioSettings.dspTime):F4}");
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

    private void UpdateFirstPlayConfirmTasks()
    {
        if (_firstPlayTasks.Count == 0) return;

        double now = AudioSettings.dspTime;

        for (int i = _firstPlayTasks.Count - 1; i >= 0; i--)
        {
            var t = _firstPlayTasks[i];
            if (t.ps == null) { _firstPlayTasks.RemoveAt(i); continue; }

            double dur = System.Math.Max(0.0001, t.endDsp - t.startDsp);
            float u = Mathf.Clamp01((float)((now - t.startDsp) / dur));
            float eased = u * u * (3f - 2f * u); // SmoothStep

            t.ps.transform.position = Vector3.Lerp(t.start, t.end, eased);

            if (now >= t.endDsp)
            {
                var emitParams = new ParticleSystem.EmitParams
                {
                    position = t.end,
                    startColor = t.color
                };
                t.ps.Emit(emitParams, firstPlayConfirmEmitCount);
                t.ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                Destroy(t.ps.gameObject, 0.35f);
                _firstPlayTasks.RemoveAt(i);
            }
            else
            {
                _firstPlayTasks[i] = t;
            }
        }
    }

    private void EnqueueBlast(GameObject marker, Vector3 dir, float durationSeconds, float startScale = 1f, float endScale = 0.2f, System.Action onDone = null)
    {
        if (!marker) return;

        var d = dir;
        if (d.sqrMagnitude < 1e-6f) d = UnityEngine.Random.insideUnitSphere * 0.5f;
        d = d.normalized * 0.8f;

        _blastTasks.Add(new BlastTask
        {
            go         = marker,
            startPos   = marker.transform.position,
            dir        = d,
            startScale = startScale,
            endScale   = endScale,
            dur        = Mathf.Max(0.01f, durationSeconds),
            t          = 0f,
            onDone     = onDone
        });
    }
}
