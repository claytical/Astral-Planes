using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
public enum NoteCollectionMode {
    TimedPuzzle,
    FreePlay
}

public class Collectable : MonoBehaviour
{
    public InstrumentTrack assignedInstrumentTrack; // 🎼 The track that spawned this collectable
    public SpriteRenderer energySprite;
    private int amount = 1;
    private int noteDurationTicks = 4; // 🎵 Default to 1/16th note duration
    private int assignedNote; // 🎵 The MIDI note value
    private Vector3 startPosition;
    public delegate void OnCollectedHandler(int duration, float force);
    public event OnCollectedHandler OnCollected;
    public event Action OnDestroyed;
    public List<int> sharedTargetSteps = new List<int>();
    private Vector3 originalScale;
    private bool isInitialized = false;
    private bool reachedDestination = false;

    void Start()
    {
        originalScale = transform.localScale;
        isInitialized = true;

        if (energySprite != null)
        {
            var c = energySprite.color;
            c.a = 0f;
            energySprite.color = c;
        }
    }

    
    // 🔹 Initializes the collectable with its note data
    public void Initialize(int note, int duration, InstrumentTrack track, NoteSet noteSet, List<int> steps)
    {
        assignedNote = note;
        noteDurationTicks = duration;
        assignedInstrumentTrack = track;
        sharedTargetSteps = steps;
        // ✅ Choose one target step — e.g. round robin or random
        if (steps is { Count: > 0 })
        {
            double now = AudioSettings.dspTime;
        }
        else
        {
            Debug.LogWarning($"{gameObject.name} - No target steps provided.");
        }
        CollectableParticles particleScript = GetComponent<CollectableParticles>();
        if (particleScript != null && noteSet != null)
        {
            particleScript.Configure(noteSet);
        }
        energySprite.color = track.trackColor;
        if (assignedInstrumentTrack == null)
        {
            Debug.LogError($"Collectable {gameObject.name} - assignedInstrumentTrack is NULL on initialization!");
        }
        var explode = GetComponent<Explode>();
        if (explode != null)
        {
            explode.ApplyLifetimeProfile(LifetimeProfile.GetProfile(MinedObjectType.Note));
        }        
    }

    public int GetNote()
    {
        return assignedNote;
    }
    private void OnFailedCollect()
    {
        if (energySprite != null)
        {
            StartCoroutine(FlashRedThenDie());
        }

        // ⚡ Zap the player
        if (TryGetComponent(out Collider2D col))
            col.enabled = false;

        OnDestroyed?.Invoke(); // still notify listeners

        // Optional: particle zap
        if (TryGetComponent(out CollectableParticles particles))
            particles.EmitZap(); // you'll define this next

        Destroy(gameObject, 0.3f); // delay to show flash
    }
    private IEnumerator FlashRedThenDie()
    {
        Color original = energySprite.color;
        energySprite.color = Color.red;
        yield return new WaitForSeconds(0.1f);
        energySprite.color = original;
    }

private void OnTriggerEnter2D(Collider2D coll)
{
    Debug.Log($"🧲 Collectable hit by {coll.gameObject.name}");
    
    // only collect if we’re currently in the timing window
    if (!TryGetComponent<GhostNoteVine>(out var vine) || !vine.IsHighlighted)
    {

        var explodeWrong = GetComponent<Explode>();
        OnDestroyed?.Invoke();
        if (explodeWrong != null) explodeWrong.Permanent();
        else              Destroy(gameObject);
        return;
    }
        // destroy logic…

    var vehicle = coll.GetComponent<Vehicle>();
    if (vehicle == null) return;

    // 1) Gather timing info in doubles
    double dspNow       = AudioSettings.dspTime;
    var    drumTrack    = assignedInstrumentTrack.drumTrack;
    double loopLength   = drumTrack.GetLoopLengthInSeconds();
    double stepDuration = loopLength / drumTrack.totalSteps;

    // timingWindowSteps = "how many steps of tolerance" (e.g. 1 means ±½ a step)
    double timingWindow = stepDuration * drumTrack.timingWindowSteps * 0.5;

    double loopStart      = drumTrack.startDspTime;
    double sinceStart     = dspNow - loopStart;
    // floorDiv for negative values too, but dspNow should always >= start
    double loopsCompleted = Math.Floor(sinceStart / loopLength);

    // where this particular loop began (absolute DSP time)
    double currentLoopStart = loopStart + loopsCompleted * loopLength;

    // 2) fold current DSP into [0, loopLength)
    double tPos = (dspNow - currentLoopStart) % loopLength;
    if (tPos < 0) tPos += loopLength;

    // 3) find the best‐match step
    int    matchedStep = -1;
    double bestError   = timingWindow; // seed to your ±window
    foreach (int step in sharedTargetSteps)
    {
        // ideal time of that step *within the loop*
        double stepPos = (step * stepDuration) % loopLength;

        // raw distance
        double delta = Math.Abs(stepPos - tPos);
        // choose the shorter way around
        if (delta > loopLength * 0.5)
            delta = loopLength - delta;

        // only accept if *strictly closer* and *within* your ±window
        if (delta < bestError)
        {
            bestError   = delta;
            matchedStep = step;
        }
    }

    if (matchedStep >= 0)
    {
        // we're within ±timingWindow of that step!
        float force = vehicle.GetForceAsMidiVelocity();
        assignedInstrumentTrack.OnCollectableCollected(
            this, matchedStep, noteDurationTicks, force
        );
        OnCollected?.Invoke(noteDurationTicks, force);
        Debug.Log($"✅ On time! step={matchedStep}, error={bestError:F4}s");
        var explode = GetComponent<Explode>();
        OnDestroyed?.Invoke();
        if (explode != null) explode.Permanent();
        else              Destroy(gameObject);
    }
    else
    {
        OnFailedCollect();
        Debug.Log($"❌ Missed! no step within ±{timingWindow:F4}s of {tPos:F4}s");
    }

    // destroy logic…
}

