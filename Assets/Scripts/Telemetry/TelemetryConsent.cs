// Player-facing "Publish Flight Plans" preference. Deliberately separate from
// TelemetryConfig.enabled, which is a build/developer toggle, not consent — playing the game,
// enabling Steam Stats, or any other analytics setting must never imply this. No Flight Plan
// leaves the machine unless PublishFlightPlansEnabled is explicitly true.
//
// Stored in GameSettings (settings.json). A choice is always made through Decide(), which
// sets both fields in a single atomic write.
public static class TelemetryConsent
{
    public static bool PublishFlightPlansEnabled => GameSettings.PublishEnabled;

    // Whether the player has ever made an explicit Publish / Don't Publish choice. Gates the
    // Living Score first-run prompt (SolarSystemRecordDisplay) and SelectGlyphHint.
    public static bool HasDecided => GameSettings.PublishDecided;

    /// <summary>Records an explicit choice (Living Score prompt or Options → Publishing).</summary>
    public static void Decide(bool publish) => GameSettings.SetPublishChoice(publish);
}
