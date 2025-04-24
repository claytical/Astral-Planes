using UnityEngine;
using Kino;
using System.Collections;

public class GlitchController : MonoBehaviour
{
    public AnalogGlitch analogGlitch;
    public DrumTrack drumTrack;

    [Header("Wildcard Glitch Settings")]
    public float wildcardScanLine = 0.3f;
    public float wildcardVerticalJump = 0.2f;
    public float wildcardHorizontalShake = 0.3f;
    public float wildcardColorDrift = 0.4f;

    [Header("Burst Glitch Settings")]
    public float burstScanLine = 0.8f;
    public float burstVerticalJump = 0.6f;
    public float burstHorizontalShake = 0.6f;
    public float burstColorDrift = 0.9f;
    public float burstDuration = 1.0f;

    private bool isBursting = false;
    private bool isWildcardActive = false;

    public static GlitchController Instance { get; private set; }

    void Awake()
    {
        Instance = this;
    }

    void Update()
    {
        if (analogGlitch == null || drumTrack == null)
            return;

        bool inWildcard = drumTrack.currentPhase == MusicalPhase.Wildcard;

        if (inWildcard && !isWildcardActive)
        {
            ApplyWildcardGlitch();
        }
        else if (!inWildcard && isWildcardActive)
        {
            ClearWildcardGlitch();
        }

        // Burst always overrides glitch visuals temporarily
        if (isBursting)
        {
            ApplyGlitch(burstScanLine, burstVerticalJump, burstHorizontalShake, burstColorDrift);
        }
        else if (isWildcardActive)
        {
            ApplyGlitch(wildcardScanLine, wildcardVerticalJump, wildcardHorizontalShake, wildcardColorDrift);
        }
        else
        {
            ApplyGlitch(0f, 0f, 0f, 0f);
        }
    }

    public void TriggerHitGlitch()
    {
        if (drumTrack != null && drumTrack.currentPhase == MusicalPhase.Wildcard)
        {
            TriggerBurst();
        }
    }

    public void TriggerBurst()
    {
        if (!isBursting)
            StartCoroutine(GlitchBurst());
    }

    private IEnumerator GlitchBurst()
    {
        isBursting = true;

        yield return new WaitForSeconds(burstDuration);

        isBursting = false;
    }

    private void ApplyWildcardGlitch()
    {
        isWildcardActive = true;
        Debug.Log("🔁 Wildcard glitch started");
    }

    private void ClearWildcardGlitch()
    {
        isWildcardActive = false;
        Debug.Log("✅ Wildcard glitch ended");
    }

    private void ApplyGlitch(float scanLine, float verticalJump, float horizontalShake, float colorDrift)
    {
        analogGlitch.scanLineJitter = scanLine;
        analogGlitch.verticalJump = verticalJump;
        analogGlitch.horizontalShake = horizontalShake;
        analogGlitch.colorDrift = colorDrift;
    }
}
