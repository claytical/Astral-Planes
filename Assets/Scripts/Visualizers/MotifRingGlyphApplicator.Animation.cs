using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// ── Low-level draw-in / travel-dot animation coroutines ─────────────────
public partial class MotifRingGlyphApplicator
{
    private Coroutine _shakeCoroutine;

    /// <summary>Routed replacement for AudioSource.PlayClipAtPoint — sends the one-shot through
    /// the Effects mixer group (for anti-click limiting) instead of the unmixed default output,
    /// and uses one temp GameObject+AudioSource per call instead of Unity's internal pool.</summary>
    private void PlayOneShotRouted(AudioClip clip, Vector3 pos, float volume)
    {
        if (clip == null) return;

        var go = new GameObject("OneShotSfx");
        go.transform.position = pos;

        var src = go.AddComponent<AudioSource>();
        src.spatialBlend = 0f; // 2D, matches prior PlayClipAtPoint usage at Camera.main
        src.outputAudioMixerGroup = effectsMixerGroup;
        src.PlayOneShot(clip, volume);

        Destroy(go, clip.length);
    }

    /// <summary>Larger, lower-frequency shake for a successful tether break — reads as a released spring.</summary>
    public void PlayBreakShake()
    {
        if (config == null) return;
        PlayShake(config.breakShakeDuration, config.breakShakeAmplitude, config.breakShakeFrequency);
    }

    /// <summary>Short, high-frequency shake for a denied collision (nothing pending to break) — reads as hitting a wall.</summary>
    public void PlayDenialShake()
    {
        if (config == null) return;
        PlayShake(config.denialShakeDuration, config.denialShakeAmplitude, config.denialShakeFrequency);
    }

    private void PlayShake(float duration, float amplitude, float frequency)
    {
        if (_shakeCoroutine != null) StopCoroutine(_shakeCoroutine);
        _shakeCoroutine = StartCoroutine(ShakeRoutine(duration, amplitude, frequency));
    }

