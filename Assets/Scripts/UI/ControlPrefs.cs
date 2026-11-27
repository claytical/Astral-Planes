using System;

/// <summary>Which of the two keyboard/mouse control schemes the keyboard player uses.</summary>
public enum KbmScheme
{
    /// <summary>A/D rotate the heading, W/S drift forward / turn around, Shift boost, Space place.</summary>
    Keyboard = 0,
    /// <summary>Ship flies toward the cursor, left click boost, right click place.</summary>
    Mouse = 1,
}

/// <summary>
/// Player-facing keyboard/mouse control preference (Options → Controls → Keyboard Scheme).
/// Stored in <see cref="GameSettings"/> (settings.json). The names returned by
/// <see cref="SchemeName"/> must match the control schemes in AstralPlanes.inputactions; each scheme
/// pairs both Keyboard and Mouse and acts purely as a binding mask.
/// </summary>
public static class ControlPrefs
{
    public const string KeyboardSchemeName = "Keyboard";
    public const string MouseSchemeName = "Mouse";

    /// <summary>Fired after the scheme changes, so joined keyboard players can switch live.</summary>
    public static event Action<KbmScheme> Changed;

    public static KbmScheme Scheme
    {
        get => GameSettings.KbmScheme;
        set
        {
            if (Scheme == value) return;
            GameSettings.KbmScheme = value;
            GameSettings.Flush();
            Changed?.Invoke(value);
        }
    }

    public static string SchemeName => NameOf(Scheme);

    public static string NameOf(KbmScheme scheme) =>
        scheme == KbmScheme.Mouse ? MouseSchemeName : KeyboardSchemeName;

    public static bool IsKbmSchemeName(string name) =>
        name == KeyboardSchemeName || name == MouseSchemeName;

    /// <summary>GameSettings.RestoreDefaults changed the stored scheme underneath us.</summary>
    internal static void RaiseChanged(KbmScheme scheme) => Changed?.Invoke(scheme);
}
