using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

// Resolves the "Select"/View/Share/Minus glyph for a controller (a given pad, the first joined
// player's, or the first enabled gamepad). Detection order: Steam Input's glyph PNG for the
// button actually bound to the "Menu" action on that controller, falling back to a
// hand-authored sprite per brand (brand from the device type, see SteamInputManager.BrandOf). Singleton, co-located on the SteamManager prefab alongside SteamManager /
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

    /// <summary>
    /// Glyph for the Select/View/Share/Minus ("Menu") button on <paramref name="pad"/>, or on the
    /// first joined player's / first enabled gamepad when null.
    /// </summary>
    public Sprite GetOpenSettingsGlyph(Gamepad pad = null)
    {
        pad ??= ResolvePad();

        if (SteamInputManager.Initialized && SteamInputManager.Instance != null)
        {
            string path = SteamInputManager.Instance.GetGlyphPngPath(pad, SteamInputManager.ActionMenu);
            if (!string.IsNullOrEmpty(path))
            {
                var sprite = LoadSpriteFromDisk(path);
                if (sprite != null) return sprite;
            }
        }

        return FallbackFor(GetActiveControllerBrand(pad));
    }

    public ControllerBrand GetActiveControllerBrand(Gamepad pad = null)
    {
        pad ??= ResolvePad();
        if (pad != null) return SteamInputManager.BrandOf(pad);

        if (SteamInputManager.Initialized && SteamInputManager.Instance != null)
            return SteamInputManager.Instance.GetControllerBrand();

        return ControllerBrand.Unknown;
    }

    // First joined player's gamepad, else the first enabled gamepad (disabled natives are
    // skipped, so under Steam Input this is a SteamInputGamepad).
    private static Gamepad ResolvePad()
    {
        var players = GameFlowManager.Instance != null ? GameFlowManager.Instance.localPlayers : null;
        if (players != null)
        {
            foreach (var player in players)
            {
                if (player == null) continue;
                var playerInput = player.GetComponent<PlayerInput>();
                if (playerInput == null) continue;
                foreach (var device in playerInput.devices)
                    if (device is Gamepad gp && gp.enabled) return gp;
            }
        }

        foreach (var gp in Gamepad.all)
            if (gp.enabled) return gp;
        return null;
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
