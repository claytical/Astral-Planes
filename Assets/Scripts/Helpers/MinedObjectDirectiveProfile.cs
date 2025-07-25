using UnityEngine;

[CreateAssetMenu(menuName = "Astral Planes/Mined Object Directive")]
public class MinedObjectDirectiveProfile : ScriptableObject
{
    public MinedObjectType minedObjectType;
    public MusicalRoleProfile role;
    public NoteSetSeries noteSetSeries;
    public TrackModifierType trackModifierType;
    }
[System.Serializable]
public class WeightedDirective
{
    public MinedObjectDirectiveProfile directive;
    public float weight = 1f;
}