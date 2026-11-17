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
        RefreshLegibleRestTint();
        float target = on ? Mathf.Max(1f, sizeMul) : 1f;
        if (_blockFormRoutine != null) StopCoroutine(_blockFormRoutine);
        if (!isActiveAndEnabled) { ApplyBlockSizeMul(target); return; }
        _blockFormRoutine = StartCoroutine(BlockFormRoutine(target));
    }

    private IEnumerator BlockFormRoutine(float target)
    {
        float from = _blockSizeMul;
        float t = 0f;
        while (t < kBlockFormLerpSeconds)
        {
            t += Time.deltaTime;
            ApplyBlockSizeMul(Mathf.Lerp(from, target, Mathf.SmoothStep(0f, 1f, t / kBlockFormLerpSeconds)));
            yield return null;
        }
        ApplyBlockSizeMul(target);
        _blockFormRoutine = null;
    }

    // Rewrites startSize (future emissions) and rescales live particles by the ratio, so the
    // swell reads immediately instead of waiting a particle lifetime.
    private void ApplyBlockSizeMul(float mul)
    {
        mul = Mathf.Max(0.0001f, mul);
        float ratio = mul / Mathf.Max(0.0001f, _blockSizeMul);
        _blockSizeMul = mul;

        var ps = visual.particleSystem;
        if (ps == null || Mathf.Approximately(ratio, 1f)) return;

        var main = ps.main;
        main.startSize = Mathf.Max(0.0001f, main.startSize.constant * ratio);

        int count = ps.particleCount;
        if (count <= 0) return;
        if (_blockFormParticleBuffer == null || _blockFormParticleBuffer.Length < count)
            _blockFormParticleBuffer = new ParticleSystem.Particle[Mathf.Max(count, 16)];
        int n = ps.GetParticles(_blockFormParticleBuffer);
        for (int i = 0; i < n; i++)
            _blockFormParticleBuffer[i].startSize *= ratio;
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
