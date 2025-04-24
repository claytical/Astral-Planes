using UnityEngine;

[RequireComponent(typeof(Collider2D))]
public class MIDISoundEffect : MonoBehaviour
{
    public CollectionEffectType effectType;

    [Tooltip("Only used for Role-based effects like LoopExpansion and TrackClear")]
    public MusicalRole musicalRole = MusicalRole.Harmony;

    private void OnTriggerEnter2D(Collider2D other)
    {
        if (other.GetComponent<Vehicle>() && CollectionSoundManager.Instance != null)
        {
            CollectionSoundManager.Instance.PlayEffect(effectType, musicalRole);
        }
    }

    public void Collide()
    {
        CollectionSoundManager.Instance.PlayEffect(effectType, musicalRole);
    }
}
