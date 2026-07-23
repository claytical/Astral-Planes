using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// ── Low-level draw-in / travel-dot animation coroutines ─────────────────
public partial class MotifRingGlyphApplicator
{
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
        int leaderSteps = drumRef != null ? drumRef.GetLeaderSteps() : binSteps;

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

            int noteLeaderStep = info.AbsStep % leaderSteps;
            int triggerStep    = (noteLeaderStep - leadSteps + leaderSteps) % leaderSteps;

            Transform markerTr = null;
            if (noteViz?.noteMarkers != null && info.Track != null)
                noteViz.noteMarkers.TryGetValue((info.Track, info.AbsStep), out markerTr);

            var capturedInfo = info;
            StartCoroutine(WaitAndLaunchDot(
                ringTransform, info.TugLocalPos, info.DotColor,
                markerTr, outerR, info.NoteAngle,
                drumRef, triggerStep, config.noteTravelDuration, shouldStop,
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
        DrumTrack drumRef, int triggerStep,
        float travelDuration, System.Func<bool> shouldStop,
        System.Action onLaunch = null)
    {
        if (drumRef != null && drumRef.currentStep != triggerStep)
        {
            bool stepFired = false;
            System.Action<int, int> onStep = (s, _) => { if (s == triggerStep) stepFired = true; };
            drumRef.OnStepChanged += onStep;
            yield return new WaitUntil(() => stepFired || shouldStop());
            drumRef.OnStepChanged -= onStep;
        }

        if (shouldStop() || ringTransform == null) yield break;

        if (config?.launchSfx != null)
        {
            var cam = Camera.main;
            AudioSource.PlayClipAtPoint(
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
            AudioSource.PlayClipAtPoint(
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
