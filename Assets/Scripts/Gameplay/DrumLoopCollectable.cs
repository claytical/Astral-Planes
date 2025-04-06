using System;
using System.Collections;
using UnityEngine;

public class DrumLoopCollectable : MonoBehaviour
{
    public DrumLoopPattern pattern;
    public float timeToLive = 6f;
    private int loopAge = 0; // Tracks number of drum loops since spawn
    private DrumTrack drumTrack;
    private AudioClip newDrumLoopClip;
    private bool collected = false;
    private InstrumentTrack track;
    private float spawnTime;
    private bool expired = false;
    
    [Header("Pulse Settings")]
    public int pulseEveryNSteps = 4;  // Pulse on every 4th step, for example
    public float baseScaleMultiplier = 1.3f;  // How big the pulse gets
    public float pulseDuration = 0.15f;   // How quickly it scales in/out
    public float pulseSpeed = 1.5f;
    public float maxPulseScale = 1.2f;

    [Header("Star Layers")]
    public SpriteRenderer baseRenderer;    // Outer diamond
    public SpriteRenderer overlayRenderer; // Inner highlight
    [Header("Visual Effects")]
    public ParticleSystem starBurstEffect; // ✅ Assign in Inspector
    private int lastStep = -1;
    private bool isAnimating = false;
    private Vector3 originalScale;
    private float pulseTimer = 0f;
    void Start()
    {
        spawnTime = Time.time;
    }
    
    void HandleExpiration()
    {
        if (Time.time - spawnTime > timeToLive && !collected)
        {
            Debug.Log($"[DrumLoopCollectable] Expired: {gameObject.name}");
            collected = true; // So it doesn't trigger twice
            if (drumTrack != null)
            {
                drumTrack.NotifyStarExpired(this);
                drumTrack.RemoveDrumLoopCollectable(this);                
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
    }
    void Update()
    {
        pulseTimer += Time.deltaTime * pulseSpeed;
        float scale = 1f + Mathf.Sin(pulseTimer) * 0.1f * maxPulseScale;
        transform.localScale = new Vector3(scale, scale, 1f);

        HandleExpiration();
    }
    
    IEnumerator DoPulse()
    {
        isAnimating = true;
        float elapsed = 0f;
        float intensity = drumTrack.GetStarCollectionRatio(); // ✅ Scales pulse dynamically
        Vector3 targetScale = originalScale * Mathf.Lerp(baseScaleMultiplier, 1.8f, intensity);

        while (elapsed < pulseDuration)
        {
            elapsed += Time.deltaTime;
            transform.localScale = Vector3.Lerp(originalScale, targetScale, elapsed / pulseDuration);
            yield return null;
        }

        elapsed = 0f;
        while (elapsed < pulseDuration)
        {
            elapsed += Time.deltaTime;
            transform.localScale = Vector3.Lerp(targetScale, originalScale, elapsed / pulseDuration);
            yield return null;
        }

        transform.localScale = originalScale;
        isAnimating = false;
    }
    public void SetDrums(DrumTrack drums)
    {
        drumTrack = drums;
        var visual = drumTrack.GetVisualForPattern(pattern);
        ApplyVisual(visual);
    }

    void OnDestroy()
    {
        if (drumTrack != null)
        {
            if (drumTrack.GetCollectedStarCount() > 0 || drumTrack.GetActiveStarCount() > 0)
            {
                drumTrack.RemoveDrumLoopCollectable(this);
            }
        }

    }

    public void ApplyVisual(DrumLoopPatternVisual visual)
    {
        if (visual == null)
        {
            Debug.LogWarning($"⚠️ No visual provided for pattern {pattern}");
            return;
        }

        if (baseRenderer != null)
            baseRenderer.color = visual.color;

        if (overlayRenderer != null)
            overlayRenderer.color = visual.color;

        if (visual.particleEffectPrefab != null)
        {
            Instantiate(visual.particleEffectPrefab, transform.position, Quaternion.identity, transform);
        }

        pulseSpeed = visual.pulseSpeed;
        maxPulseScale = visual.pulseScale;
    }



    private void Collect()
    {
        if (!collected)
        {
            collected = true;
            drumTrack.CollectedDrumLoop(this);
            StartCoroutine(PulseWithBeat());
        }
    }
    private IEnumerator PulseWithBeat()
    {
        while (true)
        {
            float pulseSize = Mathf.Lerp(1f, 1.5f, Mathf.PingPong(Time.time * 2f, 1));
            transform.localScale = Vector3.one * pulseSize;
            yield return null;
        }
    }
    
    
    public void TriggerFinalBurst()
    {
        if (starBurstEffect != null)
        {
            starBurstEffect.Play();
            StartCoroutine(DestroyAfterParticles()); // ✅ Wait for the effect before destruction
        }
        else
        {
            Destroy(gameObject); // ✅ If no particles exist, destroy immediately
        }
    }

    private IEnumerator DestroyAfterParticles()
    {
        Debug.Log($"[DrumLoopCollectable] Waiting for particles to finish: {gameObject.name}");

        float timeout = 2f;
        float elapsed = 0f;

        while (starBurstEffect != null && starBurstEffect.isPlaying && elapsed < timeout)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        if (drumTrack != null)
        {
            drumTrack.RemoveDrumLoopCollectable(this);
        }

        Debug.Log($"[DrumLoopCollectable] Destroying after particles: {gameObject.name}");
        Destroy(gameObject);
    }


    private void OnTriggerEnter2D(Collider2D coll)
    {
        Debug.Log($"[DrumLoopCollectable] Triggered: {gameObject.name}");
        if (!collected)
        {
            Vehicle vehicle = coll.gameObject.GetComponent<Vehicle>();
            if (vehicle != null)
            {
                if (drumTrack != null)
                {
                    Debug.Log($"[DrumLoopCollectable] Collected vehicle: {gameObject.name}");
                    Collect(); 
                }
            }
            
        }
    }

    public void MoveToCorner(Vector3 cornerPosition)
    {
        // Turn off normal physics
        Rigidbody2D rb = GetComponent<Rigidbody2D>();
        if (rb != null)
        {
            rb.isKinematic = true;
            rb.linearVelocity = Vector2.zero;
            rb.angularVelocity = 0f;
            rb.Sleep();
        }

        // Option A: Instantly teleport the collectable
        //transform.position = cornerPosition;
    
        // Option B: Smoothly tween to the corner
         StartCoroutine(MoveSmoothly(cornerPosition, 1.0f));
    }

    private IEnumerator MoveSmoothly(Vector3 targetPos, float duration)
    {
        Vector3 start = transform.position;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            transform.position = Vector3.Lerp(start, targetPos, t);
            yield return null;
        }
        transform.position = targetPos;
    }

}

