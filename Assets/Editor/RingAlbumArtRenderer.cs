using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// Renders a ring session as a square still image: the record as the PhaseLibrary carousel's
/// selected glyph draws it, with every ring at the same (highlighted) look and full-depth dips,
/// centred on black. Used by RingSessionManagerWindow's album-art export.
///
/// The glyph config and line material come from the carousel prefab, so the album art
/// follows any carousel tuning. Rendering happens in an isolated preview scene.
/// </summary>
public static class RingAlbumArtRenderer
{
    public const int DefaultSize = 1400;

    private const string CarouselPrefabPath = "Assets/Prefabs/UI/PhaseLibraryCarousel.prefab";

    // World radius every record's outer ring is laid out to. Line width (config.recordLineWidth)
    // is in world units, so this fixes the line weight relative to the image. It is about the
    // size of a typical selected record in the carousel; lower it for heavier lines.
    private const float StillRadius = 2.5f;

    /// <summary>PNG bytes for <paramref name="snap"/>, or null if it can't be rendered (reason logged).</summary>
    public static byte[] RenderPng(MotifSnapshot snap, int size = DefaultSize)
    {
        if (snap == null) return null;
        if (!TryLoadCarouselSettings(out var config, out var lineMaterial)) return null;

        Scene scene = EditorSceneManager.NewPreviewScene();
        RenderTexture rt = null, resolved = null;
        Texture2D tex = null;
        var bakedLineMeshes = new List<Mesh>();
        GameObject glyphGo = null;
        try
        {
            glyphGo = new GameObject("AlbumArtGlyph");
            SceneManager.MoveGameObjectToScene(glyphGo, scene);
            var glyph = glyphGo.AddComponent<MotifRingGlyphApplicator>();
            glyph.config       = config;
            glyph.lineMaterial = lineMaterial;
            glyph.ApplyStill(snap, StillRadius);

            // Half a pixel of slack so the outer line's antialiased edge isn't clipped.
            float frame = glyph.StillFrameRadius;
            float radius = frame * (1f + 1f / size);
            if (radius <= 0f)
            {
                Debug.LogWarning($"[AlbumArt] Ring {snap.PhaseIndex}_{snap.MotifIndex} has no rings to draw.");
                return null;
            }

            var camGo = new GameObject("AlbumArtCamera");
            SceneManager.MoveGameObjectToScene(camGo, scene);
            camGo.transform.position = new Vector3(0f, 0f, -10f);
            var cam = camGo.AddComponent<Camera>();
            cam.scene            = scene;
            cam.orthographic     = true;
            cam.orthographicSize = radius;
            cam.aspect           = 1f;
            cam.clearFlags       = CameraClearFlags.SolidColor;
            cam.backgroundColor  = Color.black;
            cam.nearClipPlane    = 0.01f;
            cam.farClipPlane     = 100f;
            cam.enabled          = false;

            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            var desc = new RenderTextureDescriptor(size, size, RenderTextureFormat.ARGB32, 24)
            {
                msaaSamples = 8,
                sRGB        = linear,
            };
            rt = new RenderTexture(desc);
            rt.Create();

            // Camera.Render() through the URP 2D renderer comes out black for a preview scene, so the
            // ring meshes are drawn straight into the texture instead. Both ring shaders are unlit
            // (RingUnlit / the fill graph), so no pipeline lighting is lost.
            DrawGlyph(glyphGo, cam, rt, bakedLineMeshes);

            // Resolve MSAA into a plain texture before reading it back.
            resolved = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32,
                linear ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Default);
            Graphics.Blit(rt, resolved);

            var prev = RenderTexture.active;
            RenderTexture.active = resolved;
            tex = new Texture2D(size, size, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;

            return tex.EncodeToPNG();
        }
        finally
        {
            // Fill meshes are owned by the glyph and outlive its GameObjects.
            if (glyphGo != null)
                foreach (var mf in glyphGo.GetComponentsInChildren<MeshFilter>(true))
                    if (mf.sharedMesh != null) Object.DestroyImmediate(mf.sharedMesh);
            foreach (var m in bakedLineMeshes) if (m != null) Object.DestroyImmediate(m);
            if (tex != null) Object.DestroyImmediate(tex);
            if (resolved != null) RenderTexture.ReleaseTemporary(resolved);
            if (rt != null) { rt.Release(); Object.DestroyImmediate(rt); }
            EditorSceneManager.ClosePreviewScene(scene);
        }
    }

