public enum MineNodeBehaviorCategory
{
    Deliberate,  // Bass: slow, holds territory corridors, long commits
    Orbital,     // Harmony: arcs and loops back, orbit direction persists
    Darting,     // Lead: fast, jittery, proximity-evasive
    Rhythmic,    // Groove: burst-pause cycle snapped to beat boundary
}

public static class MineNodeBehaviorCategoryExtensions
{
    public static MineNodeBehaviorCategory GetBehaviorCategory(this MusicalRole role) => role switch
    {
        MusicalRole.Bass    => MineNodeBehaviorCategory.Deliberate,
        MusicalRole.Harmony => MineNodeBehaviorCategory.Orbital,
        MusicalRole.Lead    => MineNodeBehaviorCategory.Darting,
        MusicalRole.Groove  => MineNodeBehaviorCategory.Rhythmic,
        _                   => MineNodeBehaviorCategory.Deliberate,
    };
}
