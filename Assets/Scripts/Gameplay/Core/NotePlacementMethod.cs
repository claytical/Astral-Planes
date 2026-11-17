/// <summary>How a Vehicle places collected notes into the loop. Chosen per player at ship select.</summary>
public enum NotePlacementMethod
{
    /// <summary>Vehicle carries notes and the player commits them with the release button.</summary>
    Manual = 0,
    /// <summary>Notes auto-deposit into the NoteVisualizer at the quantized moment.</summary>
    Auto = 1,
}

/// <summary>
/// Single source of truth for the starting placement mode (LEGIBILITY_AUDIT.md C4). LocalPlayer,
/// PlayerSelect and Vehicle all initialize from this so they can't disagree again.
/// </summary>
public static class NotePlacementDefaults
{
    public const NotePlacementMethod Default = NotePlacementMethod.Auto;
}