    // Draws every fill and contour under the glyph into `target`, back to front by sortingOrder
    // (fills before contours on a tie), as the 2D renderer would.
    private static void DrawGlyph(GameObject glyphGo, Camera cam, RenderTexture target, List<Mesh> bakedLineMeshes)
    {
        var draws = new List<(int order, int kind, Mesh mesh, Matrix4x4 matrix, Material mat, MaterialPropertyBlock mpb)>();

        foreach (var mr in glyphGo.GetComponentsInChildren<MeshRenderer>(true))
        {
            var mf = mr.GetComponent<MeshFilter>();
            if (!mr.enabled || mf == null || mf.sharedMesh == null || mr.sharedMaterial == null) continue;
            var mpb = new MaterialPropertyBlock();
            mr.GetPropertyBlock(mpb);
            draws.Add((mr.sortingOrder, 0, mf.sharedMesh, mr.transform.localToWorldMatrix, mr.sharedMaterial, mpb));
        }

        foreach (var lr in glyphGo.GetComponentsInChildren<LineRenderer>(true))
        {
            if (!lr.enabled || lr.positionCount < 2 || lr.sharedMaterial == null) continue;
            var mesh = new Mesh { name = "AlbumArtLine" };
            lr.BakeMesh(mesh, cam, true); // world space, vertex colours included
            bakedLineMeshes.Add(mesh);
            var mpb = new MaterialPropertyBlock();
            lr.GetPropertyBlock(mpb);
            draws.Add((lr.sortingOrder, 1, mesh, Matrix4x4.identity, lr.sharedMaterial, mpb));
        }

        var sorted = draws.OrderBy(d => d.order).ThenBy(d => d.kind).ToList();
        if (sorted.Count == 0) Debug.LogWarning("[AlbumArt] The glyph built no drawable rings.");

        var cmd = new CommandBuffer { name = "AlbumArt" };
        cmd.SetRenderTarget(target);
        cmd.ClearRenderTarget(true, true, Color.black);
        cmd.SetViewProjectionMatrices(cam.worldToCameraMatrix, cam.projectionMatrix);
        foreach (var d in sorted)
        {
            int pass = d.mat.FindPass("Universal2D");
            if (pass < 0) pass = 0;
            cmd.DrawMesh(d.mesh, d.matrix, d.mat, 0, pass, d.mpb);
        }
        Graphics.ExecuteCommandBuffer(cmd);
        cmd.Release();
    }

    private static bool TryLoadCarouselSettings(out RingGlyphConfig config, out Material lineMaterial)
    {
        config = null; lineMaterial = null;

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(CarouselPrefabPath);
        var carousel = prefab != null ? prefab.GetComponentInChildren<PhaseLibraryCarousel>(true) : null;
        if (carousel == null)
        {
            Debug.LogError($"[AlbumArt] No PhaseLibraryCarousel found in {CarouselPrefabPath}.");
            return false;
        }

        var so = new SerializedObject(carousel);
        config = so.FindProperty("selectedGlyphConfig").objectReferenceValue as RingGlyphConfig;

        var slots = so.FindProperty("slots");
        if (slots != null && slots.arraySize > 0
            && slots.GetArrayElementAtIndex(0).objectReferenceValue is MotifRingGlyphApplicator slot)
        {
            lineMaterial = slot.lineMaterial;
            if (config == null) config = slot.config;
        }

        if (config == null)
        {
            Debug.LogError("[AlbumArt] The carousel prefab has no selected glyph config.");
            return false;
        }
        return true;
    }
}
