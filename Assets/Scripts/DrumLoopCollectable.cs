using System;
using System.Collections;
using UnityEngine;

public class DrumLoopCollectable : MonoBehaviour
{
    public enum EvolutionStage { Sparse, Basic, Flourish, Breakbeat }
    public EvolutionStage currentStage = EvolutionStage.Sparse;
    private int loopAge = 0; // Tracks number of drum loops since spawn
    public DrumTrack drumTrack;
    public AudioClip newDrumLoopClip;
    private bool collected = false;
    
    
    [Header("Pulse Settings")]
    public int pulseEveryNSteps = 4;  // Pulse on every 4th step, for example
    public float baseScaleMultiplier = 1.3f;  // How big the pulse gets
    public float pulseDuration = 0.15f;   // How quickly it scales in/out
    [Header("Visual Effects")]
    public ParticleSystem starBurstEffect; // ✅ Assign in Inspector
    public Color[] starColors; // ✅ Assign a gradient for progression (Star 1 → Star 4)
    private SpriteRenderer spriteRenderer;
    private int lastStep = -1;
    private bool isAnimating = false;
    private Vector3 originalScale;
    
    void Update()
    {
        if (collected && drumTrack != null)
        {
            int currentStep = drumTrack.GetCurrentStep();

            if (currentStep != lastStep && currentStep % pulseEveryNSteps == 0)
            {
                if (!isAnimating) StartCoroutine(DoPulse());
                lastStep = currentStep;
            }
        }
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
    public void SetDrumTrack(DrumTrack track)
    {
        drumTrack = track;
        UpdateStarAppearance();
    }
    public void UpdateStarAppearance()
    {
        int index = drumTrack.GetCollectedStarCount();
        if (spriteRenderer != null && starColors.Length > index)
        {
            spriteRenderer.color = starColors[index]; // ✅ Change color based on collected count
        }
    }

    public void Collect()
    {
        if (!collected)
        {
            collected = true;
            drumTrack.CollectedDrumLoop(this);
            StartCoroutine(PulseWithBeat());
            UpdateStarAppearance();
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
        yield return new WaitUntil(() => !starBurstEffect.isPlaying); // ✅ Wait for ParticleSystem to finish
        Debug.Log($"[DrumLoopCollectable] Destroying after particles: {gameObject.name}");

        if (drumTrack != null)
        {
            drumTrack.RemoveDrumLoopCollectable(this); // ✅ Ensure cleanup from DrumTrack
        }
        Destroy(gameObject);
    }


    public void Remove()
    {
        if (drumTrack != null)
        {
            Vector2Int gridPos = drumTrack.WorldToGridPosition(transform.position);
            drumTrack.RemoveObstacleAt(gridPos);
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

    private void OnTriggerEnter2D(Collider2D coll)
    {
        
        if (!collected)
        {
            Vehicle vehicle = coll.gameObject.GetComponent<Vehicle>();
            if (vehicle != null)
            {
                if (drumTrack != null && newDrumLoopClip != null)
                {
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

