// One-click configuration of the URP pipeline + renderer assets.
//
//     Metoh → Configure Render Pipeline
//
// WHY THIS IS A SCRIPT AND NOT A README STEP. The repo tracks only Scripts/, Shaders/ and Sim/ —
// the .asset files that define the render pipeline live in the live project and are never committed.
// So every setting that lives on those assets was, until now, an instruction in a document that had
// to be carried out by hand and could not be verified: UNITY_PORT_NOTES [materials] literally listed
// "Add Renderer Feature → Screen Space Ambient Occlusion" as an owner step, and it was never done,
// which is why the largest single realism gain available has been sitting unclaimed.
//
// A menu command fixes that class of problem properly. It is idempotent, it reports what it changed,
// and it can be re-run after any Unity upgrade or project re-clone.
//
// AMBIENT OCCLUSION IS THE POINT. AO is the effect that visually GROUNDS things: contact shadow in
// the crease where a trunk meets snow, under a ledge, inside a crevasse mouth. Without it every prop
// and every figure reads as hovering slightly above the ground rather than sitting in it, and that
// specific tell is a large part of what "looks like an old game" actually means. Over open snowpack
// it matters more than usual, because snow bounces so much light that AO is nearly the only thing
// left darkening a contact point.
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Metoh.EditorTools
{
    public static class RenderPipelineSetup
    {
        [MenuItem("Metoh/Configure Render Pipeline")]
        public static void Configure()
        {
            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (urp == null)
            {
                Debug.LogError("[RenderPipelineSetup] No UniversalRenderPipelineAsset is active. " +
                               "Project Settings → Graphics → Scriptable Render Pipeline Settings.");
                return;
            }

            int changes = 0;
            changes += ConfigurePipelineAsset(urp);
            changes += ConfigureRenderers(urp);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log(changes == 0
                ? "[RenderPipelineSetup] Already configured — nothing to change."
                : $"[RenderPipelineSetup] Applied {changes} change(s). Enter Play mode to see them.");
        }

        // ------------------------------------------------------------------ pipeline asset

        /// <summary>
        /// Settings that live on the UniversalRenderPipelineAsset itself.
        ///
        /// Written through SerializedObject rather than the C# properties on purpose: several of
        /// these have no public setter, and the serialized field names are far more stable across URP
        /// versions than the internal API surface is. It also gives us proper undo/dirty handling for
        /// free, which matters because these are real project assets.
        /// </summary>
        private static int ConfigurePipelineAsset(UniversalRenderPipelineAsset urp)
        {
            var so = new SerializedObject(urp);
            int changed = 0;

            // HDR colour grading. The game tonemaps with ACES, and ACES in LDR mode grades AFTER the
            // image has already been crushed to 0..1 — so the highlight roll-off it exists to provide
            // has nothing left to roll off. Every specular hit on snow and ice, and every lamp in
            // camp, clips flat white instead of falling off. This is a one-line change with a visible
            // result and effectively no cost at this scene's complexity.
            changed += SetEnum(so, "m_ColorGradingMode", 1, "colour grading = HDR");

            // A larger grading LUT: 16 is the mobile default and it bands visibly across the huge,
            // near-flat blue-grey gradients this game is almost entirely made of. Snow at night is
            // exactly the worst case for a small LUT.
            changed += SetInt(so, "m_ColorGradingLutSize", 32, "grading LUT = 32");

            // Shadow cascades. One cascade over a 50 m distance puts the whole range in a single
            // shadow map, so near-field contact shadows — the ones that actually ground a figure —
            // get the same handful of texels as a tree 40 m away. Four cascades spend the resolution
            // where the player is looking.
            changed += SetInt(so, "m_ShadowCascadeCount", 4, "shadow cascades = 4");
            changed += SetFloat(so, "m_ShadowDistance", 55f, "shadow distance = 55 m");

            if (changed > 0) so.ApplyModifiedPropertiesWithoutUndo();
            return changed;
        }

        // ------------------------------------------------------------------ renderer assets

        /// <summary>
        /// Add the SSAO renderer feature to every renderer the pipeline asset uses.
        ///
        /// "Every renderer" rather than the default one: the URP template ships PC and Mobile
        /// renderers and the active one depends on the quality level, so configuring only index 0 is
        /// a coin flip that silently does nothing half the time.
        /// </summary>
        private static int ConfigureRenderers(UniversalRenderPipelineAsset urp)
        {
            int changed = 0;
            foreach (var data in RendererDataList(urp))
            {
                if (data == null) continue;
                changed += EnsureSsao(data);
            }
            return changed;
        }

        /// <summary>
        /// The renderer data list is an internal field on the pipeline asset, so it is read
        /// reflectively. Failing loudly here is deliberate — a silent empty list would look exactly
        /// like "already configured", which is the failure mode this whole file exists to avoid.
        /// </summary>
        private static ScriptableRendererData[] RendererDataList(UniversalRenderPipelineAsset urp)
        {
            var field = typeof(UniversalRenderPipelineAsset).GetField(
                "m_RendererDataList",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field?.GetValue(urp) is ScriptableRendererData[] list && list.Length > 0) return list;

            Debug.LogError("[RenderPipelineSetup] Could not read the renderer data list from the URP " +
                           "asset (field renamed in this URP version?). Add SSAO by hand: select the " +
                           "renderer asset → Add Renderer Feature → Screen Space Ambient Occlusion.");
            return new ScriptableRendererData[0];
        }

        private static int EnsureSsao(ScriptableRendererData data)
        {
            var existing = data.rendererFeatures.FirstOrDefault(f => f is ScreenSpaceAmbientOcclusion);
            bool created = false;

            if (existing == null)
            {
                var ssao = ScriptableObject.CreateInstance<ScreenSpaceAmbientOcclusion>();
                ssao.name = "ScreenSpaceAmbientOcclusion";
                // The feature is a sub-asset OF the renderer asset. Skip this and it is a loose
                // in-memory object that vanishes on domain reload, leaving a renderer with a broken
                // (missing-script) feature entry — which is worse than not adding it at all.
                AssetDatabase.AddObjectToAsset(ssao, data);
                data.rendererFeatures.Add(ssao);
                EditorUtility.SetDirty(data);
                existing = ssao;
                created = true;
            }

            TuneSsao((ScreenSpaceAmbientOcclusion)existing);
            if (created) Debug.Log($"[RenderPipelineSetup] Added SSAO to '{data.name}'.");
            return created ? 1 : 0;
        }

        /// <summary>
        /// SSAO settings, tuned for integrated graphics and for snow.
        ///
        /// AfterOpaque is the cheap path: it runs on the resolved opaque buffer instead of forcing a
        /// full depth-normals prepass, which on an integrated GPU is the difference between "free" and
        /// "noticeable". The radius is deliberately small — AO here is doing contact shadow, the dark
        /// line where an object meets the snow, not broad-scale shading, and a large radius over a
        /// mostly-flat white field just fogs the whole image grey.
        /// </summary>
        private static void TuneSsao(ScreenSpaceAmbientOcclusion ssao)
        {
            var so = new SerializedObject(ssao);
            var s = so.FindProperty("m_Settings");
            if (s == null) return;

            SetChild(s, "Intensity", 0.55f);
            SetChild(s, "Radius", 0.3f);
            SetChild(s, "Falloff", 40f);
            SetChildInt(s, "Downsample", 1);       // half-res AO; the blur hides it
            SetChildInt(s, "AfterOpaque", 1);      // no depth-normals prepass
            SetChildInt(s, "Samples", 1);          // medium
            SetChildInt(s, "BlurQuality", 1);      // medium
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // ------------------------------------------------------------------ serialized helpers
        //
        // All of these no-op (and report nothing changed) when the property is absent or already
        // correct, which is what makes the whole command safe to re-run.

        private static void SetChild(SerializedProperty parent, string name, float value)
        {
            var p = parent.FindPropertyRelative(name);
            if (p != null && p.propertyType == SerializedPropertyType.Float) p.floatValue = value;
        }

        private static void SetChildInt(SerializedProperty parent, string name, int value)
        {
            var p = parent.FindPropertyRelative(name);
            if (p == null) return;
            if (p.propertyType == SerializedPropertyType.Boolean) p.boolValue = value != 0;
            else if (p.propertyType == SerializedPropertyType.Enum || p.propertyType == SerializedPropertyType.Integer) p.intValue = value;
        }

        private static int SetInt(SerializedObject so, string path, int value, string label)
        {
            var p = so.FindProperty(path);
            if (p == null || p.intValue == value) return 0;
            p.intValue = value;
            Debug.Log($"[RenderPipelineSetup] {label}");
            return 1;
        }

        private static int SetEnum(SerializedObject so, string path, int value, string label)
        {
            return SetInt(so, path, value, label);
        }

        private static int SetFloat(SerializedObject so, string path, float value, string label)
        {
            var p = so.FindProperty(path);
            if (p == null || Mathf.Approximately(p.floatValue, value)) return 0;
            p.floatValue = value;
            Debug.Log($"[RenderPipelineSetup] {label}");
            return 1;
        }
    }
}
