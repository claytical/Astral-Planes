using UnityEngine;

[RequireComponent(typeof(Collider2D))]
public class MIDISoundEffect : MonoBehaviour
{
    public CollectionEffectType effectType;
    public SoundEffectMood mood = SoundEffectMood.Friendly;
    
    [Tooltip("Only used for Role-based effects like LoopExpansion and TrackClear")]
    public MusicalRole musicalRole = MusicalRole.Harmony;

    private void OnTriggerEnter2D(Collider2D other)
    {
        if (other.GetComponent<Vehicle>())
        {
            Debug.Log("Playing Other Effect: " + mood);
            CollectionSoundManager.Instance.PlayEffect((int)mood);
        }
    }

    public void Collide()
    {
        Debug.Log("Playing Collide Effect: " + mood);

        CollectionSoundManager.Instance?.PlayEffect((int)effectType);
    }
}
