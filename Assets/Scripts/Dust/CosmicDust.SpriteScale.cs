using System.Collections;
using UnityEngine;

public partial class CosmicDust
{
    private Coroutine _spriteScaleRoutine;
    private float _cellWorldSize = 1f;
    private float _cellClearanceWorld = 0f;
    // The sprite animates to this scale (1.0 = exactly fills cell, >1.0 = overlap, <1.0 = gap).
    // Set by SetCellSizeDrivenScale via the generator's dustFootprintMul.
    private float _spriteScaleTarget = 1f;

    private IEnumerator ScaleSpriteRoutine(float from, float to, float seconds)
    {
        Vector3 a = Vector3.one * from;
        Vector3 b = Vector3.one * to;

        float t = 0f;
        seconds = Mathf.Max(0.01f, seconds);

        SetBaseSpriteScale(a);

        while (t < seconds)
        {
            t += Time.deltaTime;
            float u = Mathf.SmoothStep(0f, 1f, t / seconds);
            SetBaseSpriteScale(Vector3.Lerp(a, b, u));
            yield return null;
        }

        SetBaseSpriteScale(b);

        if (to > from) _growInOverride = -1f;
    }
// ------------- TIMING HELPERS -------------
    private float ResolveScaleSeconds(float from, float to, float seconds)
    {
        if (seconds > 0f) return seconds;

        // PASS 2: generator overrides regrow duration (bin-time radiance)
        if (to > from && _growInOverride > 0f)
            return Mathf.Max(0.01f, _growInOverride);

        return (to >= from) ? _timings.regrowSpriteScaleInSeconds : _timings.clearSpriteScaleOutSeconds;
    }
// ------------- SCALE -------------
    private void AnimateSpriteScale(float from, float to, float seconds = -1f)
    {
        float dur = ResolveScaleSeconds(from, to, seconds);

        if (_spriteScaleRoutine != null)
            StopCoroutine(_spriteScaleRoutine);

        _spriteScaleRoutine = StartCoroutine(ScaleSpriteRoutine(from, to, dur));
    }
    private void ResetSpriteScaleTo(float s)
    {
        if (visual.sprite == null) return;
        SetBaseSpriteScale(Vector3.one * s);
    }
    public void SetCellSizeDrivenScale(float cellWorldSize, float footprintMul = 1.15f, float clearanceWorld = 0f)
    {
        _cellWorldSize       = Mathf.Max(0.001f, cellWorldSize);
        _cellClearanceWorld  = Mathf.Max(0f, clearanceWorld);
        // footprintMul drives how large the sprite is relative to the cell tile.
        // 1.0 = exactly touches neighbour, >1.0 = overlaps, <1.0 = gap.
        _spriteScaleTarget   = Mathf.Max(0.01f, footprintMul);
        RebuildColliderForCurrentScale();
    }
    private void RebuildColliderForCurrentScale()
    {
        if (_box == null) return;

        float desiredWorld = Mathf.Clamp(_cellWorldSize - _cellClearanceWorld, 0.001f, _cellWorldSize);

        // The box collider must represent exactly one cell in physics space,
        // regardless of the sprite's visual footprint (dustFootprintMul).
        //
        // IMPORTANT: if the box is on the same transform as the sprite,
        // the sprite scale-in animation (to _spriteScaleTarget, typically 1.15)
        // will inflate the box's world footprint AFTER this method runs.
        // Compensate by dividing out the target sprite scale so the final
        // world size lands at exactly desiredWorld once the animation completes.
        float spriteScale = Mathf.Max(0.01f, _spriteScaleTarget);

        float sx = Mathf.Max(0.0001f, Mathf.Abs(transform.lossyScale.x));
        float sy = Mathf.Max(0.0001f, Mathf.Abs(transform.lossyScale.y));

        _box.size = new Vector2(
            desiredWorld / (sx * spriteScale),
            desiredWorld / (sy * spriteScale)
        );
        _box.offset = Vector2.zero;
    }
    private void SyncColliderRadiusToSprite()
    {
        // Rule #3: collider radius should match the size of the visual sprite.
        if (_circle == null || visual.sprite == null) return;

        // Sprite bounds are in world space and already include current transform scale.
        float worldRadius = Mathf.Max(0.0001f, Mathf.Max(visual.sprite.bounds.extents.x, visual.sprite.bounds.extents.y));

        // Convert world radius to collider-local radius (CircleCollider2D.radius is in the collider's local space).
        float sx = Mathf.Max(0.0001f, Mathf.Abs(_circle.transform.lossyScale.x));
        float sy = Mathf.Max(0.0001f, Mathf.Abs(_circle.transform.lossyScale.y));
        float lossy = Mathf.Max(sx, sy);

        _circle.radius = worldRadius / lossy;
        _circle.offset = Vector2.zero;
    }
}
