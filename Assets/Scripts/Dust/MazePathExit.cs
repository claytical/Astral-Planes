using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One tunnel mouth in a path-choice interstitial maze. Passive data holder + trigger
/// detector; PathChoiceCoordinator builds/positions the GameObject and collider, then calls
/// Configure(). Firing OnTriggerEnter2D just reports back to the owner — winner selection,
/// sealing, and redirect all live in PathChoiceCoordinator.
/// </summary>
public class MazePathExit : MonoBehaviour
{
    public Vector2Int AnchorCell { get; private set; }
    public IReadOnlyList<Vector2Int> GapCells { get; private set; }
    public BoundaryWrap.BoundarySide Side { get; private set; }
    public int TargetPhaseIndex { get; private set; }
    public int TargetMotifIndex { get; private set; }

    private PathChoiceCoordinator _owner;
    private Collider2D _col;
    private GameObject _visualInstance;
    private float _sealFadeSeconds;
    private Coroutine _sealFadeRoutine;

    public void Configure(
        PathChoiceCoordinator owner,
        Vector2Int anchorCell,
        List<Vector2Int> gapCells,
        BoundaryWrap.BoundarySide side,
        int targetPhaseIndex,
        int targetMotifIndex,
        GameObject visualInstance = null,
        float sealFadeSeconds = 0.75f)
    {
        _owner = owner;
        AnchorCell = anchorCell;
        GapCells = gapCells;
        Side = side;
        TargetPhaseIndex = targetPhaseIndex;
        TargetMotifIndex = targetMotifIndex;
        _col = GetComponent<Collider2D>();
        _visualInstance = visualInstance;
        _sealFadeSeconds = sealFadeSeconds;
    }

    public void SetSealed(bool sealedShut)
    {
        if (_col != null) _col.enabled = !sealedShut;
        if (_visualInstance == null) return;

        if (sealedShut)
        {
            // Fade the losing tunnel's marker out instead of cutting it on the same frame it
            // seals, so a losing exit doesn't visibly pop off mid-effect while
            // PathChoiceCoordinator's carry-out sequence still has seconds left to run.
            Debug.Log($"[PathExitFade] SetSealed(true) on {name}: visual={_visualInstance.name}, fadeSeconds={_sealFadeSeconds}, alreadyFading={_sealFadeRoutine != null}");
            if (_sealFadeRoutine == null)
                _sealFadeRoutine = StartCoroutine(SealFadeRoutine());
        }
        else
        {
            if (_sealFadeRoutine != null)
            {
                StopCoroutine(_sealFadeRoutine);
                _sealFadeRoutine = null;
            }
            _visualInstance.SetActive(true);
        }
    }

    // Stops emission immediately, then fades every already-live particle to nothing over
    // _sealFadeSeconds by shrinking its size to 0 (a render-level effect every particle shader
    // respects, unlike vertex-color alpha, which depends on the material) while also fading its
    // alpha, plus collapsing any Trails module's ribbon width to 0 (trails don't inherit size
    // from their particle, so the per-particle shrink alone doesn't touch them). randomSeed is
    // used as a stable per-particle key across frames since GetParticles' array order isn't
    // guaranteed to stay stable as particles die.
    private IEnumerator SealFadeRoutine()
    {
        var particleSystems = _visualInstance.GetComponentsInChildren<ParticleSystem>(true);
        foreach (var ps in particleSystems)
            ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);

        // Trail ribbons don't take their width from Particle.startSize — that's a separate,
        // module-level curve — so a "flowing streamer" style effect (Trails enabled, which this
        // prefab uses) would keep rendering at full width from the per-particle fade above alone.
        // Collapsing widthOverTrail to a shrinking constant fades the ribbon itself in lockstep.
        var trailsEnabled = new bool[particleSystems.Length];
        for (int i = 0; i < particleSystems.Length; i++)
            trailsEnabled[i] = particleSystems[i].trails.enabled;

        var buffers = new ParticleSystem.Particle[particleSystems.Length][];
        var baseAlpha = new Dictionary<uint, float>[particleSystems.Length];
        var baseSize = new Dictionary<uint, Vector3>[particleSystems.Length];
        var use3DSize = new bool[particleSystems.Length];
        int totalSnapshotted = 0;
        for (int i = 0; i < particleSystems.Length; i++)
        {
            use3DSize[i] = particleSystems[i].main.startSize3D;
            buffers[i] = new ParticleSystem.Particle[Mathf.Max(1, particleSystems[i].particleCount)];
            baseAlpha[i] = new Dictionary<uint, float>();
            baseSize[i] = new Dictionary<uint, Vector3>();
            int count = particleSystems[i].GetParticles(buffers[i]);
            totalSnapshotted += count;
            for (int p = 0; p < count; p++)
            {
                var particle = buffers[i][p];
                baseAlpha[i][particle.randomSeed] = particle.startColor.a / 255f;
                baseSize[i][particle.randomSeed] = use3DSize[i] ? particle.startSize3D : Vector3.one * particle.startSize;
            }
        }
        Debug.Log($"[PathExitFade] {name}: {particleSystems.Length} ParticleSystem(s) found, {totalSnapshotted} live particle(s) snapshotted, use3DSize={string.Join(",", use3DSize)}");

        float duration = Mathf.Max(0.01f, _sealFadeSeconds);
        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            float keep = 1f - Mathf.SmoothStep(0f, 1f, t / duration);

            for (int i = 0; i < particleSystems.Length; i++)
            {
                var ps = particleSystems[i];
                if (ps == null) continue;

                if (trailsEnabled[i])
                {
                    var trails = ps.trails;
                    trails.widthOverTrail = new ParticleSystem.MinMaxCurve(keep);
                }

                int count = ps.GetParticles(buffers[i]);
                for (int p = 0; p < count; p++)
                {
                    var particle = buffers[i][p];

                    float ba = baseAlpha[i].TryGetValue(particle.randomSeed, out var a) ? a : 1f;
                    var c = particle.startColor;
                    c.a = (byte)Mathf.RoundToInt(255f * ba * keep);
                    particle.startColor = c;

                    Vector3 bs = baseSize[i].TryGetValue(particle.randomSeed, out var s) ? s : Vector3.one;
                    if (use3DSize[i])
                        particle.startSize3D = bs * keep;
                    else
                        particle.startSize = bs.x * keep;

                    buffers[i][p] = particle;
                }
                ps.SetParticles(buffers[i], count);
            }
            yield return null;
        }

        Debug.Log($"[PathExitFade] {name}: fade complete after {t:F2}s, deactivating visual");
        if (_visualInstance != null) _visualInstance.SetActive(false);
        _sealFadeRoutine = null;
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        var rb = other.attachedRigidbody;
        if (rb == null) return;

        var vehicle = rb.GetComponentInParent<Vehicle>();
        if (vehicle != null) _owner?.OnVehicleReachedExit(this, vehicle);
    }
}
