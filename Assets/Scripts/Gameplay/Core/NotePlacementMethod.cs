/// <summary>How a Vehicle places collected notes into the loop. Chosen per player at ship select.</summary>
public enum NotePlacementMethod
{
    /// <summary>Vehicle carries notes and the player commits them with the release button.</summary>
    Manual = 0,
    /// <summary>Notes auto-deposit into the NoteVisualizer at the quantized moment.</summary>
    Auto = 1,
}
