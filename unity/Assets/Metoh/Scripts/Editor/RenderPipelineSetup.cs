// One-click configuration of the URP pipeline + renderer assets.
//
//     Metoh → Configure Render Pipeline
//
// WHY THIS IS A SCRIPT AND NOT A README STEP. The repo tracks only Scripts/, Shaders/ and Sim/ —
// the .asset files that define the render pipeline live in the live project and are never committed.
// So every setting that lives on those assets was, until now, an instruction in a document that had
// to be carried out by hand and could not be verified: UNITY_NOTES [materials] literally listed
// "Add Renderer Feature → Screen Space Ambient Occlusion" as an owner step, and it was never done,
// which is why the largest single realism gain available has been sitting unclaimed.
//
// A menu command fixes that class of problem properly. It is idempotent, it reports what it changed,
// and it can be re-run after any Unity upgrade or project re-clone.
//
// **AND IT HAS NEVER BEEN RUN.** Checked against the live project on 2026-08-15: both `PC_RPAsset`
// and `Mobile_RPAsset` still read `m_ColorGradingMode: 0` (LDR) and `m_ShadowDistance: 50`, and the
// SSAO on `PC_Renderer` is the URP template's own — `Intensity 0.4`, `Falloff 100`, `AfterOpaque 0`,
// `Downsample 0` — not the values TuneSsao writes. So the LDR-grading problem this file's header
// describes is still live, and the AO is both untuned for snow and running on the expensive path.
// Writing the script turned a documentation problem into a one-click problem; it did not turn it
// into a solved one. **Run Metoh → Configure Render Pipeline.** If a future reader finds this
// paragraph still accurate, the answer is still the same one click.
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
            var assets = PipelineAssets();
            if (assets.Length == 0)
            {
                Debug.LogError("[RenderPipelineSetup] No UniversalRenderPipelineAsset is active. " +
                               "Project Settings → Graphics → Scriptable Render Pipeline Settings.");
                return;
            }

            int changes = 0;
            changes += DisableAsyncShaderCompilation();
            foreach (var urp in assets)
            {
                changes += ConfigurePipelineAsset(urp);
                changes += ConfigureRenderers(urp);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log(changes == 0
                ? $"[RenderPipelineSetup] Already configured — nothing to change ({assets.Length} asset(s) checked)."
                : $"[RenderPipelineSetup] Applied {changes} change(s) across {assets.Length} asset(s). " +
                  "Enter Play mode to see them.");
        }

        /// <summary>
        /// Every URP asset this project can end up rendering through, not just the active one.
        ///
        /// The template ships a pipeline asset PER QUALITY LEVEL — here `PC_RPAsset` and
        /// `Mobile_RPAsset` — and `currentRenderPipeline` is only whichever one the editor happens to
        /// be sitting on. Configuring that alone means the settings survive exactly until someone
        /// moves the quality dropdown, at which point the whole look silently reverts with no error
        /// and no obvious cause. This is the same argument <see cref="ConfigureRenderers"/> already
        /// makes one level down about renderers, applied one level up.
        /// </summary>
        private static UniversalRenderPipelineAsset[] PipelineAssets()
        {
            var found = new System.Collections.Generic.List<UniversalRenderPipelineAsset>();
            void Add(RenderPipelineAsset a)
            {
                if (a is UniversalRenderPipelineAsset u && !found.Contains(u)) found.Add(u);
            }

            Add(GraphicsSettings.defaultRenderPipeline);
            Add(GraphicsSettings.currentRenderPipeline);
            for (int i = 0; i < QualitySettings.count; i++) Add(QualitySettings.GetRenderPipelineAssetAt(i));
            return found.ToArray();
        }

        // ------------------------------------------------------------------ editor-only

        /// <summary>
        /// Turn OFF the editor's asynchronous shader compilation.
        ///
        /// THIS IS THE "BROKEN FIRST LOAD" (owner report, 2026-08-15: *"every time it rebuilds the
        /// scene the first loading screen is always broken"*, and *"all the trees were neon blue until
        /// I alt-tabbed away"*). Both are one thing.
        ///
        /// With async compilation on, the editor does NOT wait for a shader variant to be ready before
        /// drawing with it. It renders the object in a flat placeholder colour — the neon blue — or,
        /// for something like the skybox, skips the draw entirely, which leaves whatever was already
        /// in the framebuffer. That is the rectangular black-and-stale tiles across the title screen:
        /// not corruption, just regions nothing wrote this frame. Compilation finishes in the
        /// background, so it always "fixes itself" after a few seconds or an alt-tab, which is exactly
        /// what makes it read as a flaky renderer rather than as a build step.
        ///
        /// It fires on the FIRST load after any shader reimport — i.e. every scene rebuild, and every
        /// time one of the four Metoh shaders is edited — which is precisely the moment somebody is
        /// looking at the title screen to judge whether an art change landed.
        ///
        /// So the trade is deliberate: with this off, the editor STALLS while variants compile instead
        /// of showing garbage. Same wait, but the screen stays honest, and a stall is unambiguous where
        /// a neon tree is indistinguishable from a real shader bug. This whole codebase is written
        /// against the rule that a silent degradation is worse than a loud stop ([legibility]) — this
        /// is that rule applied to the editor itself.
        ///
        /// Editor-only: it has no effect on a built player, which always compiles ahead. If the stalls
        /// become intolerable while iterating on shaders, flip it back in
        /// Edit → Preferences → Asset Pipeline; nothing depends on it being off.
        /// </summary>
        /// <summary>Public entry point, so GameSceneSetup can assert it too.</summary>
        internal static void DisableAsyncShaderCompilationIfNeeded() => DisableAsyncShaderCompilation();

        private static int DisableAsyncShaderCompilation()
        {
            if (!EditorSettings.asyncShaderCompilation) return 0;
            EditorSettings.asyncShaderCompilation = false;
            Debug.Log("[RenderPipelineSetup] async shader compilation = OFF — the editor now waits for " +
                      "variants instead of drawing neon-blue placeholders and stale framebuffer tiles " +
                      "on the first load after a shader reimport.");
            return 1;
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

            // Depth texture. `Weather`'s soft particles sample `_CameraDepthTexture` to fade a flake
            // out as it approaches whatever is behind it, instead of being sliced by the depth test
            // into a hard-edged white polygon. URP only generates that texture if something declares
            // it needs it, and "something" here is a checkbox on an untracked asset — so the failure
            // mode is the usual one for this file: no error, the snow just quietly stops being soft.
            changed += SetBool(so, "m_RequireDepthTexture", true, "depth texture = on");

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

        private static int SetBool(SerializedObject so, string path, bool value, string label)
        {
            var p = so.FindProperty(path);
            if (p == null || p.boolValue == value) return 0;
            p.boolValue = value;
            Debug.Log($"[RenderPipelineSetup] {label}");
            return 1;
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
