// ShardPickup.cs (patch)
using System.Collections;
using UnityEngine;

[RequireComponent(typeof(Collider2D))]
public class ShardPickup : MonoBehaviour
{
    [Header("Ghost Payload")]
    public InstrumentTrack track;
    public int   step;
    public int   note;
    public int   duration;
    public float velocity = 80f;
    private PhaseStar owner;
    [Header("Behavior")]
    public float energyCost = 0.06f;
    public float flyToRibbonDuration = 0.22f;

    private bool armed = false;          // ⬅ default FALSE now
    private bool initialized = false;
    private Collider2D col;

    void Awake()
    {
        col = GetComponent<Collider2D>();
        if (col) col.enabled = false;    // ⬅ collider off until configured+armed
        Debug.Log($"Shard Created {gameObject.name}");
    }

    public void SetOwner(PhaseStar ps)
    {
        owner = ps;
    }    // Called by PhaseStar immediately after Instantiate
    public void Configure(InstrumentTrack t, int s, int n, int d, float v,
                          float cost, float flyDur)
    {
        track = t; step = s; note = n; duration = d; velocity = v;
        energyCost = cost; flyToRibbonDuration = flyDur;
        initialized = true;
    }

    // Called by PhaseStar right after Configure
    public void ArmNextFixedUpdate(float extraDelaySeconds = 0f)
    {
        StartCoroutine(ArmRoutine(extraDelaySeconds));
    }

    private IEnumerator ArmRoutine(float delay)
    {
        // ensure we pass at least one physics step
        yield return new WaitForFixedUpdate();
        if (delay > 0f) yield return new WaitForSeconds(delay);

        if (col) col.enabled = true;
        armed = true;
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        // ⬅ hard guards
        if (!initialized || !armed || track == null) return;

        var v = other.GetComponent<Vehicle>();
        if (v == null) return;

        armed = false; // single-use
        v.ConsumeEnergy(energyCost);

        // safe now: track was assigned in Configure
        var nv = track.controller.noteVisualizer;
        Vector3 ribbonPos = nv.ComputeRibbonWorldPosition(track, step);

        StartCoroutine(FlyThenCommit(ribbonPos));
    }
    private IEnumerator FlyThenCommit(Vector3 ribbonTarget)
    {
        Vector3 start = transform.position;
        float t = 0f;

        while (t < 1f)
        {
            t += Time.deltaTime / Mathf.Max(0.01f, flyToRibbonDuration);
            float eased = Mathf.SmoothStep(0f, 1f, t);
            transform.position = Vector3.Lerp(start, ribbonTarget, eased);
            yield return null;
        }

        var myTrack = track; var myStep = step; var myNote = note;
        var myDur = duration; var myVel = Mathf.Clamp(velocity, 50f, 127f);

        // single, ordered placement
        RemixCorrectionQueue.Instance.Enqueue(() =>
        {
            myTrack.AddNoteToLoop(myStep, myNote, myDur, myVel);
            myTrack.controller.noteVisualizer.PlacePersistentNoteMarker(myTrack, myStep);
            CollectionSoundManager.Instance?.PlayEffect(SoundEffectPreset.Aether);
            owner?.NotifyShardResolved();
        });

        var explode = GetComponent<Explode>();
        if (explode != null) explode.Permanent(); else Destroy(gameObject);
    }

}