    public void DriftToTarget(Vector3 from, Vector3 to, float duration)
    {
        StartCoroutine(DriftRoutine(from, to, duration));
    }
    private IEnumerator DriftRoutine(Vector3 from, Vector3 to, float duration)
    {
        Collider2D col = GetComponent<Collider2D>();
        if (col != null) col.enabled = false;

        transform.position = from;
        float elapsed = 0f;
        while (elapsed < duration)
        {
            float t = elapsed / duration;
            transform.position = Vector3.Lerp(from, to, t);
            elapsed += Time.deltaTime;
            yield return null;
        }

        transform.position = to;
        if (col != null) col.enabled = true;
        
        if (TryGetComponent(out GhostNoteVine vine))
        {
            var nv = assignedInstrumentTrack.controller.noteVisualizer;
            List<Vector3> targetPositions = nv.GetRibbonWorldPositionsForSteps(assignedInstrumentTrack, sharedTargetSteps);
            vine.CreateVines(to, targetPositions, sharedTargetSteps); // 🌿 now that we're in place
            reachedDestination = true;
        }
        
    }
    public void PulseIfOnStep(int currentStep)
    {
        if (!isInitialized || sharedTargetSteps == null || !reachedDestination) return;

        bool isOnBeat = TryGetComponent(out GhostNoteVine vine) && vine.IsHighlighted;

        // Pulse transform
        float targetScale = isOnBeat ? 1.2f : 1f;
        float scaleSpeed = 10f;
        transform.localScale = Vector3.Lerp(transform.localScale, originalScale * targetScale, Time.deltaTime * scaleSpeed);

        if (energySprite != null)
        {
            Color baseColor = assignedInstrumentTrack != null
                ? assignedInstrumentTrack.trackColor
                : Color.white;

            // Color and alpha targets
            Color targetColor = isOnBeat
                ? baseColor
                : Color.Lerp(baseColor, Color.white, 0.2f);
            float targetAlpha = isOnBeat ? 1f : 0.3f;
            float alphaSpeed = 10f;

            Color currentColor = energySprite.color;
            Color desiredColor = new Color(targetColor.r, targetColor.g, targetColor.b, targetAlpha);
            energySprite.color = Color.Lerp(currentColor, desiredColor, Time.deltaTime * alphaSpeed);
        }

        // Particle logic
        var cp = GetComponent<CollectableParticles>();
        cp?.SetGravityForBeat(isOnBeat);
        cp?.SetEmissionActive(isOnBeat);
    }

}
