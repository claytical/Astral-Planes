using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Layouts;
using UnityEngine.InputSystem.LowLevel;
#if UNITY_EDITOR
using UnityEditor;
#endif

// One of these exists per controller Steam Input reports. SteamInputManager feeds it each frame
// from ISteamInput action data. It is a *semantic* gamepad, not a physical one:
//   buttonSouth = "Choose" (confirm)   buttonEast  = "Back"
//   buttonWest  = "West"               buttonNorth = "North"
//   leftStick   = "Move"               rightTrigger = "Thrust"
//   select      = "Menu" (Select/View/Share/Minus)   start = "Start"
//   dpad        = "MenuUp/Down/Left/Right"           shoulders = "LeftShoulder/RightShoulder"
// Steam's per-controller config decides which physical button drives each action, so for
// example Switch A (right face button) arrives here as buttonSouth = confirm. Every existing
// <Gamepad>/… binding and every raw Gamepad poll in the game works on it unchanged.
#if UNITY_EDITOR
[InitializeOnLoad]
#endif
[InputControlLayout(stateType = typeof(GamepadState), displayName = "Steam Input Controller")]
public class SteamInputGamepad : Gamepad
{
    public const string LayoutName = nameof(SteamInputGamepad);

    /// <summary>Steam InputHandle_t value for this controller.</summary>
    public ulong SteamHandle { get; internal set; }

    /// <summary>Brand reported by Steam (GetInputTypeForHandle) when the device was created.</summary>
    public ControllerBrand Brand { get; internal set; } = ControllerBrand.Unknown;

    static SteamInputGamepad()
    {
        Register();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Register()
    {
        InputSystem.RegisterLayout<SteamInputGamepad>(LayoutName);
    }
}
