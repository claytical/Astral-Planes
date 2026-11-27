/// <summary>How a Vehicle places collected notes into the loop. Chosen per player at ship select.</summary>
public enum NotePlacementMethod
{
    /// <summary>Vehicle carries notes and the player commits them with the release button.</summary>
    Manual = 0,
    /// <summary>Notes auto-deposit into the NoteVisualizer at the quantized moment.</summary>
    Auto = 1,
}

/// <summary>
/// Fallback starting placement mode (LEGIBILITY_AUDIT.md C4) and GameSettings' default. A fresh
/// player at ship select starts on GameSettings.GrooveStyle (the last confirmed choice, also in
/// Options → Controls); this constant only seeds field initializers and the settings default.
/// </summary>
public static class NotePlacementDefaults
{
    public const NotePlacementMethod Default = NotePlacementMethod.Auto;
}
