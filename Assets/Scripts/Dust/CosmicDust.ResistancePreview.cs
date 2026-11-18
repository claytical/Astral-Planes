using UnityEngine;

public partial class CosmicDust
{
    // Non-destructive preview cue for Vehicle's scouting telegraph (see Vehicle.Plow.cs
    // UpdateScoutingPreview): briefly pulses the cell's own role color brighter — more resistant
    // cells pulse brighter — so players can see a route's cost before contact instead of
    // discovering it by draining fuel. It never touches _currentTint/cell state.
    // The scouting telegraph calls this every physics tick (50 Hz) for every cell in a ship's
    // footprint. Restarting the pulse each tick allocated a coroutine and rewrote the cell's
    // particles constantly; instead, let an in-flight preview pulse run until it's mostly done.
    private int _previewPulseToken = -1;
    private float _previewPulseStartTime;

    public void PreviewResistance(float resistance01, float pulseSeconds = 0.18f)
    {
        float r = Mathf.Clamp01(resistance01);
        if (r <= 0f || pulseSeconds <= 0f) return;

        if (_tintPulseRoutine != null && _previewPulseToken == _tintPulseToken
            && Time.time - _previewPulseStartTime < pulseSeconds * 0.6f)
            return;

        Color.RGBToHSV(_currentTint, out float h, out float s, out float v);
        Color previewTint = Color.HSVToRGB(h, s, Mathf.Clamp01(v + r * 0.5f));
        previewTint.a = _currentTint.a;

        CancelTintPulse(restoreToBase: false);
        _tintPulseToken++;
        int token = _tintPulseToken;
        _previewPulseToken = token;
        _previewPulseStartTime = Time.time;
        _tintPulseRoutine = StartCoroutine(TintPulseRoutine(token, previewTint, pulseSeconds));
    }
}
