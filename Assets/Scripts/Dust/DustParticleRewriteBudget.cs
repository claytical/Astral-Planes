using UnityEngine;

// =========================================================================
//  DustParticleRewriteBudget
//
//  Shared per-frame budget for rewriting a dust cell's LIVE particles
//  (ParticleSystem.GetParticles/SetParticles) from animation loops: tint
//  pulses, colour flashes (bass-drop shockwave, block shimmer, scouting
//  preview), block-form swell, regrow fade-in. Each of those used to rewrite
//  every particle of every animating cell every frame, so a maze-wide event
//  (ring completion → hundreds of blocks, bass drop → every solid cell)
//  produced a multi-frame spike proportional to the maze size.
//
//  Only INTERMEDIATE frames of an animation ask the budget. The final value
//  of every animation is always written (callers bypass the budget), so a
//  throttled cell never ends stuck mid-colour; it just animates at a lower
//  frame rate while the maze is busy. main.startColor / startSize (future
//  emissions) is always written — it's cheap — only the live rewrite is gated.
//
//  Fairness: when last frame's demand exceeded the cap, each cell is allowed
//  only on frames where (frame + phase) % stride == 0, so the load spreads
//  evenly across cells instead of the same first-N cells always winning.
//  A hard cap still bounds a sudden first-frame burst.
// =========================================================================
public static class DustParticleRewriteBudget
{
    /// <summary>Target live-particle rewrites per frame across all dust cells.</summary>
    public static int TargetPerFrame = 48;

    private static int _frame = -1;
    private static int _requestsThisFrame;
    private static int _grantsThisFrame;
    private static int _requestsLastFrame;

    /// <param name="phase">A stable per-cell value (e.g. instance id) that spreads cells across frames.</param>
    public static bool TryRewrite(int phase)
    {
        int f = Time.frameCount;
        if (f != _frame)
        {
            _requestsLastFrame = _requestsThisFrame;
            _requestsThisFrame = 0;
            _grantsThisFrame = 0;
            _frame = f;
        }
        _requestsThisFrame++;

        int target = Mathf.Max(1, TargetPerFrame);
        if (_grantsThisFrame >= target + target / 2) return false; // hard cap for burst frames

        int stride = Mathf.Max(1, (_requestsLastFrame + target - 1) / target);
        if (stride > 1 && ((f + (phase & 0x7fffffff)) % stride) != 0) return false;

        _grantsThisFrame++;
        return true;
    }
}
