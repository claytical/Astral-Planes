using UnityEngine;

/// <summary>
/// Shared runtime fallback materials. Code that builds LineRenderers / particle renderers at runtime
/// used to call <c>new Material(Shader.Find("Sprites/Default"))</c> per instance: a shader lookup
/// plus a material that was never destroyed, once per tentacle / tether / shimmer. Colour on those
/// renderers comes from vertex colour (LineRenderer gradients, particle startColor), so they can
/// all share one material. Don't mutate it — callers that need per-instance material properties
/// (e.g. material.color) must create their own.
/// </summary>
public static class SharedMaterials
{
    private static Material _spriteDefault;

    public static Material SpriteDefault
    {
        get
        {
            if (_spriteDefault == null)
                _spriteDefault = new Material(Shader.Find("Sprites/Default")) { name = "Shared Sprites/Default (runtime)" };
            return _spriteDefault;
        }
    }
}
