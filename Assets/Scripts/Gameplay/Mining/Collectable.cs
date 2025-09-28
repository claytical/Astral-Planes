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
    private int assignedNote;          // 🎵 The MIDI note value
    public Transform ribbonMarker;           // assigned when spawned
    public NoteTether tether;               // runtime
    private bool awaitingPulse = false;
    public int intendedStep = -1;       // set at spawn (authoritative target)

    private Vector3 originalScale;
    private bool isInitialized = false;
    private bool reachedDestination = false;

    // Idempotency flags
    private bool _handled;            // prevents double processing on trigger
    private bool _destroyNotified;    // prevents double OnDestroyed

    // Optional availability tension (currently only used if you enable it)
    private double _availableUntilDsp;
    [SerializeField] [Range(0.6f, 1.4f)]
    private float availabilityFactor = 1.0f;

    public delegate void OnCollectedHandler(int duration, float force);
    public event OnCollectedHandler OnCollected;   // informational; does not call the track
    public event Action OnDestroyed;               // for bookkeeping (track cleans lists, etc.)

    public List<int> sharedTargetSteps = new List<int>();

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

    private void OnEnable()
    {
        _handled = false;
        _destroyNotified = false;
    }

// Collectable.cs  (replace the existing method)
    public void AttachTetherAtSpawn(Transform marker, GameObject tetherPrefabGO, Color trackColor)
    {
        Debug.Log($"AttachingTetherAtSpawn { (marker ? marker.name : "(null)") }");
        ribbonMarker = marker;
        if (!ribbonMarker || tether != null || tetherPrefabGO == null) return;

        var go = Instantiate(tetherPrefabGO);
        tether = go.GetComponent<NoteTether>() ?? go.AddComponent<NoteTether>();
        tether.SetEndpoints(transform, ribbonMarker, trackColor, 1f);
    }

    public void TravelAlongTetherAndFinalize(int durationTicks, float force, float seconds = 0.35f)
    {
        StartCoroutine(TravelRoutine(durationTicks, force, seconds));
    }

    private IEnumerator TravelRoutine(int durationTicks, float force, float seconds)
    {
        if (TryGetComponent(out Collider2D col)) col.enabled = false;
        var ps = GetComponentInChildren<ParticleSystem>();
        if (ps) ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);

        float t = 0f;
        while (t < seconds && tether)
        {
            t += Time.deltaTime;
            float u = Mathf.SmoothStep(0f, 1f, t / seconds);
            transform.position = tether.EvaluatePosition01(u);
            yield return null;
        }
        if (ribbonMarker) transform.position = ribbonMarker.position;

        var ml = ribbonMarker ? ribbonMarker.GetComponent<MarkerLight>() : null;
        if (ml) ml.LightUp(tether != null ? tether.baseColor : Color.white);

        OnCollected?.Invoke(durationTicks, force); // ✅ event raised inside Collectable
        OnDestroyed?.Invoke();

        if (tether) Destroy(tether.gameObject);
        Destroy(gameObject);
    }

    public void AttachTether(Transform marker, Color trackColor, LineRenderer tetherTemplate = null)
    {
        ribbonMarker = marker;
        if (ribbonMarker == null) return;

        // make a child gameobject to hold the tether
        var go = new GameObject("NoteTether");
        go.transform.SetParent(null); // world
        tether = go.AddComponent<NoteTether>();
        var lr = go.GetComponent<LineRenderer>();

        // copy style from an optional template (so you can art-direct once in a prefab)
        if (tetherTemplate != null)
        {
            lr.material       = new Material(tetherTemplate.material);
            lr.textureMode    = tetherTemplate.textureMode;
            tether.segments   = Mathf.Max(16, tetherTemplate.positionCount);
            tether.baseWidth  = tetherTemplate.widthMultiplier;
            tether.noiseAmp   = 0.15f;
            tether.noiseSpeed = 1.5f;
            tether.sag        = 0.6f;
        }
        else
        {
            // basic material if none provided
            lr.material = new Material(Shader.Find("Sprites/Default"));
        }

        tether.SetEndpoints(transform, ribbonMarker, trackColor, 1f);
    }
    // 🔹 Initializes the collectable with its note data
    public void Initialize(int note, int duration, InstrumentTrack track, NoteSet noteSet, List<int> steps)
    {
        assignedNote        = note;
        noteDurationTicks   = duration;
        assignedInstrumentTrack = track;
        sharedTargetSteps   = steps ?? new List<int>();

        if (sharedTargetSteps.Count == 0)
            Debug.LogWarning($"{gameObject.name} - No target steps provided.");

        if (TryGetComponent(out CollectableParticles particleScript) && noteSet != null)
            particleScript.Configure(noteSet);

        if (track != null)
            energySprite.color = track.trackColor;

        if (assignedInstrumentTrack == null)
            Debug.LogError($"Collectable {gameObject.name} - assignedInstrumentTrack is NULL on initialization!");

        // Lifetime profile if available, otherwise a safe TTL fallback
        if (TryGetComponent(out Explode explode))
        {
            explode.ApplyLifetimeProfile(LifetimeProfile.GetProfile(MinedObjectType.Note));
        }
        else
        {
            float ttl = Mathf.Max(3f, track.drumTrack.GetLoopLengthInSeconds() * 1.1f);
            StartCoroutine(AutoExpire(ttl));
        }

        // (Optional) duration-based availability window — enable if you want “true miss” by time.
        // var dt = track.drumTrack.GetLoopLengthInSeconds() / track.drumTrack.totalSteps;
        // int ticksPerStep = Mathf.RoundToInt(480f / (track.GetTotalSteps() / 4f));
        // int durSteps     = Mathf.Max(1, Mathf.RoundToInt((float)duration / Mathf.Max(1, ticksPerStep)));
        // _availableUntilDsp = AudioSettings.dspTime + durSteps * dt * availabilityFactor;
    }
