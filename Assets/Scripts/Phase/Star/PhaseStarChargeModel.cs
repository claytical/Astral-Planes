using System.Collections.Generic;
using UnityEngine;

public interface IPhaseStarChargeModel
{
    void AddCharge(MusicalRole role, float energyUnitsDelivered, float dustToStarChargeMul, MusicalRole attunedRole);
    float GetTotalCharge();
    float GetChargeNormalized01(MusicalRole role);
    float GetRoleHunger(MusicalRole role);
    bool TryGetDominantRole(float threshold, out MusicalRole role, out float rawCharge);
}

public sealed class PhaseStarChargeModel : IPhaseStarChargeModel
{
    private readonly Dictionary<MusicalRole, float> _starCharge;

    public PhaseStarChargeModel(Dictionary<MusicalRole, float> starCharge) => _starCharge = starCharge;

    public void AddCharge(MusicalRole role, float energyUnitsDelivered, float dustToStarChargeMul, MusicalRole attunedRole)
    {
        if (role == MusicalRole.None) return;
        if (attunedRole != MusicalRole.None && role != attunedRole) return;
        float add = Mathf.Max(0f, energyUnitsDelivered) * dustToStarChargeMul;
        if (add <= 0f) return;
        float fieldAvg = (_starCharge.Count > 0) ? GetTotalCharge() / _starCharge.Count : 0f;
        _starCharge.TryGetValue(role, out float cur);
        if (cur > fieldAvg * 1.5f) add *= 0.5f;
        _starCharge[role] = cur + add;
    }

    public float GetTotalCharge() { float total = 0f; foreach (var kv in _starCharge) total += kv.Value; return total; }
    public float GetChargeNormalized01(MusicalRole role) => _starCharge.TryGetValue(role, out var c) ? Mathf.Clamp01(c) : 0f;
    public float GetRoleHunger(MusicalRole role) => 1f - GetChargeNormalized01(role);
    public bool TryGetDominantRole(float threshold, out MusicalRole role, out float rawCharge)
    {
        role = MusicalRole.None; rawCharge = 0f;
        foreach (var kv in _starCharge)
            if (kv.Value > rawCharge) { role = kv.Key; rawCharge = kv.Value; }
        return role != MusicalRole.None && rawCharge >= threshold;
    }
}