    // Drives transform.localPosition only — never written elsewhere in this class, so this can't
    // fight the show/hide (localScale), play-area fit (position), or spin-off exit (rotation)
    // coroutines that already own the other transform fields.
    private IEnumerator ShakeRoutine(float duration, float amplitude, float frequency)
    {
        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            float decay   = 1f - (t / duration);
            float offsetX = (Mathf.PerlinNoise(Time.time * frequency, 0f) - 0.5f) * 2f * amplitude * decay;
            float offsetY = (Mathf.PerlinNoise(0f, Time.time * frequency) - 0.5f) * 2f * amplitude * decay;
            transform.localPosition = new Vector3(offsetX, offsetY, 0f);
            yield return null;
        }
        transform.localPosition = Vector3.zero;
        _shakeCoroutine = null;
    }

    private static IEnumerator AnimateMeshFill(
        Mesh mesh, int[] fullTris, int segments, float delay, float drawDuration)
    {
        if (delay > 0f) yield return new WaitForSeconds(delay);

        float elapsed = 0f;
        while (elapsed < drawDuration)
        {
            elapsed += Time.deltaTime;
            int visible = Mathf.Clamp(
                Mathf.RoundToInt(Mathf.Clamp01(elapsed / drawDuration) * segments) * 6,
                0, fullTris.Length);
            mesh.SetTriangles(fullTris, 0, visible, 0);
            mesh.RecalculateBounds();
            yield return null;
        }

        mesh.SetTriangles(fullTris, 0);
        mesh.RecalculateBounds();
    }

    private IEnumerator AnimateSingleRing(
        LineRenderer lr, List<Vector2> pts,
        float delay, float drawDuration,
        Transform ringTransform, float rotDegPerSec,
        List<NoteAnimInfo> noteInfos, NoteVisualizer noteViz,
        System.Func<bool> shouldStop,
        bool spherical = false)
    {
        if (delay > 0f) yield return new WaitForSeconds(delay);

        int   total    = pts.Count;
        int   nextNote = 0;
        float elapsed  = 0f;
        lr.positionCount = 2;

        while (elapsed < drawDuration)
        {
            elapsed += Time.deltaTime;
            float progress   = Mathf.Clamp01(elapsed / drawDuration);
            float drawnAngle = progress * Mathf.PI * 2f;

            while (nextNote < noteInfos.Count && drawnAngle >= noteInfos[nextNote].NoteAngle)
            {
                var info = noteInfos[nextNote++];
                if (noteViz?.noteMarkers != null && info.Track != null &&
                    noteViz.noteMarkers.TryGetValue((info.Track, info.AbsStep), out var markerTr) &&
                    markerTr != null)
                {
                    StartCoroutine(TravelNoteDot(
                        markerTr.position, ringTransform,
                        info.RingLocalPos, info.TugLocalPos,
                        config.noteTravelDuration, info.DotColor));
                }
            }

            int count = Mathf.Clamp(Mathf.RoundToInt(progress * total), 2, total);
            lr.positionCount = count;
            for (int i = 0; i < count; i++)
                lr.SetPosition(i, new Vector3(pts[i].x, pts[i].y, 0f));
            yield return null;
        }

        lr.positionCount = total;
        for (int i = 0; i < total; i++)
            lr.SetPosition(i, new Vector3(pts[i].x, pts[i].y, 0f));

        while (!shouldStop())
        {
            if (ringTransform == null) yield break;
            if (spherical)
                ringTransform.Rotate(0f, rotDegPerSec * Time.deltaTime, 0f);
            else
                ringTransform.Rotate(0f, 0f, rotDegPerSec * Time.deltaTime);
            yield return null;
        }
    }

    // Plays the last record ring the same way a gameplay ring animates:
    // starts flat, fires step-synced travel dots before each note's beat,
    // and simultaneously tweens the matching dip into the contour.
    private IEnumerator AnimateLastRecordRing(
        LineRenderer lr, List<Vector2> flatPts,
        float delay,
        Transform ringTransform, float rotDegPerSec,
        List<NoteAnimInfo> noteInfos, NoteVisualizer noteViz,
        MusicalRole role, int binIndex, Color color, int binSteps, float outerR,
        System.Func<bool> shouldStop,
        bool spherical = false)
    {
        if (delay > 0f) yield return new WaitForSeconds(delay);
        if (shouldStop()) yield break;

        // Show the full flat circle immediately — dips tween in as each dot travels.
        ApplyContourPoints(lr, flatPts);

        var drumRef     = GameFlowManager.Instance?.activeDrumTrack;

        float stepDurRec = 0f;
        drumRef?.TryGetNextBaseStepDsp(out _, out stepDurRec);
        int leadSteps = stepDurRec > 0.001f
            ? Mathf.Max(1, Mathf.RoundToInt(config.noteTravelDuration / stepDurRec))
            : FallbackLeadSteps;

        var      revealedNotes = new List<MotifSnapshot.NoteEntry>();
        Coroutine contourTween = null;

        foreach (var info in noteInfos)
        {
            if (shouldStop()) break;

            Transform markerTr = null;
            if (noteViz?.noteMarkers != null && info.Track != null)
                noteViz.noteMarkers.TryGetValue((info.Track, info.AbsStep), out markerTr);

            var capturedInfo = info;
            StartCoroutine(WaitAndLaunchDot(
                ringTransform, info.TugLocalPos, info.DotColor,
                markerTr, outerR, info.NoteAngle,
                drumRef, info.AbsStep, leadSteps, config.noteTravelDuration, shouldStop,
                onLaunch: () =>
                {
                    if (capturedInfo.SourceNote == null) return;
                    revealedNotes.Add(capturedInfo.SourceNote);
                    var targetPoly = MotifRingGlyphGenerator.GenerateSingleRingAtRadius(
                        role, binIndex, color, revealedNotes, binSteps, outerR, config);
                    var targetPts = targetPoly?.Points;
                    if (targetPts != null)
                    {
                        if (contourTween != null) StopCoroutine(contourTween);
                        contourTween = StartCoroutine(
                            TweenContour(lr, targetPts, config.noteTravelDuration));
                    }
                }));
        }

        while (!shouldStop())
        {
            if (ringTransform == null) yield break;
            if (spherical)
                ringTransform.Rotate(0f, rotDegPerSec * Time.deltaTime, 0f);
            else
                ringTransform.Rotate(0f, 0f, rotDegPerSec * Time.deltaTime);
            yield return null;
        }
    }

    private IEnumerator WaitAndLaunchDot(
        Transform ringTransform, Vector3 tugLocal, Color dotColor,
        Transform markerTr, float outerR, float angle,
        DrumTrack drumRef, int absStep, int leadSteps,
        float travelDuration, System.Func<bool> shouldStop,
        System.Action onLaunch = null,
        System.Action onDepart = null,
        System.Func<bool> isCancelled = null)
    {
        if (drumRef != null)
        {
            // Recompute the trigger step against the LIVE leaderSteps on every check, not a
            // snapshot taken before this coroutine started. leaderSteps can change while this
            // wait is pending — most notably right around a bin completing, when a track's
            // committed width grows on demand (see InstrumentTrack.BinState.cs) — and a step
            // baked against the old domain may never recur, hanging this wait forever (see
            // DrumTrack.Transport.cs:311-317 for the same risk acknowledged elsewhere).
            bool stepFired = false;
            System.Action<int, int> onStep = (s, liveLeaderSteps) =>
            {
                if (liveLeaderSteps <= 0) return;
                int liveNoteLeaderStep = ((absStep % liveLeaderSteps) + liveLeaderSteps) % liveLeaderSteps;
                int liveTriggerStep    = (liveNoteLeaderStep - leadSteps + liveLeaderSteps) % liveLeaderSteps;
                if (s == liveTriggerStep) stepFired = true;
            };
            onStep(drumRef.currentStep, drumRef.GetLeaderSteps()); // in case we're already there
            if (!stepFired)
            {
                drumRef.OnStepChanged += onStep;
                yield return new WaitUntil(() => stepFired || shouldStop() || (isCancelled?.Invoke() ?? false));
                drumRef.OnStepChanged -= onStep;
            }
        }

        if (shouldStop() || (isCancelled?.Invoke() ?? false) || ringTransform == null) yield break;

        // The step has been reached and the dot is about to depart — the "snap" moment for
        // any preview tether tracking this note (see DeformBinRingCoroutine).
        onDepart?.Invoke();

        if (config?.launchSfx != null)
        {
            var cam = Camera.main;
            PlayOneShotRouted(
                config.launchSfx,
                cam != null ? cam.transform.position : Vector3.zero,
                config.launchSfxVolume);
        }

        Vector3 dotWorld = markerTr != null
            ? markerTr.position
            : ringTransform.TransformPoint(
                  new Vector3(Mathf.Cos(angle) * outerR * 1.5f, Mathf.Sin(angle) * outerR * 1.5f, 0f));

        // Ring surface position in local ring space — dot arrives here first, then pushes inward.
        var ringLocalPos = new Vector3(Mathf.Cos(angle) * outerR, Mathf.Sin(angle) * outerR, 0f);

        // onLaunch fires at impact (when the dot reaches the ring surface), not at departure.
        StartCoroutine(TravelNoteDot(dotWorld, ringTransform, ringLocalPos, tugLocal, travelDuration, dotColor, onLaunch));
    }

    private IEnumerator TweenContour(LineRenderer lr, List<Vector2> to, float duration,
        System.Action onComplete = null)
    {
        if (lr == null || to == null || lr.positionCount != to.Count) yield break;
        var from = new List<Vector2>(lr.positionCount);
        for (int i = 0; i < lr.positionCount; i++)
        {
            var p = lr.GetPosition(i);
            from.Add(new Vector2(p.x, p.y));
        }
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            for (int i = 0; i < from.Count; i++)
            {
                var pt = Vector2.Lerp(from[i], to[i], t);
                lr.SetPosition(i, new Vector3(pt.x, pt.y, 0f));
            }
            yield return null;
        }
        ApplyContourPoints(lr, to);
        onComplete?.Invoke();
    }

    private static void ApplyContourPoints(LineRenderer lr, List<Vector2> pts)
    {
        if (lr == null || pts == null) return;
        lr.positionCount = pts.Count;
        for (int i = 0; i < pts.Count; i++)
            lr.SetPosition(i, new Vector3(pts[i].x, pts[i].y, 0f));
    }

    private IEnumerator TravelNoteDot(
        Vector3 startWorld, Transform ringTransform,
        Vector3 ringLocalPos, Vector3 tugLocalPos,
        float duration, Color color,
        System.Action onImpact = null)
    {
        GameObject go;
        if (noteTravelDotPrefab != null)
        {
            go = Instantiate(noteTravelDotPrefab, startWorld, Quaternion.identity, transform);
            go.transform.localScale = Vector3.one * (config.noteDotRadius * 2f);
            var lr2 = go.GetComponentInChildren<LineRenderer>();
            if (lr2 != null) { lr2.startColor = lr2.endColor = color; }
            else
            {
                var sr = go.GetComponentInChildren<SpriteRenderer>();
                if (sr != null) sr.color = color;
            }
        }
        else
        {
            go = new GameObject("NoteTravelDot");
            go.transform.SetParent(transform, worldPositionStays: true);
            go.transform.position = startWorld;

            var lr = go.AddComponent<LineRenderer>();
            if (lineMaterial != null) lr.material = lineMaterial;
            lr.startColor        = lr.endColor = color;
            lr.widthMultiplier   = config.lineWidth * 2f;
            lr.useWorldSpace     = false;
            lr.loop              = false;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows    = false;
            // Above the backdrop discs/Fill/Contour — the traveling dot must stay visible as it
            // crosses the ring, not disappear behind the record's solid backing.
            lr.sortingOrder      = TravelDotSortingOrder;

            const int segs = 8;
            float dotR = config.noteDotRadius;
            lr.positionCount = segs + 1;
            for (int i = 0; i <= segs; i++)
            {
                float a = i / (float)segs * Mathf.PI * 2f;
                lr.SetPosition(i, new Vector3(Mathf.Cos(a) * dotR, Mathf.Sin(a) * dotR, 0f));
            }
        }

        // Phase 1 — approach: dot travels from the note marker to the ring surface.
        float elapsed = 0f;
        while (elapsed < duration)
        {
            if (ringTransform == null) { Destroy(go); yield break; }
            elapsed += Time.deltaTime;
            go.transform.position = Vector3.Lerp(
                startWorld,
                ringTransform.TransformPoint(ringLocalPos),
                Mathf.Clamp01(elapsed / duration));
            yield return null;
        }

        if (ringTransform == null) { Destroy(go); yield break; }

        // Impact: dot has reached the ring surface — start the note and contour deformation.
        if (config?.impactSfx != null)
        {
            var cam = Camera.main;
            PlayOneShotRouted(
                config.impactSfx,
                cam != null ? cam.transform.position : Vector3.zero,
                config.impactSfxVolume);
        }
        onImpact?.Invoke();

        // Phase 2 — push: dot moves inward from ring surface to tug point as the ring curves.
        elapsed = 0f;
        while (elapsed < duration)
        {
            if (ringTransform == null) { Destroy(go); yield break; }
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            go.transform.position = ringTransform.TransformPoint(
                Vector3.Lerp(ringLocalPos, tugLocalPos, t));
            yield return null;
        }

        Destroy(go);
    }
}
