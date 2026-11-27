using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Raw-polled menu navigation shared by MainMenu and OptionsScreen. It follows ModalMenu's rules:
/// D-pad / arrows / left stick, PadSemantics confirm and back, and one pad or any pad. Steps are
/// assigned rather than summed, so a pad that a Steam Deck reports twice can't double-step.
/// Left/Right come back as a held direction that fires once, then repeats; the menu decides
/// what a horizontal press means.
/// </summary>
public sealed class MenuNav
{
    public struct Frame
    {
        public int vertical;     // -1 up, +1 down (one per press)
        public int horizontal;   // -1 / +1 on press and on each repeat tick
        public bool hRepeat;     // horizontal came from a repeat tick, not a fresh press
        public bool confirm;
        public bool back;
    }

    const float StickThreshold = 0.6f;
    const float RepeatDelay = 0.35f;
    const float RepeatInterval = 0.08f;

    /// <summary>Last frame a menu consumed a keyboard Escape press. GlobalEscapeQuitListener skips its
    /// tap action for that press.</summary>
    public static int EscapeClaimedFrame { get; private set; } = -1;
    public static void ClaimEscape() => EscapeClaimedFrame = Time.frameCount;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => EscapeClaimedFrame = -1;

    /// <summary>Only this pad drives the menu; null = every connected pad.</summary>
    public Gamepad pad;
    /// <summary>Whether Space also confirms (the title menu); Options keeps ModalMenu's Enter only.</summary>
    public bool spaceConfirms;

    bool _stickVHeld = true;
    int _hHeldDir;
    float _hRepeatAt;
    bool _hNeedsRelease = true;

    /// <summary>Call when the menu (or its focus) changes: input already held is ignored until released.</summary>
    public void RequireRelease()
    {
        _stickVHeld = true;
        _hNeedsRelease = true;
        _hHeldDir = 0;
    }

    public Frame Poll()
    {
        var f = new Frame();
        bool stickV = false;

        if (pad != null) PollPad(pad, ref f, ref stickV);
        else
        {
            var pads = Gamepad.all;
            for (int i = 0; i < pads.Count; i++) PollPad(pads[i], ref f, ref stickV);
        }
        _stickVHeld = stickV;

        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.upArrowKey.wasPressedThisFrame) f.vertical = -1;
            else if (kb.downArrowKey.wasPressedThisFrame || kb.tabKey.wasPressedThisFrame) f.vertical = +1;
            f.confirm |= kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame
                         || (spaceConfirms && kb.spaceKey.wasPressedThisFrame);
            if (kb.escapeKey.wasPressedThisFrame) { f.back = true; }
        }

        f.horizontal = PollHorizontal(out f.hRepeat);
        return f;
    }

    /// <summary>True when the back press this frame came from the keyboard's Escape.</summary>
    public static bool EscapePressedThisFrame => Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;

    void PollPad(Gamepad p, ref Frame f, ref bool stickV)
    {
        if (p == null) return;
        if (p.dpad.up.wasPressedThisFrame) f.vertical = -1;
        else if (p.dpad.down.wasPressedThisFrame) f.vertical = +1;

        Vector2 s = p.leftStick.ReadValue();
        bool vertical = Mathf.Abs(s.y) > StickThreshold && Mathf.Abs(s.y) >= Mathf.Abs(s.x);
        if (vertical)
        {
            stickV = true;
            if (!_stickVHeld && f.vertical == 0) f.vertical = s.y > 0f ? -1 : +1;
        }

        f.confirm |= PadSemantics.ConfirmButton(p).wasPressedThisFrame;
        f.back |= PadSemantics.BackButton(p).wasPressedThisFrame;
    }

    int PollHorizontal(out bool isRepeat)
    {
        isRepeat = false;
        int dir = 0;
        if (pad != null) dir = HeldHorizontal(pad);
        else
            foreach (var p in Gamepad.all) { dir = HeldHorizontal(p); if (dir != 0) break; }

        var kb = Keyboard.current;
        if (dir == 0 && kb != null)
        {
            if (kb.leftArrowKey.isPressed) dir = -1;
            else if (kb.rightArrowKey.isPressed) dir = +1;
        }

        if (dir == 0) { _hHeldDir = 0; _hNeedsRelease = false; return 0; }
        if (_hNeedsRelease) return 0;

        float now = Time.unscaledTime;
        if (dir != _hHeldDir)
        {
            _hHeldDir = dir;
            _hRepeatAt = now + RepeatDelay;
            return dir;
        }
        if (now >= _hRepeatAt)
        {
            _hRepeatAt = now + RepeatInterval;
            isRepeat = true;
            return dir;
        }
        return 0;
    }

    static int HeldHorizontal(Gamepad p)
    {
        if (p == null) return 0;
        if (p.dpad.left.isPressed) return -1;
        if (p.dpad.right.isPressed) return +1;
        Vector2 s = p.leftStick.ReadValue();
        if (Mathf.Abs(s.x) > StickThreshold && Mathf.Abs(s.x) > Mathf.Abs(s.y)) return s.x > 0f ? +1 : -1;
        return 0;
    }
}
