using System;
using UnityEngine;

public class ShardPickup : MonoBehaviour
{
    public InstrumentTrack targetTrack { get; private set; }
    public NoteSet targetNoteSet { get; private set; }
    public PhaseStar owner { get; private set; }

    // Tunables
    [SerializeField] private float armingDelay = 0.05f; // avoid same-frame double-triggers
    private bool armed = false;
    private bool burstTriggered = false;

    // Optional: little bob
    [SerializeField] private float bobAmp = 0.1f;
    [SerializeField] private float bobHz  = 1.2f;
    private Vector3 startPos;

    void Awake() { startPos = transform.position; }

    public void SetOwner(PhaseStar star) => owner = star;

    public void Configure(InstrumentTrack track, NoteSet noteSet)
    {
        targetTrack  = track;
        targetNoteSet = noteSet;
    }

    public void ArmNextFixedUpdate(float extraDelay = 0f)
    {
        if (!gameObject.activeInHierarchy) return;
        StartCoroutine(ArmRoutine(extraDelay));
    }

    private System.Collections.IEnumerator ArmRoutine(float extra)
    {
        yield return new WaitForSeconds(armingDelay + Mathf.Max(0f, extra));
        armed = true;
    }

    void Update()
    {
        // gentle bob
        if (bobAmp > 0f)
        {
            float y = Mathf.Sin(Time.time * Mathf.PI * 2f * bobHz) * bobAmp;
            transform.position = new Vector3(startPos.x, startPos.y + y, startPos.z);
        }
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        if (!armed || burstTriggered) return;
        if (!other.TryGetComponent<Vehicle>(out _)) return;

        TriggerBurst();
    }

    private void OnCollisionEnter2D(Collision2D coll)
    {
        if (!armed || burstTriggered) return;
        if (!coll.gameObject.TryGetComponent<Vehicle>(out _)) return;

        TriggerBurst();
    }

    private void TriggerBurst()
    {
        if (burstTriggered) return;
        burstTriggered = true;

        if (targetTrack == null || targetNoteSet == null)
        {
            Debug.LogWarning("ShardPickup: missing targetTrack or targetNoteSet.");
            Destroy(gameObject);
            owner?.NotifyShardBurstComplete(null);
            return;
        }

        var dt = targetTrack.drumTrack;
        var parent = targetTrack.collectableParent != null
            ? targetTrack.collectableParent
            : targetTrack.transform;

        // Spawn a one-track burst that uses the GhostPattern math
        NoteBurstSpawner.SpawnBurst(
            targetTrack,
            targetNoteSet,
            parent,
            transform.position,
            dt.GetLoopLengthInSeconds()
        );

        // Inform the owner star that the burst started; the star will watch the track until all despawn
        owner?.NotifyShardBurstComplete(targetTrack);

        // optional fx here…

        Destroy(gameObject);
    }
}
