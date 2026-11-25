using UnityEngine;

// In-world count tell: a star is a tangle of tendrils, one per note it still needs (one per
// flower it must drain). Each tendril is an idle tentacle's own LineRenderer; when a flower is
// available the tendril pointing closest to it grows out as the vine, and after the zap that
// tentacle stops counting, so the tendrils tick down one per note while every second note forms
// a diamond in the body (PhaseStar.DiamondBody.cs). Visible for the star's whole hungry life.
public partial class PhaseStarDustAffect
{
    private static readonly GradientColorKey[] s_budColorKeys = new GradientColorKey[2];
    private static readonly GradientAlphaKey[] s_budAlphaKeys = new GradientAlphaKey[2];

    private const int kBudPoints = 10;
    private float _budSpin;

    // Vine taper (thin root, full body, pointed tip) and the idle tendril's (full root, tapering).
    private static readonly AnimationCurve VineWidthCurve = new AnimationCurve(
        new Keyframe(0f, 0.3f), new Keyframe(0.2f, 1.0f), new Keyframe(0.65f, 0.9f), new Keyframe(1f, 0.0f));
    private static readonly AnimationCurve BudWidthCurve = new AnimationCurve(
        new Keyframe(0f, 1.0f), new Keyframe(0.6f, 0.7f), new Keyframe(1f, 0.25f));

    // Tutorial callout ("These are discoveries."): the tendrils brighten, thicken and pulse.
    private bool _budHighlightTarget;
    private float _budHighlight01;

    public void SetBudHighlight(bool on) => _budHighlightTarget = on;

    /// <summary>World distance from the star's centre to its idle tendril tips.</summary>
    public float TendrilReachWorld()
    {
        float cell = CellWorld(_gfm != null ? _gfm.activeDrumTrack : GameFlowManager.Instance?.activeDrumTrack);
        return (Cfg.budRootOffsetCells + Cfg.budLengthCells) * cell;
    }

    private int BudsWanted()
    {
        if (_attunedRole == MusicalRole.None || _star == null || !_star.ShowsHungerTendrils)
            return 0;
        int wanted = _star.RemainingZapCount - CountZapBudgetInFlight();
        return Mathf.Clamp(wanted, 0, Mathf.Max(1, Cfg.budMaxShown));
    }

    private void TickBuds(float dt)
    {
        int wanted = BudsWanted();
        int idle = 0;
        foreach (var t in _tentacles)
            if (t.state == TentacleState.Idle && t.role == _attunedRole) idle++;
        int shown = Mathf.Min(wanted, idle);

        _budSpin += dt * Cfg.budSpinDegreesPerSecond;
        _budHighlight01 = Mathf.MoveTowards(_budHighlight01, _budHighlightTarget ? 1f : 0f, dt / 0.4f);
        Vector2 starPos = transform.position;
        float cell = CellWorld(_gfm != null ? _gfm.activeDrumTrack : GameFlowManager.Instance?.activeDrumTrack);

        int slot = 0;
        foreach (var t in _tentacles)
        {
            bool isBud = t.state == TentacleState.Idle && t.role == _attunedRole && slot < shown;
            if (!isBud)
            {
                if (t.budVisible) HideBud(t);
                continue;
            }

            float targetAngle = _budSpin + 360f * slot / Mathf.Max(1, shown);
            if (!t.budVisible)
            {
                t.budAngle = targetAngle;
                t.budVisible = true;
                t.budGrow01 = 0f;
                t.lastBudColor = new Color(-1f, -1f, -1f, -1f);
                t.lastBudAlpha = -1f;
            }
            else
            {
                // Re-space smoothly when the count changes instead of snapping.
                t.budAngle = Mathf.LerpAngle(t.budAngle, targetAngle, 1f - Mathf.Exp(-6f * dt));
            }
            t.budGrow01 = Mathf.MoveTowards(t.budGrow01, 1f, dt / 0.35f);
            DrawBud(t, starPos, cell);
            slot++;
        }
    }

