using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
public class Collectable : MonoBehaviour
{
    public InstrumentTrack assignedInstrumentTrack; // 🎼 The track that spawned this collectable
    public SpriteRenderer energySprite;
    public int burstId; 
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
    [SerializeField] private float pulseSpeed = 1.6f;
    [SerializeField] private float minAlpha = 0.25f;
    [SerializeField] private float maxAlpha = 0.65f;
    [SerializeField] private float pulseScale = 1.08f;
    private Coroutine _pulseRoutine;
    public delegate void OnCollectedHandler(int duration, float force);
    public event OnCollectedHandler OnCollected;   // informational; does not call the track
    public event Action OnDestroyed;               // for bookkeeping (track cleans lists, etc.)

    public List<int> sharedTargetSteps = new List<int>();
    public int GetNote() => assignedNote;
    public bool IsDark { get; private set; } = false;

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
            particleScript.ConfigureByDuration(noteSet, duration, track);
        if (track != null)
            energySprite.color = track.trackColor;
        if (energySprite != null && track != null)
        {
            var c = track.trackColor;
            c.a = Mathf.Clamp01(maxAlpha);     // start semi-transparent
            energySprite.color = c;

            if (_pulseRoutine != null) StopCoroutine(_pulseRoutine);
            _pulseRoutine = StartCoroutine(PulseEnergySprite());
        }


        if (assignedInstrumentTrack == null)
            Debug.LogError($"Collectable {gameObject.name} - assignedInstrumentTrack is NULL on initialization!");

        StartCoroutine(DarkTimeoutRoutine(track));
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
    public void AttachTetherAtSpawn(Transform marker, GameObject tetherPrefabGO, Color trackColor, float durSteps)
    {
        Debug.Log($"AttachingTetherAtSpawn { (marker ? marker.name : "(null)") }");
        ribbonMarker = marker;
        if (!ribbonMarker || tether != null || tetherPrefabGO == null) return;

        var go = Instantiate(tetherPrefabGO);
        tether = go.GetComponent<NoteTether>() ?? go.AddComponent<NoteTether>();
        tether.SetEndpoints(transform, ribbonMarker, trackColor, 1f);
        if (TryGetComponent(out CollectableParticles cp) && tether != null)
        {
            Debug.Log($"Has tether!");
            // speed can be scaled by duration if you like (durSteps) to make long notes ooze slower
            float dripSpeed = Mathf.Lerp(0.3f, 0.9f, Mathf.Clamp01(durSteps / 6f)); // longer → faster or slower, your call
            cp.RegisterTether(tether, pull: 0.7f);
            //tether.RegisterDripEmitter(cp, dripSpeed);
        }

    }
    public void TravelAlongTetherAndFinalize(int durationTicks, float force, float seconds = 0.35f)
    {
        StartCoroutine(TravelRoutine(durationTicks, force, seconds));
    }
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

    void Start()
    {
        originalScale = transform.localScale;
        isInitialized = true;
    }
    private IEnumerator DarkTimeoutRoutine(InstrumentTrack track)
    {
        var drums = track != null ? track.drumTrack : null;
        if (drums == null) yield break;

        // ~just over one loop; tweak to taste or make profile-driven
        float ttl = Mathf.Max(3f, drums.GetLoopLengthInSeconds() * 1.1f);
        yield return new WaitForSeconds(ttl);

        // Become "dark" (muted look), but keep collider & collection live
        IsDark = true;

        if (energySprite != null)
        {
            var c = energySprite.color;
            // desaturate & lower alpha a bit
            var grey = new Color(0.25f, 0.25f, 0.25f, Mathf.Clamp01(c.a * 0.55f));
            energySprite.color = grey;
        }

        // If we have a marker, ensure it’s greyed
        if (ribbonMarker != null)
        {
            var ml = ribbonMarker.GetComponent<MarkerLight>() ?? ribbonMarker.gameObject.AddComponent<MarkerLight>();
            ml.SetGrey(new Color(1f, 1f, 1f, 0.18f));
        }
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
    private IEnumerator PulseEnergySprite()
    {
        Vector3 startScale = transform.localScale;
        while (true)
        {
            float t = (Mathf.Sin(Time.time * pulseSpeed) * 0.5f) + 0.5f; // 0..1
            float a = Mathf.Lerp(minAlpha, maxAlpha, t);
            if (energySprite != null)
            {
                var col = energySprite.color; col.a = a; energySprite.color = col;
            }
            transform.localScale = startScale * Mathf.Lerp(1f, pulseScale, t);
            yield return null;
        }
    }
    private IEnumerator FinishAfterPulse(System.Action afterPulse)
    {
        // tiny delay allows PulseToEnd to schedule fade
        yield return new WaitForSeconds(0.05f);
        afterPulse?.Invoke();
        Destroy(gameObject);
    }
    private void NotifyDestroyedOnce()
    {
        if (_destroyNotified) return;
        _destroyNotified = true;
        OnDestroyed?.Invoke();
    }
    
    private void OnEnable()
    {
        _handled = false;
        _destroyNotified = false;
    }
    private void OnDestroy()  { NotifyDestroyedOnce(); }
    private void OnDisable()  { NotifyDestroyedOnce(); } // pooling-safe
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

            // success path
            float force = vehicle.GetForceAsMidiVelocity();
            vehicle.CollectEnergy(amount);
            assignedInstrumentTrack.OnCollectableCollected(this, intendedStep >= 0 ? intendedStep : matchedStep, noteDurationTicks, force);
            if (TryGetComponent(out Collider2D col)) col.enabled = false;
            var explode = GetComponent<Explode>();
            if(explode != null) explode.Permanent(false);

    }
    
 
}
