using System.Collections;
using UnityEngine;

// Cosmic Block form (role-wide blocks, CosmicDustGenerator.Blocks.cs). A blocked cell is this same
// dust cell transformed in place: its particles swell to a much larger start size (the old Cosmic
// Block prefab authored startSize 2 vs Cosmic Potential's 0.5 → ×4 by default), reading as a dense
// wall. Indestructibility lives on the generator (CellFlags.RoleBlocked); this partial is the look
// plus "never a star target".
//
// The multiplier is layered on top of the cell-relative base size (ApplyCellRelativeParticleSizing)
// and survives CaptureBaseVisual / ResetVisualToBase, which divide/multiply it back out.
public partial class CosmicDust
{
    private const float kBlockFormLerpSeconds = 0.4f;
    // A ring completion converts a whole role at once (hundreds of cells). Each cell starts its
    // swell after a random delay in [0, this] so the work, and the look, ripples in over a few
    // frames instead of landing in one. Gameplay state (IsBlockForm, flags, collision) is
    // immediate; only the visual is staggered.
    private const float kBlockFormMaxStaggerSeconds = 0.3f;

    private float _blockSizeMul = 1f;            // currently applied multiplier
    private Coroutine _blockFormRoutine;
    private ParticleSystem.Particle[] _blockFormParticleBuffer;

    public bool IsBlockForm { get; private set; }

    public void SetBlockForm(bool on, float sizeMul = 4f)
    {
        IsBlockForm = on;
        // A block shows no flower bud and never reads as bright star food. The bloom itself is
        // kept (HasBloomedFlower), so a cell that reverts to dust gets its earned bud back.
        if (flowerRoot != null) flowerRoot.SetActive(!on && HasBloomedFlower);
        float target = on ? Mathf.Max(1f, sizeMul) : 1f;
        if (_blockFormRoutine != null) { StopCoroutine(_blockFormRoutine); _blockFormRoutine = null; }
        if (!isActiveAndEnabled)
        {
            RefreshLegibleRestTint();
            ApplyBlockSizeMul(target);
            return;
        }
        _blockFormRoutine = StartCoroutine(BlockFormRoutine(target, Random.Range(0f, kBlockFormMaxStaggerSeconds)));
    }

    private IEnumerator BlockFormRoutine(float target, float delay)
    {
        float until = Time.time + delay;              // no WaitForSeconds alloc per cell
        while (Time.time < until) yield return null;

        // The rest-tint refresh is a full particle rewrite — deferred to here so it staggers too.
        RefreshLegibleRestTint();

        float from = _blockSizeMul;
        float t = 0f;
        while (t < kBlockFormLerpSeconds)
        {
            t += Time.deltaTime;
            ApplyBlockSizeMul(Mathf.Lerp(from, target, Mathf.SmoothStep(0f, 1f, t / kBlockFormLerpSeconds)), budgeted: true);
            yield return null;
        }
        ApplyBlockSizeMul(target);
        _blockFormRoutine = null;
    }

    // Rewrites startSize (future emissions) and resizes live particles, so the swell reads
    // immediately instead of waiting a particle lifetime. Budgeted calls (intermediate lerp
    // frames) may skip the live rewrite; with constant start size the live write is absolute,
    // so a skipped frame loses nothing and the next allowed write (or the final one) catches up.
    private void ApplyBlockSizeMul(float mul, bool budgeted = false)
    {
        mul = Mathf.Max(0.0001f, mul);
        float ratio = mul / Mathf.Max(0.0001f, _blockSizeMul);
        _blockSizeMul = mul;

        var ps = visual.particleSystem;
        if (ps == null || Mathf.Approximately(ratio, 1f)) return;

        var main = ps.main;
        bool constantSize = main.startSize.mode == ParticleSystemCurveMode.Constant;
        float newStart = Mathf.Max(0.0001f, main.startSize.constant * ratio);
        main.startSize = newStart;

        int count = ps.particleCount;
        if (count <= 0) return;
        // Non-constant start size can only be updated relatively, so it can't skip frames.
        if (constantSize && !AllowLiveRewrite(budgeted)) return;
        if (_blockFormParticleBuffer == null || _blockFormParticleBuffer.Length < count)
            _blockFormParticleBuffer = new ParticleSystem.Particle[Mathf.Max(count, 16)];
        int n = ps.GetParticles(_blockFormParticleBuffer);
        for (int i = 0; i < n; i++)
        {
            if (constantSize) _blockFormParticleBuffer[i].startSize = newStart;
            else _blockFormParticleBuffer[i].startSize *= ratio;
        }
        ps.SetParticles(_blockFormParticleBuffer, n);
    }

    // Snap back to normal form with no animation (pool reuse / teardown).
    private void ResetBlockFormImmediate()
    {
        if (_blockFormRoutine != null) { StopCoroutine(_blockFormRoutine); _blockFormRoutine = null; }
        IsBlockForm = false;
        ApplyBlockSizeMul(1f);
    }
}
