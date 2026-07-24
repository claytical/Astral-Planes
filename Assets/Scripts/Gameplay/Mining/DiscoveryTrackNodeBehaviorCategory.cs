public enum DiscoveryTrackNodeBehaviorCategory
{
    Deliberate,  // Bass: slow, holds territory corridors, long commits
    Orbital,     // Harmony: arcs and loops back, orbit direction persists
    Darting,     // Lead: fast, jittery, proximity-evasive
    Rhythmic,    // Groove: burst-pause cycle snapped to beat boundary
}

public static class DiscoveryTrackNodeBehaviorCategoryExtensions
{
    public static DiscoveryTrackNodeBehaviorCategory GetBehaviorCategory(this MusicalRole role) => role switch
    {
        MusicalRole.Bass    => DiscoveryTrackNodeBehaviorCategory.Deliberate,
        MusicalRole.Harmony => DiscoveryTrackNodeBehaviorCategory.Orbital,
        MusicalRole.Lead    => DiscoveryTrackNodeBehaviorCategory.Darting,
        MusicalRole.Groove  => DiscoveryTrackNodeBehaviorCategory.Rhythmic,
        _                   => DiscoveryTrackNodeBehaviorCategory.Deliberate,
    };
}