    private void DrawBud(Tentacle t, Vector2 starPos, float cell)
    {
        var line = t.line;
        if (line == null) return;

        float grow = Mathf.SmoothStep(0f, 1f, t.budGrow01);
        float root = Cfg.budRootOffsetCells * cell;
        float len  = Cfg.budLengthCells * cell * grow;
        float rad  = t.budAngle * Mathf.Deg2Rad;
        Vector2 dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
        float z = transform.position.z;
        float time = Time.time * Mathf.Max(0.01f, noiseSpeed) * 2f + t.flowOffset * 10f;
        float curl = Cfg.budCurlAmplitude * cell * grow;
        float sway = Cfg.budWiggleAmplitude * cell;

        // Swim personality (PhaseStarMotion2D.Swim.cs): pull in on the stroke, stream behind
        // while gliding, lean toward a curious ship, flick on the beat. The tutorial callout
        // keeps the tendrils fully out so they stay readable.
        var motion = _star != null ? _star.Motion : null;
        if (motion != null)
        {
            float calm = 1f - _budHighlight01;
            float pull = motion.TendrilPullIn01 * calm;
            len  *= 1f - pull;
            curl *= 1f + 1.5f * pull;

            float trail = motion.Trail01 * calm;
            if (trail > 0f)
            {
                Vector2 bent = Vector2.Lerp(dir, motion.TrailDir, trail);
                if (bent.sqrMagnitude > 0.0001f) dir = bent.normalized;
            }

            float lean = motion.ShipLean01 * Cfg.swim.curiousTendrilLean;
            if (lean > 0f)
            {
                // Tendrils already facing the ship reach for it most.
                float facing = 0.5f + 0.5f * Vector2.Dot(dir, motion.ShipLeanDir);
                Vector2 bent = Vector2.Lerp(dir, motion.ShipLeanDir, lean * facing);
                if (bent.sqrMagnitude > 0.0001f) dir = bent.normalized;
            }

            sway += motion.BeatFlick01 * Cfg.swim.beatFlickAmount * Cfg.budWiggleAmplitude * cell;
        }
        Vector2 nrm = new Vector2(-dir.y, dir.x);
        // Each tendril curls its own way (and slowly breathes) so the tangle doesn't look combed.
        float curlSign = (t.lineIndexInRole & 1) == 0 ? 1f : -1f;
        float breathe = 0.75f + 0.25f * Mathf.Sin(time * 0.35f + t.flowOffset * 3f);

        line.positionCount = kBudPoints;
        for (int i = 0; i < kBudPoints; i++)
        {
            float u = i / (float)(kBudPoints - 1);
            // Curl bends harder toward the tip; the sway travels outward along it.
            float bend = curlSign * curl * breathe * u * u;
            float wave = Mathf.Sin(time + u * 4f) * sway * u;
            Vector2 p = starPos + dir * (root + len * u) + nrm * (bend + wave);
            line.SetPosition(i, new Vector3(p.x, p.y, z));
        }

        // Highlight: a gentle pulse between a lift and the full callout.
        float h = 0f;
        if (_budHighlight01 > 0f)
        {
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * Mathf.PI * 2f * Mathf.Max(0.01f, Cfg.budHighlightPulseHz));
            h = _budHighlight01 * Mathf.Lerp(0.55f, 1f, pulse);
        }
        float a = Mathf.Lerp(Cfg.budAlpha, Cfg.budHighlightAlpha, h);
        if (_star != null && _star.IsDimmedVisual) a *= Cfg.budDimmedAlphaMul;

        // The callout also lifts the colour toward white at its peak, so it reads at a glance.
        Color rc = Color.Lerp(GetRoleColor(t.role), Color.white, 0.45f * h);
        if (rc != t.lastBudColor || !Mathf.Approximately(a, t.lastBudAlpha))
        {
            t.lastBudColor = rc;
            t.lastBudAlpha = a;
            // Scratch key arrays: SetKeys copies them, and the highlight pulse changes rc/a every frame.
            s_budColorKeys[0] = new GradientColorKey(rc, 0f);
            s_budColorKeys[1] = new GradientColorKey(rc, 1f);
            s_budAlphaKeys[0] = new GradientAlphaKey(a, 0f);
            s_budAlphaKeys[1] = new GradientAlphaKey(a * 0.6f, 1f);
            t.gradient.SetKeys(s_budColorKeys, s_budAlphaKeys);
            line.colorGradient = t.gradient;
            // Bud gradient overwrote the shared gradient; force the growing gradient to rebuild.
            t.lastGrowingGradientAlphaScale = float.NaN;
        }

        if (!t.budCurveApplied) { line.widthCurve = BudWidthCurve; t.budCurveApplied = true; }
        line.widthMultiplier = Cfg.budWidthWorld * Mathf.Lerp(1f, Cfg.budHighlightWidthMul, h);
        line.enabled = true;
    }

    private void HideBud(Tentacle t)
    {
        t.budVisible = false;
        t.budGrow01 = 0f;
        if (t.budCurveApplied && t.line != null) t.line.widthCurve = VineWidthCurve;
        t.budCurveApplied = false;
        // A bud that became a vine keeps its line (the vine owns it now).
        if (t.state == TentacleState.Idle && t.line != null)
        {
            t.line.enabled = false;
            t.line.widthMultiplier = tentacleWidth;
        }
    }

    private void HideAllBuds()
    {
        foreach (var t in _tentacles)
            if (t.budVisible) HideBud(t);
    }
}
