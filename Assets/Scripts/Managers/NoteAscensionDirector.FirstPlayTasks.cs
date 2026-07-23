using System.Collections.Generic;
using UnityEngine;

public sealed partial class NoteAscensionDirector
{
    [Header("First-Play Particle")]
    [Tooltip("How many particles to emit when a first-play task arrives at the line.")]
    public int firstPlayConfirmEmitCount = 6;

    private struct FirstPlayTask
    {
        public ParticleSystem ps;
        public Vector3 start;
        public Vector3 end;
        public Color color;
        public double startDsp;
        public double endDsp;
    }

    private readonly List<FirstPlayTask> _firstPlayTasks = new();

    /// <summary>
    /// Register a "first play" catch-up particle task.
    /// The particle will travel from startWorld to endWorld over durationSeconds (DSP time).
    /// </summary>
    public void EnqueueFirstPlayTask(
        ParticleSystem ps,
        Vector3 startWorld,
        Vector3 endWorld,
        Color color,
        float durationSeconds)
    {
        if (ps == null) return;

        double now = AudioSettings.dspTime;
        _firstPlayTasks.Add(new FirstPlayTask
        {
            ps = ps,
            start = startWorld,
            end = endWorld,
            color = color,
            startDsp = now,
            endDsp = now + durationSeconds
        });
    }

    // Step first-play catch-up tasks — called from Update().
    private void TickFirstPlayTasks()
    {
        if (_firstPlayTasks.Count == 0) return;

        double now = AudioSettings.dspTime;

        for (int i = _firstPlayTasks.Count - 1; i >= 0; i--)
        {
            var t = _firstPlayTasks[i];

            if (t.ps == null)
            {
                _firstPlayTasks.RemoveAt(i);
                continue;
            }

            double span = t.endDsp - t.startDsp;
            float raw = (span > 0.0) ? (float)((now - t.startDsp) / span) : 1f;
            float eased = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(raw));

            // This makes late spawns appear already in progress instead of racing to catch up.
            Vector3 p = Vector3.Lerp(t.start, t.end, eased);
            t.ps.transform.position = p;

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
}
