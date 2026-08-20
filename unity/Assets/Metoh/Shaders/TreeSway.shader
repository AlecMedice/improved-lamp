// Wind sway for the forest.
//
// WHY THIS IS A SHADER AND NOT A SCRIPT. The obvious implementation — rotate each tree's transform a
// little — is impossible here. WorldBuilder MERGES every tree in a forest chunk into one mesh
// (CombineInstance, ~40 trees per chunk, 64 chunks), which is what keeps the draw-call count sane on
// the integrated GPU this build targets ([perf]). There is no per-tree transform left to rotate;
// moving the chunk's GameObject would slide forty trees sideways as one rigid slab.
//
// So the sway has to happen per VERTEX, and the vertex needs to know two things the merged mesh does
// not naturally carry:
//
//   uv2.x  SWAY WEIGHT   0 at this tree's own base, rising toward its crown. Not chunk-relative
//                        height — trees stand on sloped ground at different altitudes, so a
//                        chunk-space Y would make the uphill trees rigid and the downhill ones
//                        thrash. Trunk bases must stay planted or the whole forest looks like it is
//                        skating.
//   uv2.y  PHASE         a per-tree constant. Without it every tree in the chunk leans the same way
//                        at the same instant, which reads as the ground tilting rather than as wind.
//
// WorldBuilder.BakeSwayData writes both after combining, walking the vertex ranges it just added.
//
// THE MOTION ITSELF is two sines at different rates plus a much faster, smaller one for the crown
// flutter. Real wind is gusty, so the trunk lean and the branch flutter must not share a frequency —
// if they do, the tree pumps like a spring instead of being pushed. Gusts come from a slow sine
// multiplying the whole thing, which is what stops it reading as a metronome.
//
// Displacement is in WORLD space along a fixed wind direction, deliberately: neighbouring trees have
// different rotations baked into the merged mesh, and an object-space push would send them in
// different directions from the same wind.
//
// ---------------------------------------------------------------------------------------------
// THE NORMAL MAP AND THE KEYWORD BLOCK ARE BOTH LOAD-BEARING, AND BOTH WERE MISSING (fixed
// 2026-08-15). This shader declared `_BumpMap` and `_BumpScale`, MeshUtil.Sway dutifully bound
// ProcTex.BarkNormal to every trunk and ProcTex.SnowNormal to every snow-laden crown — and the
// fragment shader never sampled either one. `surface.normalTS` was left at zero, so 2,400 trees
// took the whole of [materials] and rendered flat anyway. It presents as an art problem, which is
// exactly the failure mode the terrain shader's own header warns about.
//
// The keyword list below is deliberately the SAME SET Snowpack.shader declares, and for the same
// reason its header gives: miss one and the failure is always quiet and always looks like an art
// bug. What was missing here specifically cost, in order of how much it showed:
//   _FORWARD_PLUS / _CLUSTER_LIGHT_LOOP  — under Forward+ the per-object light list is never read,
//                                          so the campfire, the duffel lamp, the crevasse glows and
//                                          every searcher's torch lit NOTHING on a trunk.
//   _SHADOWS_SOFT                        — the forest kept hard shadow edges on the tier that pays
//                                          for soft ones, which is most of the frame.
//   _ADDITIONAL_LIGHT_SHADOWS            — torches cast no shadows through the trees.
//   _LIGHT_COOKIES                       — the torch cookie (ProcTex.TorchCookie) was ignored, so a
//                                          beam landed on a trunk as a hard mathematical disc.
//   _SCREEN_SPACE_OCCLUSION              — the trees never RECEIVED the AO the render-pipeline
//                                          setup exists to add.
// The DepthNormals pass at the bottom was likewise promised by a comment and simply absent.
Shader "Metoh/TreeSway"
{
    Properties
    {
        _BaseMap        ("Base map", 2D) = "white" {}
        _BaseColor      ("Base colour", Color) = (1,1,1,1)
        _BumpMap        ("Normal map", 2D) = "bump" {}
        _BumpScale      ("Normal scale", Float) = 1
        _Smoothness     ("Smoothness", Range(0,1)) = 0.2
        _WindDir        ("Wind direction (xz)", Vector) = (-0.86, 0, 0.51, 0)
        _WindStrength   ("Sway metres at full weight", Float) = 0.22
        _WindSpeed      ("Sway rate", Float) = 0.85
        _GustScale      ("Gust size (metres)", Float) = 26
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        LOD 200

        // Everything the material can set lives in ONE cbuffer, declared identically in every pass,
        // or the SRP Batcher silently refuses to batch — and this shader draws ~190 chunk meshes.
        HLSLINCLUDE
        // Core FIRST: CBUFFER_START is one of its macros, and an HLSLINCLUDE block is prepended to
        // every pass ahead of that pass's own includes.
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            float4 _BaseColor;
            float  _BumpScale;
            float  _Smoothness;
            float4 _WindDir;
            float  _WindStrength;
            float  _WindSpeed;
            float  _GustScale;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag

            // The standard URP lighting keyword set — see the file header for what each one cost
            // while it was missing.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            // The Forward+ light loop keyword was renamed between URP versions; declaring an unused
            // one costs nothing, so both are listed rather than betting on the Unity version.
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Sway.hlsl"

            TEXTURE2D(_BaseMap);      SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap);      SAMPLER(sampler_BumpMap);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                float2 sway       : TEXCOORD1; // uv2: (weight, phase) — see the file header
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS   : TEXCOORD2;
                float4 tangentWS  : TEXCOORD3;
                float  fogCoord   : TEXCOORD4;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);
                posWS = ApplySway(posWS, IN.sway, _WindDir.xyz, _WindStrength, _WindSpeed, _GustScale);

                // Normals are NOT re-derived from the swayed surface. The displacement is a metre at
                // the very crown and a few centimetres over most of the mesh, so the error is far
                // below what a bark normal map is already perturbing — and recovering the true normal
                // would mean differencing the sway field per vertex, which is real cost for something
                // nobody can see at night through fog.
                VertexNormalInputs nrm = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);

                OUT.positionWS = posWS;
                OUT.positionCS = TransformWorldToHClip(posWS);
                OUT.normalWS   = nrm.normalWS;
                OUT.tangentWS  = float4(nrm.tangentWS, IN.tangentOS.w * GetOddNegativeScale());
                OUT.uv         = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.fogCoord   = ComputeFogFactor(OUT.positionCS.z);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv) * _BaseColor;

                // Bark striation on the trunks, snow grain on the laden crowns. The map defaults to
                // "bump" (flat), so an untextured material still renders correctly.
                float3 normalTS = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, IN.uv), _BumpScale);

                float3 geoNormal = normalize(IN.normalWS);
                float3 tangent = normalize(IN.tangentWS.xyz);
                float3 bitangent = IN.tangentWS.w * cross(geoNormal, tangent);
                float3x3 tbn = float3x3(tangent, bitangent, geoNormal);

                SurfaceData surface = (SurfaceData)0;
                surface.albedo = albedo.rgb;
                surface.smoothness = _Smoothness;
                surface.occlusion = 1.0;
                surface.alpha = 1.0;
                surface.normalTS = normalTS;

                InputData inputData = (InputData)0;
                inputData.positionWS = IN.positionWS;
                inputData.normalWS = normalize(TransformTangentToWorld(normalTS, tbn));
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                inputData.fogCoord = IN.fogCoord;
                inputData.bakedGI = SampleSH(inputData.normalWS);
                // Required by _SCREEN_SPACE_OCCLUSION — without it SSAO samples garbage and the
                // trunks gain blotches that swim with the camera.
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                half4 color = UniversalFragmentPBR(inputData, surface);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                return color;
            }
            ENDHLSL
        }

        // Shadows have to sway too, or a still shadow under a moving tree gives the whole thing away.
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vertShadow
            #pragma fragment fragShadow
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "Sway.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct AttributesS
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 sway       : TEXCOORD1;
            };

            float4 vertShadow(AttributesS IN) : SV_POSITION
            {
                float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);
                posWS = ApplySway(posWS, IN.sway, _WindDir.xyz, _WindStrength, _WindSpeed, _GustScale);
                float3 nrmWS = TransformObjectToWorldNormal(IN.normalOS);

                // Punctual shadow casters (a torch, the brazier) bias toward the light's POSITION, not
                // a fixed direction — without the branch a trunk lit from a nearby point light writes
                // its shadow with the moon's bias and peters/acnes visibly up close.
                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDir = normalize(_LightPosition - posWS);
                #else
                    float3 lightDir = _LightDirection;
                #endif

                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(posWS, nrmWS, lightDir));
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                return positionCS;
            }

            half4 fragShadow() : SV_Target { return 0; }
            ENDHLSL
        }

        // DepthOnly and DepthNormals, so the depth prepass and the non-AfterOpaque SSAO path both see
        // the SWAYED silhouette. Without these the occlusion sticks to where the tree would be if it
        // were rigid, which shows up as a dark halo that lags the trunk.
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vertDepth
            #pragma fragment fragDepth

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Sway.hlsl"

            struct AttributesD
            {
                float4 positionOS : POSITION;
                float2 sway       : TEXCOORD1;
            };

            float4 vertDepth(AttributesD IN) : SV_POSITION
            {
                float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);
                posWS = ApplySway(posWS, IN.sway, _WindDir.xyz, _WindStrength, _WindSpeed, _GustScale);
                return TransformWorldToHClip(posWS);
            }

            half4 fragDepth() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vertDN
            #pragma fragment fragDN

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Sway.hlsl"

            TEXTURE2D(_BumpMap);  SAMPLER(sampler_BumpMap);

            struct AttributesDN
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                float2 sway       : TEXCOORD1;
            };

            struct VaryingsDN
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float4 tangentWS  : TEXCOORD2;
            };

            VaryingsDN vertDN(AttributesDN IN)
            {
                VaryingsDN OUT = (VaryingsDN)0;
                float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);
                posWS = ApplySway(posWS, IN.sway, _WindDir.xyz, _WindStrength, _WindSpeed, _GustScale);
                VertexNormalInputs nrm = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);
                OUT.positionCS = TransformWorldToHClip(posWS);
                OUT.normalWS = nrm.normalWS;
                OUT.tangentWS = float4(nrm.tangentWS, IN.tangentOS.w * GetOddNegativeScale());
                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);
                return OUT;
            }

            // Must produce the SAME normal the lit pass does, or SSAO occludes against a surface that
            // is not the one being drawn — the rule Snowpack.hlsl exists to enforce for the ground.
            half4 fragDN(VaryingsDN IN) : SV_Target
            {
                float3 normalTS = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, IN.uv), _BumpScale);
                float3 geoNormal = normalize(IN.normalWS);
                float3 tangent = normalize(IN.tangentWS.xyz);
                float3 bitangent = IN.tangentWS.w * cross(geoNormal, tangent);
                float3x3 tbn = float3x3(tangent, bitangent, geoNormal);
                float3 n = normalize(TransformTangentToWorld(normalTS, tbn));
                return half4(NormalizeNormalPerPixel(n), 0.0);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}
