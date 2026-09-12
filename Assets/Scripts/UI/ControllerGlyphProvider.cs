using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

// Resolves the "Select"/View/Share/Minus glyph for whatever controller is currently connected.
// Detection order: Steam Input (when available) for real per-controller glyph art, falling
// back to PlayerInput's currentControlScheme (already brand-named in AstralPlanes.inputactions)
// when Steam Input isn't initialized, falling back to a hand-authored sprite per brand as a
// last resort. Singleton, co-located on the SteamManager prefab alongside SteamManager /
// SteamStatsManager / SteamInputManager so any scene can reach it via Instance.
public class ControllerGlyphProvider : MonoBehaviour
{
    public static ControllerGlyphProvider Instance { get; private set; }

    [System.Serializable]
    private struct BrandGlyph
    {
        public ControllerBrand brand;
        public Sprite sprite;
    }

    [Tooltip("Used when Steam Input is unavailable. One entry per controller brand; Unknown falls back to defaultFallback.")]
    [SerializeField] private BrandGlyph[] fallbackGlyphsByBrand;
    [SerializeField] private Sprite defaultFallback;

    // Keyed by absolute PNG path, so a controller-type change invalidates naturally via cache miss.
    private readonly System.Collections.Generic.Dictionary<string, Sprite> _cache = new();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }

    public Sprite GetOpenSettingsGlyph()
    {
        if (SteamInputManager.Initialized && SteamInputManager.Instance != null)
        {
            string path = SteamInputManager.Instance.GetGlyphPngPath("OpenSettings");
            if (!string.IsNullOrEmpty(path))
            {
                var sprite = LoadSpriteFromDisk(path);
                if (sprite != null) return sprite;
            }
        }

        return FallbackFor(GetActiveControllerBrand());
    }

    public ControllerBrand GetActiveControllerBrand()
    {
        if (SteamInputManager.Initialized && SteamInputManager.Instance != null)
        {
            var brand = SteamInputManager.Instance.GetPrimaryControllerBrand();
            if (brand != ControllerBrand.Unknown) return brand;
        }

        return GetControllerBrandFromPlayerInput();
    }

    private static ControllerBrand GetControllerBrandFromPlayerInput()
    {
        var players = GameFlowManager.Instance != null ? GameFlowManager.Instance.localPlayers : null;
        if (players == null) return ControllerBrand.Unknown;

        foreach (var player in players)
        {
            if (player == null) continue;
            var playerInput = player.GetComponent<PlayerInput>();
            if (playerInput == null) continue;

            switch (playerInput.currentControlScheme)
            {
                case "Xbox": return ControllerBrand.Xbox;
                case "Playstation": return ControllerBrand.PlayStation;
                case "Switch": return ControllerBrand.Switch;
                case "Generic Gamepad": return ControllerBrand.Generic;
            }
        }

        return ControllerBrand.Unknown;
    }

    private Sprite LoadSpriteFromDisk(string path)
    {
        if (_cache.TryGetValue(path, out var cached)) return cached;
        if (!File.Exists(path)) return null;

        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(bytes)) return null;

            var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
            _cache[path] = sprite;
            return sprite;
        }
        catch (System.Exception e)
        {
            if (GameFlowManager.VerboseLogging) Debug.LogWarning($"[ControllerGlyphProvider] Failed to load glyph at '{path}': {e.Message}");
            return null;
        }
    }

    private Sprite FallbackFor(ControllerBrand brand)
    {
        if (fallbackGlyphsByBrand != null)
        {
            foreach (var entry in fallbackGlyphsByBrand)
                if (entry.brand == brand) return entry.sprite;
        }
        return defaultFallback;
    }
}
