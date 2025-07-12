// HexagonShield.cs

using System.Collections;
using UnityEngine;

public class HexagonShield : MonoBehaviour
{
    public enum HexagonShieldType { Friendly, Depleting }
    public HexagonShieldType shieldType = HexagonShieldType.Friendly;

    private SpriteRenderer sr;
    private Color depletingColor = new Color(1f, 0.2f, 0.2f, 0.2f);
    public SpriteRenderer baseSprite;
    public ParticleSystem particleSystem;
    //public SpriteRenderer halo;
    public float amplitude = 1f; // drift radius
    public float speed = 0.5f;     // speed of drift
    public Vector2 offset;
    private float alphaBreathingOffset = 0;
    private static readonly float ChainRadius = 1.5f;
    private DrumTrack drumTrack;
    private float originalAlpha;
    private Vector3 fullScale;
    private Vector3 velocity;
    private Vector3 baseScale;

    void Awake()
    {
        fullScale = transform.localScale; // What the final scale *should be*
        alphaBreathingOffset = Random.Range(0f, 1f);
    }

    private IEnumerator GrowIn()
    {
        float tGrow = 0f;
        float duration = 0.4f;
        Debug.Log($"{gameObject.name} started grow:" + Time.time);

        while (tGrow < duration)
        {
            tGrow += Time.deltaTime;
            float s = Mathf.SmoothStep(0f, 1f, tGrow / duration);
            transform.localScale = fullScale * s;
            yield return null;
        }
        transform.localScale = fullScale; // Ensure exact final size
    }


    public void Begin()
    {
        SetColorVariance();
        // ✅ Start alpha fade-in
        if (baseSprite != null)
        {
            Color c = baseSprite.color;
            originalAlpha = c.a;
            c.a = 0f;
            baseSprite.color = c;
        }
        transform.localScale = Vector3.zero;
        StartCoroutine(GrowIn());
        StartCoroutine(FadeInAlpha(targetAlpha: originalAlpha));

    }
    private IEnumerator FadeInAlpha(float targetAlpha)
    {
        if (baseSprite == null) yield break;
Debug.Log($"{gameObject.name} fade in alpha:" + Time.time);
        float duration = 0.5f;
        float t = 0f;

        Color color = baseSprite.color;
        float finalAlpha = originalAlpha;// store the intended final alpha
        color.a = 0f;
        baseSprite.color = color;

        while (t < duration)
        {
            t += Time.deltaTime;
            float a = Mathf.Lerp(0f, finalAlpha, t / duration);
            color.a = a;
            baseSprite.color = color;
            yield return null;
        }

        color.a = finalAlpha;
        baseSprite.color = color;
    }

    void SetColorVariance()
    {
        Color color = baseSprite.color;
        float variation = Random.Range(-0.02f, 0.02f);
        color.r += variation;
        color.g += variation;
        color.b += variation;
        baseSprite.color = color;

        // Optional: Apply glow material tweaks here
    }
    private void SetParticleColor(Color c)
    {
        if (particleSystem == null) return;

        var main = particleSystem.main;
        main.startColor = new ParticleSystem.MinMaxGradient(c);
    }

    public void TriggerRippleEffect()
    {
        foreach (HexagonShield hex in FindObjectsOfType<HexagonShield>())
        {
            float dist = Vector2.Distance(transform.position, hex.transform.position);
            float delay = dist * 0.05f;
        }
    }

    public void SetDrumTrack(DrumTrack track)
    {
        drumTrack = track;
    }

    public void ShiftToPhaseColor(MusicalPhaseProfile profile, float duration)
    {
        StartCoroutine(PhaseColorLerpRoutine(profile.visualColor, duration));
    }

    private IEnumerator PhaseColorLerpRoutine(Color phaseColor, float duration)
    {
        if (baseSprite == null) yield break;

        Color startColor = baseSprite.color;
        float t = 0f;

        while (t < duration)
        {
            t += Time.deltaTime;
            Color lerped = Color.Lerp(startColor, phaseColor, t / duration);
            baseSprite.color = lerped;
            SetParticleColor(lerped);
            yield return null;
        }

        baseSprite.color = phaseColor;
        SetParticleColor(phaseColor);

    }

