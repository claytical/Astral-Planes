using System.Collections;
using UnityEngine;

public class VisualNoteMarker : MonoBehaviour
{
    private SpriteRenderer _spriteRenderer;
    public ParticleSystem capturedParticles;
    public ParticleSystem preCaptureParticles;
    public bool IsLit { get; set; }

    void Awake()
    {
        if (_spriteRenderer == null)
            _spriteRenderer = GetComponentInChildren<SpriteRenderer>();
    }
    public void ApplyLitColor(Color color)
    {
        IsLit = true;

        // sprite
        if (_spriteRenderer == null) _spriteRenderer = GetComponentInChildren<SpriteRenderer>();
        if (_spriteRenderer != null)
        {
            var c = color;
            c.a = _spriteRenderer.color.a; // preserve current alpha choice if you want
            _spriteRenderer.color = c;
        }

        // particles: main.startColor is NOT enough if ColorOverLifetime is enabled
        ApplyParticleColor(capturedParticles, color);
        ApplyParticleColor(preCaptureParticles, color);
    }

    private static void ApplyParticleColor(ParticleSystem ps, Color color)
    {
        if (ps == null) return;

        var main = ps.main;
        main.startColor = color;

        var col = ps.colorOverLifetime;
        if (col.enabled)
            col.color = new ParticleSystem.MinMaxGradient(color);

        var trails = ps.trails;
        if (trails.enabled)
            trails.colorOverLifetime = new ParticleSystem.MinMaxGradient(color);

        // If it’s already playing, this updates newly emitted particles.
        // (Existing particles may keep their old color depending on modules/shaders.)
        if (!ps.isPlaying) ps.Play();
    }
    public void SetWaitingParticles(Color color)
    {
        if (preCaptureParticles != null)
        {
            var pre = preCaptureParticles.main;
            pre.startColor = color;

            // If your prefab uses ColorOverLifetime, it can override startColor.
            // We make sure it's not forcing gray.
            var col = preCaptureParticles.colorOverLifetime;
            if (col.enabled) col.enabled = false;
        }
    }

    /// <summary>
    /// Called when the collectable finishes travel and the marker should become "captured".
    /// This must update BOTH the sprite and particle colors (not just MarkerLight).
    /// </summary>
    public void LightUp(Color color)
    {
        IsLit = true;

        // Sprite
        if (_spriteRenderer == null)
            _spriteRenderer = GetComponentInChildren<SpriteRenderer>();
        if (_spriteRenderer != null)
        {
            var c = color;
            // keep whatever alpha you want for markers; your Initialize() used ~0.5
            c.a = Mathf.Max(_spriteRenderer.color.a, 0.5f);
            _spriteRenderer.color = c;
        }

        // Pre-capture particles: stop or recolor (your choice)
        if (preCaptureParticles != null)
        {
            var preMain = preCaptureParticles.main;
            preMain.startColor = color;

            var preCol = preCaptureParticles.colorOverLifetime;
            if (preCol.enabled) preCol.enabled = false;

            // Usually once captured, you stop the "waiting" effect
            preCaptureParticles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }

        // Captured particles: recolor + ensure playing
        if (capturedParticles != null)
        {
            var capMain = capturedParticles.main;
            capMain.startColor = color;

            var capCol = capturedParticles.colorOverLifetime;
            if (capCol.enabled) capCol.enabled = false;

            if (!capturedParticles.isPlaying)
                capturedParticles.Play(true);
        }
    }

    public void Initialize(Color color)
    {
        if (_spriteRenderer == null)
            _spriteRenderer = GetComponentInChildren<SpriteRenderer>();

        if (_spriteRenderer != null)
            _spriteRenderer.color = color;

        if (capturedParticles != null)
        {
            var main = capturedParticles.main;
            main.startColor = color;

            var col = capturedParticles.colorOverLifetime;
            if (col.enabled) col.enabled = false;
        }

        if (preCaptureParticles != null)
        {
            var pre = preCaptureParticles.main;
            pre.startColor = color;

            var col = preCaptureParticles.colorOverLifetime;
            if (col.enabled) col.enabled = false;

            preCaptureParticles.Play();
        }

        StartCoroutine(FadeAndScaleIn(color));
    }

    private IEnumerator FadeAndScaleIn(Color targetColor)
    {
        if (_spriteRenderer == null) yield break;

        float duration = 2f;
        float elapsed = 0f;

        Color startColor = new Color(targetColor.r, targetColor.g, targetColor.b, 0f);
        targetColor.a = .5f;
        _spriteRenderer.color = startColor;

        Vector3 originalScale = transform.localScale;
        transform.localScale = Vector3.zero;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / duration;

            _spriteRenderer.color = Color.Lerp(startColor, targetColor, t);
            transform.localScale = Vector3.Slerp(Vector3.zero, originalScale, Mathf.SmoothStep(0f, 1f, t));
            yield return null;
        }

        _spriteRenderer.color = targetColor;
        transform.localScale = originalScale;

        if (capturedParticles != null)
        {
            // Don't re-gray anything here; keep the current color (it may be lit already).
            capturedParticles.Play();
        }
    }
}