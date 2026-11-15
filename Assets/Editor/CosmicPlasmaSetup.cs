#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// One-shot builder for the "Cosmic Plasma" dust look:
///   Assets/Materials/CosmicPlasma.mat  (Astral/CosmicPlasmaParticle shader)
///   Assets/Prefabs/Interactable Objects/Cosmic Plasma.prefab  (variant of Cosmic Potential)
/// Menu: Tools/Astral Planes/Dust/Build Cosmic Plasma Variant. Safe to re-run: it retunes the existing assets.
/// Scene wiring (CosmicDustGenerator.dustPrefab) is intentionally left to the user for A/B comparison.
/// </summary>
public static class CosmicPlasmaSetup
{
    const string ShaderName   = "Astral/CosmicPlasmaParticle";
    const string MaterialPath = "Assets/Materials/CosmicPlasma.mat";
    const string BasePrefab   = "Assets/Prefabs/Interactable Objects/Cosmic Potential.prefab";
    const string VariantPath  = "Assets/Prefabs/Interactable Objects/Cosmic Plasma.prefab";

    [MenuItem("Tools/Astral Planes/Dust/Build Cosmic Plasma Variant")]
    public static void Build()
    {
        var shader = Shader.Find(ShaderName);
        if (shader == null)
        {
            Debug.LogError($"[CosmicPlasma] Shader '{ShaderName}' not found. Let Unity finish importing Assets/Shaders/CosmicPlasmaParticle.shader and check the console for compile errors.");
            return;
        }

        var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (mat == null)
        {
            mat = new Material(shader) { name = "CosmicPlasma" };
            AssetDatabase.CreateAsset(mat, MaterialPath);
        }
        else mat.shader = shader;

        var basePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(BasePrefab);
        if (basePrefab == null)
        {
            Debug.LogError($"[CosmicPlasma] Base prefab not found at {BasePrefab}");
            return;
        }

        GameObject variant;
        if (AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath) == null)
        {
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab);
            variant = PrefabUtility.SaveAsPrefabAsset(inst, VariantPath); // saves as a prefab variant of the base
            Object.DestroyImmediate(inst);
        }
        else variant = AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath);

        var root = PrefabUtility.LoadPrefabContents(VariantPath);
        try
        {
            var particles = root.transform.Find("Particles");
            var ps = particles != null ? particles.GetComponent<ParticleSystem>() : root.GetComponentInChildren<ParticleSystem>();
            if (ps == null) { Debug.LogError("[CosmicPlasma] No ParticleSystem found in variant."); return; }

            var psr = ps.GetComponent<ParticleSystemRenderer>();
            psr.sharedMaterial = mat;
            psr.renderMode = ParticleSystemRenderMode.Billboard;

            // ONE stationary field kernel per cell, centered, so the visible surface is deterministic and matches the
            // cell collider. (Random particles per cell made some solid cells invisible and others bleed into open space.)
            var main = ps.main;
            main.maxParticles = 1;
            // The base prefab has startSpeed 0.1 and gravityModifier 0.001. Harmless for 2-5 s particles, but this particle lives
            // "forever", so it would drift many cells away from its collider. It must be perfectly stationary.
            main.startSpeed = 0f;
            main.gravityModifier = 0f;
            main.startLifetime = 100000f;
            main.startSize = 1f; // overwritten at runtime by CosmicDust.ApplyCellRelativeParticleSizing (must stay constant)
            // keep the prefab's startColor (CosmicDust captures _baseAlpha from it)

            var emission = ps.emission;
            emission.rateOverTime = 20f; // refills the single slot immediately; CosmicDust's emission multiplier still scales/zeroes it

            var shape = ps.shape;
            shape.radius = 0f;
            shape.randomPositionAmount = 0f;

            var dust = root.GetComponent<CosmicDust>();
            if (dust != null)
            {
                dust.visual.particleCellFraction = 1.8f; // kernel diameter = 1.8 tiles (radius 0.9): see plan math; threshold ~0.45 puts the edge on the cell boundary
                EditorUtility.SetDirty(dust);
            }

            // Always simulate: don't let Unity pause/cull a cell's single particle (a paused/unspawned cell is an invisible wall).
            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;

            // The nose squash/offset deforms the Particles child (up to 28% scale + 0.2 world shift) whenever the ship presses on
            // a cell, so the surface visibly retreats from the collider. The plasma surface must stay put.
            if (dust != null)
            {
                var so = new SerializedObject(dust);
                var nc = so.FindProperty("noseCompression.enabled");
                if (nc != null) { nc.boolValue = false; so.ApplyModifiedPropertiesWithoutUndo(); }
                else Debug.LogWarning("[CosmicPlasma] Could not find noseCompression.enabled on CosmicDust.");
            }

            mat.SetFloat("_FieldGain", 1f);
            mat.SetFloat("_NoiseStrength", 0.08f);

            var sol = ps.sizeOverLifetime;
            sol.enabled = false;
            var noise = ps.noise;
            noise.enabled = false;
            var vel = ps.velocityOverLifetime;
            vel.enabled = false;

            PrefabUtility.SaveAsPrefabAsset(root, VariantPath);
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath);
        Debug.Log("[CosmicPlasma] Built material + variant. ALSO add Cosmic Plasma Feature to the 2D URP Renderer asset (visible surface comes from it). To A/B: assign 'Cosmic Plasma' to CosmicDustGenerator.dustPrefab in GeneratedTrack.");
    }
}
#endif
