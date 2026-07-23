using UnityEngine;

public partial class CosmicDust
{
    // Energy unit backing fields. Charge01 is derived — do NOT write to it directly.
    private int _maxEnergyUnits = 1;
    private int _currentEnergyUnits = 1;
    public int maxEnergyUnits => _maxEnergyUnits;
    public int currentEnergyUnits => _currentEnergyUnits;
    public float Charge01 => (float)_currentEnergyUnits / Mathf.Max(1, _maxEnergyUnits);
    public MusicalRole Role { get; private set; } = MusicalRole.None;
    private const float kSolidAlphaFloor = .55f;

    public void EnsureMinSolidAlpha(float minAlpha)
    {
        if (_currentTint.a <= minAlpha)
        {
            _currentTint.a = minAlpha;
            // Ensure at least 1 energy unit so the cell is solid.
            if (_currentEnergyUnits <= 0)
                _currentEnergyUnits = 1;
        }
        ApplyDisplayedTint(_currentTint);
        OnChargeChanged?.Invoke(Charge01);
    }

    // maxUnits: pass > 0 to update the cell's max energy units (from a role profile).
    // Pass -1 (default) to keep the current max.
    public void ApplyRoleAndCharge(MusicalRole r, Color roleColorRgb, float charge, int maxUnits = -1)
    {
        Role = r;
        if (r != MusicalRole.None) _hiddenHintColor = Color.clear;
        OnRoleChanged?.Invoke(Role);
        if (maxUnits > 0) _maxEnergyUnits = maxUnits;
        SetEnergyUnits(Mathf.RoundToInt(Mathf.Clamp01(charge) * _maxEnergyUnits));
        float visibleAlpha = Mathf.Lerp(kSolidAlphaFloor, 1f, Charge01);
        roleColorRgb.a = visibleAlpha;
        // Alpha comes from the caller (roleProfile.baseAlpha via GetBaseColor()).
        // Do NOT floor it here — cells that aren't Solid yet must stay dim.
        // The generator enforces a visible-alpha floor when solidifying.
        SetBaseTint(roleColorRgb, applyImmediatelyIfNoPulse: false);
        OnTintStateChanged?.Invoke(_currentTint);
        OnChargeChanged?.Invoke(Charge01);
    }

    // Sets current energy units, clamped [0, max]. Updates tint alpha accordingly.
    private void SetEnergyUnits(int units)
    {
        _currentEnergyUnits = Mathf.Clamp(units, 0, _maxEnergyUnits);
        float visibleAlpha = Mathf.Lerp(kSolidAlphaFloor, 1f, Charge01);
        _currentTint.a = visibleAlpha;
        OnTintStateChanged?.Invoke(_currentTint);
        OnChargeChanged?.Invoke(Charge01);
    }

    // Decrements energy units by amount. Drives visual drain. Returns actual units removed.
    // When units hit 0, disables the terrain collider — but does NOT call the generator.
    // Callers are responsible for generator-level cleanup (ClearCell vs ResetDustToNoneInPlace).
    public int ChipEnergy(int amount)
    {
        int actual = Mathf.Min(_currentEnergyUnits, Mathf.Max(0, amount));
        if (actual <= 0) return 0;
        _currentEnergyUnits -= actual;

        // Lerp RGB toward gray as energy depletes, and drop alpha too.
        // At Charge01=1: full role color. At Charge01=0: gray at low alpha.
        Color gray = new Color(0.3f, 0.3f, 0.3f, 0f);
        Color full = _currentTint;
        full.a = 1f;
        Color drained = Color.Lerp(gray, full, Charge01);
        drained.a = Mathf.Lerp(0.05f, _currentTint.a, Charge01);

        _currentTint = drained;
        OnTintStateChanged?.Invoke(_currentTint);
        OnChargeChanged?.Invoke(Charge01);

        if (_currentEnergyUnits <= 0)
            SetTerrainColliderEnabled(false);

        return actual;
    }
}
