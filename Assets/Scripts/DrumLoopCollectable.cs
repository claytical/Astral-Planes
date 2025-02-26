using UnityEngine;

public class DrumLoopCollectable : MonoBehaviour
{
    public AudioClip newDrumLoopClip;
    private DrumTrack drumTrack;

    public void SetTrack(DrumTrack drums)
    {
        drumTrack = drums;
    }

    private void OnTriggerEnter2D(Collider2D coll)
    {
        if (coll.gameObject.GetComponent<Vehicle>() != null)
        {
            if (drumTrack != null && newDrumLoopClip != null)
            {
                // Schedule the drum loop change using the new AudioClip.
                drumTrack.ScheduleDrumLoopChange(newDrumLoopClip);
            }
            Destroy(gameObject);
        }
    }

}