    public void SetPhaseColor(MusicalPhase phase)
    {
        Color phaseColor = phase switch
        {
            MusicalPhase.Establish => new Color(0.2f, 1f, 1f, 0.25f),     // Cyan / Gentle
            MusicalPhase.Evolve    => new Color(0.4f, 0.8f, 1f, 0.25f),     // Blue / Reflective
            MusicalPhase.Intensify => new Color(1f, 0.3f, 0.3f, 0.3f),     // Red / Bold
            MusicalPhase.Release   => new Color(1f, 0.8f, 0.4f, 0.25f),     // Amber / Fading
            MusicalPhase.Wildcard  => new Color(1f, 1f, 1f, 0.3f),         // White shimmer
            MusicalPhase.Pop       => new Color(1f, 0.6f, 1f, 0.25f),       // Pinkish
            _                      => new Color(0.5f, 0.5f, 0.5f, 0.2f),   // Default gray
        };

        if (baseSprite != null)
        {
            if (shieldType == HexagonShieldType.Friendly)
            {
                baseSprite.color = phaseColor;
                SetParticleColor(phaseColor);
            }
            else
            {
                baseSprite.color = depletingColor;
            }
        }
    }

    public void BreakHexagon(SoundEffectMood mood)
    {
        CollectionSoundManager.Instance.PlayEffect((int)mood);
        Debug.Log($"{gameObject.name}: Breaking Hexagon, mood: " + mood);
        if (drumTrack != null)
        {
            Vector2Int gridPos = drumTrack.WorldToGridPosition(transform.position);
            drumTrack.FreeSpawnCell(gridPos.x, gridPos.y);
            drumTrack.hexMazeGenerator.RemoveHex(gridPos);
            StartCoroutine(TriggerDelayedRegrowth(gridPos));
        }
        else
        {
            Debug.LogWarning("⚠️ DrumTrack not set on HexagonShield!");
        }

        Explode explode = GetComponent<Explode>();
        if (explode != null)
        {
            explode.Permanent();
        }
        else
        {
            Destroy(gameObject);
        }
    }
    private IEnumerator TriggerDelayedRegrowth(Vector2Int gridPos)
    {
        yield return new WaitForSeconds(0.5f); // delay long enough for Explode effect
        drumTrack.hexMazeGenerator.TriggerRegrowth(gridPos, drumTrack.currentPhase);
    }


    private void OnCollisionEnter2D(Collision2D coll)
    {
        if (coll.gameObject.TryGetComponent<Vehicle>(out var vehicle))
        {
            Vector2 impactDir = coll.relativeVelocity.normalized;
            AbsorbDiamondGhost(impactDir);
            if (shieldType == HexagonShieldType.Depleting)
            {
                vehicle.ConsumeEnergy(1f); // Optional energy penalty
            }
        }
    }

    public void SwitchType(HexagonShieldType newType)
    {
        switch (newType)
        {
            case HexagonShieldType.Friendly:
                shieldType = HexagonShieldType.Friendly;
                baseSprite.color = new Color(1f, 1f, 1f, 0.15f);
                break;
           case HexagonShieldType.Depleting:
                baseSprite.color = depletingColor;
                shieldType = HexagonShieldType.Depleting;
                break;
        }
    }

    public void AbsorbDiamondGhost(Vector2 impactDirection)
    {
        Vector2 worldImpactPoint = transform.position + (Vector3)impactDirection;
        Vector2Int center = drumTrack.WorldToGridPosition(worldImpactPoint);
        if (drumTrack.hexMazeGenerator != null)
        {
            if (CollectionSoundManager.Instance != null)
            {
                StartCoroutine(drumTrack.hexMazeGenerator.BreakSelfThenNeighbors(this, SoundEffectMood.Friendly, center, 2, .2f));
            }
            else
            {
                StartCoroutine(drumTrack.hexMazeGenerator.BreakSelfThenNeighbors(this, SoundEffectMood.Friendly, center, 2, .2f));
            }
        }
        else
        {
            Debug.Log($"Can't break...");
        }

        TriggerRippleEffect();
    }


}