using UnityEngine;

[CreateAssetMenu(
    fileName = "RoleMotifNoteSetConfig",
    menuName = "Astral Planes/Motif/Role Motif NoteSet Config")]
public class RoleMotifNoteSetConfig : ScriptableObject
{
    [Header("Identity")]
    public string id;
    public MusicalRoleProfile roleProfile;
    public MusicalRole role => roleProfile != null ? roleProfile.role : MusicalRole.None;

    [Header("Authored Riff")]
    public RiffAsset riff;

    [Header("MineNode")]
    [Tooltip("Legacy: tint budget system removed in Stage 5. No longer read at runtime.")]
    [Min(0)]
    public int mineNodeTintBudget = 20;

    [Tooltip("Number of loop boundaries after spawn before the node expires if not captured. 0 = never.")]
    [Min(0)]
    public int mineNodeExpireAfterLoops = 0;

    [Header("Note Ascension")]
    [Tooltip("How many drum loops collected notes take to rise to the ascension line. 0 = use NoteAscensionDirector default.")]
    [Min(0)]
    public int ascendLoops = 0;
}