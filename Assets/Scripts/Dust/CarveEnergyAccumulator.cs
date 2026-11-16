using UnityEngine;

// Pooled, weighted, leaky accumulator of recent vehicle carves — the drum intensity throttle
// (see DrumTrack.BeatIntensity.cs). Every carve adds its role's carve weight
// (DrumIntensityTuning.CarveWeight); the pool decays exponentially with the caller-supplied
// half-life, so the beat drains back down when the player stops carving. Decay is applied
// lazily on Register/Get, so there's no per-frame cost.
public sealed class CarveEnergyAccumulator
{
    private float _acc;
    private float _lastTime;
    private float _halfLife = 3f; // last half-life a reader asked for; Register decays with it

    public void Register(float weight)
    {
        if (weight <= 0f) return;
        float now = Time.time;
        _acc = Decayed(now) + weight;
        _lastTime = now;
    }

    // 0..1: 1 - exp(-acc / saturation). With saturation = N, about N weight-1 carves inside
    // one half-life reach ~63%.
    public float GetEnergy01(float halfLifeSeconds, float saturation)
    {
        _halfLife = Mathf.Max(0.01f, halfLifeSeconds);
        float now = Time.time;
        _acc = Decayed(now);
        _lastTime = now;
        return Mathf.Clamp01(1f - Mathf.Exp(-_acc / Mathf.Max(0.01f, saturation)));
    }

    public void Reset()
    {
        _acc = 0f;
        _lastTime = Time.time;
    }

    private float Decayed(float now)
    {
        float dt = Mathf.Max(0f, now - _lastTime);
        return _acc * Mathf.Exp(-0.6931472f * dt / _halfLife);
    }
}
