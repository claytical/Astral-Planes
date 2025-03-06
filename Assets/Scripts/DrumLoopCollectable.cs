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
    public float scaleMultiplier = 1.3f;  // How big the pulse gets
    public float pulseDuration = 0.15f;   // How quickly it scales in/out

    private int lastStep = -1;
    private bool isAnimating = false;

    private Vector3 originalScale;
    
    void Update()
    {
        if (collected)
        {
            // Guard: if no DrumTrack is assigned, exit.
            if (!drumTrack) return;

            // Ask DrumTrack for the current step
            int currentStep = drumTrack.GetCurrentStep();

            // If the step changed since last frame, see if we should pulse
            if (currentStep != lastStep)
            {
                // For example: pulse on every 'pulseEveryNSteps' boundary
                if (currentStep % pulseEveryNSteps == 0)
                {
                    // If not already animating, start a new pulse
                    if (!isAnimating)
                        StartCoroutine(DoPulse());
                }

                lastStep = currentStep;
            }
        }
    }
    IEnumerator DoPulse()
    {
        isAnimating = true;
        float elapsed = 0f;
        Vector3 targetScale = originalScale * scaleMultiplier;

        // Scale up
        while (elapsed < pulseDuration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / pulseDuration;
            transform.localScale = Vector3.Lerp(originalScale, targetScale, t);
            yield return null;
        }

        // Scale back down
        elapsed = 0f;
        while (elapsed < pulseDuration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / pulseDuration;
            transform.localScale = Vector3.Lerp(targetScale, originalScale, t);
            yield return null;
        }

        transform.localScale = originalScale;
        isAnimating = false;
    }

    public void SetTrack(DrumTrack track)
    {
        originalScale = transform.localScale;
        drumTrack = track;
    }

    public void Remove()
    {
        Debug.Log(("Remove the drum loop collectable from the grid"));
        Vector2Int gridPos = drumTrack.WorldToGridPosition(transform.position);
        drumTrack.RemoveObstacleAt(gridPos);
        Explode explode = GetComponent<Explode>();
        explode.Permanent();

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
                    collected = true;
                    drumTrack.drumLoopCollectables.Add(this);
                }
            }
            
        }
    }


}
