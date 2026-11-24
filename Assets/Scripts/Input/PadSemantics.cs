using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

// Semantic confirm/back for raw Gamepad polls. Gameplay uses the Nintendo convention on Switch
// controllers: A (right face button) confirms, B (bottom face button) backs out.
//
// - SteamInputGamepad (Steam is driving input): already semantic. Steam's per-controller config
//   maps Switch A → "Choose" → buttonSouth, so south/east are returned as-is.
// - Native SwitchProControllerHID (no Steam): Unity maps by position (A = buttonEast,
//   B = buttonSouth), so they're swapped here. Matches the "Switch" control-scheme bindings in
//   AstralPlanes.inputactions and Unity's own Submit/Cancel usages on that layout.
// - Every other pad: bottom = confirm, right = back.
public static class PadSemantics
{
    public static bool UsesNintendoSwap(Gamepad pad)
        => pad != null && pad is not SteamInputGamepad
           && SteamInputManager.BrandOf(pad) == ControllerBrand.Switch;

    public static ButtonControl ConfirmButton(Gamepad pad)
        => UsesNintendoSwap(pad) ? pad.buttonEast : pad.buttonSouth;

    public static ButtonControl BackButton(Gamepad pad)
        => UsesNintendoSwap(pad) ? pad.buttonSouth : pad.buttonEast;

    /// <summary>True when this control is the "back" button of its gamepad.</summary>
    public static bool IsBackControl(InputControl control)
        => control != null && control.device is Gamepad pad && control == BackButton(pad);
}