// Call this from your existing "player collected me" path
    public void HandleCollectedWithTether(System.Action afterPulse)
    {
        if (awaitingPulse || ribbonMarker == null || tether == null)
        {
            // No tether? just run existing completion immediately
            afterPulse?.Invoke();
            Destroy(gameObject);
            return;
        }

        awaitingPulse = true;

        // Hide the collectable visuals now (so it feels like energy converted)
        var sr = GetComponentInChildren<SpriteRenderer>();
        if (sr) sr.enabled = false;
        var ps = GetComponentInChildren<ParticleSystem>();
        if (ps) ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);

        // run pulse to marker, then light marker, then finish
        var markerLight = ribbonMarker.GetComponent<MarkerLight>();
        StartCoroutine(
            tether.PulseToEnd(0.45f, () =>
            {
                if (markerLight != null)
                    markerLight.LightUp(sr ? sr.color : Color.white);
            })
        );

        StartCoroutine(FinishAfterPulse(afterPulse));
    }

    private IEnumerator FinishAfterPulse(System.Action afterPulse)
    {
        // tiny delay allows PulseToEnd to schedule fade
        yield return new WaitForSeconds(0.05f);
        afterPulse?.Invoke();
        Destroy(gameObject);
    }

    private IEnumerator AutoExpire(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        NotifyDestroyedOnce();
        Destroy(gameObject);
    }

    public int GetNote() => assignedNote;

    private void NotifyDestroyedOnce()
    {
        if (_destroyNotified) return;
        _destroyNotified = true;
        OnDestroyed?.Invoke();
    }

    private void OnFailedCollect()
    {
        if (energySprite != null) StartCoroutine(FlashRedThenDie());

        if (TryGetComponent(out Collider2D col)) col.enabled = false;

        NotifyDestroyedOnce();

        if (TryGetComponent(out CollectableParticles particles))
            particles.EmitZap();

        Destroy(gameObject);
    }

    private void OnDestroy()  { NotifyDestroyedOnce(); }
    private void OnDisable()  { NotifyDestroyedOnce(); } // pooling-safe

    private IEnumerator FlashRedThenDie()
    {
        Color original = energySprite.color;
        energySprite.color = Color.red;
        yield return new WaitForSeconds(0.1f);
        energySprite.color = original;
    }

    private void OnTriggerEnter2D(Collider2D coll)
    {
        var vehicle = coll.GetComponent<Vehicle>();
        if (vehicle == null || _handled) return;
        _handled = true; // ✅ idempotent

        // (Optional availability check)
        // if (_availableUntilDsp > 0 && AudioSettings.dspTime > _availableUntilDsp) { OnFailedCollect(); return; }

        // 1) compute loop timing
        var drumTrack    = assignedInstrumentTrack.drumTrack;
        double dspNow    = AudioSettings.dspTime;
        double loopLen   = drumTrack.GetLoopLengthInSeconds();
        double stepDur   = loopLen / drumTrack.totalSteps;
        double timingWin = stepDur * drumTrack.timingWindowSteps * 0.5;
        double loopStart = drumTrack.startDspTime;
        double tPos      = (dspNow - loopStart) % loopLen;
        if (tPos < 0) tPos += loopLen;

        // 2) pick the best matching target step (within timing window)
        int matchedStep = -1;
        double bestErr = timingWin;
        for (int i = 0; i < sharedTargetSteps.Count; i++)
        {
            int step = sharedTargetSteps[i];
            double stepPos = (step * stepDur) % loopLen;
            double delta   = Math.Abs(stepPos - tPos);
            if (delta > loopLen * 0.5) delta = loopLen - delta;

            if (delta < bestErr)
            {
                bestErr = delta;
                matchedStep = step;
            }
        }

        if (matchedStep >= 0)
        {
            // success path
            float force = vehicle.GetForceAsMidiVelocity();
            vehicle.CollectEnergy(amount);
            assignedInstrumentTrack.OnCollectableCollected(this, intendedStep >= 0 ? intendedStep : matchedStep, noteDurationTicks, force);
            if (TryGetComponent(out Collider2D col)) col.enabled = false;
        }
        else
        {
            // miss (outside timing window)
            OnFailedCollect();
        }
    }

    public void DriftToTarget(Vector3 from, Vector3 to, float duration)
    {
        StartCoroutine(DriftRoutine(from, to, duration));
    }

    private IEnumerator DriftRoutine(Vector3 from, Vector3 to, float duration)
    {
        if (TryGetComponent(out Collider2D col)) col.enabled = false;

        transform.position = from;
        float elapsed = 0f;
        while (elapsed < duration)
        {
            float t = elapsed / Mathf.Max(0.0001f, duration);
            transform.position = Vector3.Lerp(from, to, t);
            elapsed += Time.deltaTime;
            yield return null;
        }

        transform.position = to;
        if (TryGetComponent(out Collider2D col2)) col2.enabled = true;

        if (TryGetComponent(out GhostNoteVine vine))
        {
            var nv = assignedInstrumentTrack.controller.noteVisualizer;
            List<Vector3> targetPositions = nv.GetRibbonWorldPositionsForSteps(assignedInstrumentTrack, sharedTargetSteps);
            // vine.CreateVines(to, targetPositions, sharedTargetSteps);
            reachedDestination = true;
        }
    }

    public void PulseIfOnStep(int currentStep)
    {
        if (!isInitialized || sharedTargetSteps == null || !reachedDestination) return;

        bool isOnBeat = TryGetComponent(out GhostNoteVine vine) && vine.IsHighlighted;

        float targetScale = isOnBeat ? 1.2f : 1f;
        float scaleSpeed = 10f;
        transform.localScale = Vector3.Lerp(transform.localScale, originalScale * targetScale, Time.deltaTime * scaleSpeed);

        if (energySprite != null)
        {
            Color baseColor = assignedInstrumentTrack != null ? assignedInstrumentTrack.trackColor : Color.white;
            Color targetColor = isOnBeat ? baseColor : Color.Lerp(baseColor, Color.white, 0.2f);
            float targetAlpha = isOnBeat ? 1f : 0.3f;
            float alphaSpeed = 10f;

            Color currentColor = energySprite.color;
            Color desiredColor = new Color(targetColor.r, targetColor.g, targetColor.b, targetAlpha);
            energySprite.color = Color.Lerp(currentColor, desiredColor, Time.deltaTime * alphaSpeed);
        }

        if (TryGetComponent(out CollectableParticles cp))
        {
            cp.SetGravityForBeat(isOnBeat);
            cp.SetEmissionActive(isOnBeat);
        }
    }
}
