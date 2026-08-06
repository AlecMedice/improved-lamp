// The snowpack ground — snow/rock blended by SLOPE, with the deep-snow basin made visible.
//
// WHY THIS EXISTS. The terrain was one URP/Lit material with one colour across all 800 m. Even with
// a good normal map that is a single flat value everywhere, and the result is the specific complaint
// that started this pass: you cannot tell a ridge from a hollow, you cannot judge distance, and every
// direction looks like every other one. Snow country does not actually read as white — it reads as
// the CONTRAST between white and the rock the wind strips bare, and that contrast is what your eye
// uses to see the shape of the land. Add the dark anchors back and the terrain becomes legible
// without touching a single vertex.
//
// SLOPE IS THE RIGHT DRIVER, AND `1 - normal.y` IS THE WRONG WAY TO MEASURE IT HERE. This terrain is
// gentle: fBm at 0.0065/m scaled to a 14 m amplitude gives real-world gradients around 0.02..0.35,
// so `1 - normal.y` spans roughly 0..0.06 and every sensible-looking threshold in that range is
// either "no rock anywhere" or "rock everywhere". We use the GRADIENT (rise over run,
// `length(n.xz)/n.y`), which spans a usable 0..0.4 on this terrain and means the tuning values below
// are readable as real slopes rather than as magic numbers.
//
// THE DRIFT BASIN IS A GAMEPLAY SURFACE, NOT DECORATION. `Movement.DeepSnowDepth` slows searchers on
// any ground below `Player.DriftHeight`, which is about a third of the map — and until now that zone
// was completely invisible. A routing choice you cannot see is not a choice; it is an ambush. The
// drift tint here is driven from those exact constants (WorldBuilder passes them in, so there is one
// source of truth and no second copy to drift out of sync), which makes the slow zone something a
// player can read off the ground and plan around. Presentation only — nothing here touches the sim.
Shader "Metoh/Snowpack"
{
    Properties
    {
        [Header(Snow)]
        _BaseColor      ("Snow colour", Color) = (0.79, 0.84, 0.89, 1)
        _SnowSmoothness ("Snow smoothness", Range(0,1)) = 0.42
        _BumpMap        ("Snow normal", 2D) = "bump" {}
        _SnowTiling     ("Snow tiling (metres per repeat)", Float) = 6
        _SnowNormalScale("Snow normal strength", Range(0,3)) = 0.75

        [Header(Exposed rock)]
        _RockColor      ("Rock colour", Color) = (0.28, 0.29, 0.32, 1)
        _RockSmoothness ("Rock smoothness", Range(0,1)) = 0.12
        _RockMap        ("Rock normal", 2D) = "bump" {}
        _RockTiling     ("Rock tiling (metres per repeat)", Float) = 4
        _RockNormalScale("Rock normal strength", Range(0,3)) = 1.1

        [Header(Slope blend)]
        _SlopeStart     ("Rock starts at gradient", Range(0,1)) = 0.17
        _SlopeEnd       ("Full rock at gradient", Range(0,1)) = 0.34
        _SlopeJitter    ("Boundary break-up", Range(0,1)) = 0.4

        [Header(Deep snow basin)]
        _DriftColor     ("Drift colour", Color) = (0.72, 0.79, 0.88, 1)
        _DriftHeight    ("Drift top (world Y)", Float) = -2
        _DriftDepth     ("Metres to full drift", Float) = 1.5
        _DriftStrength  ("Drift tint strength", Range(0,1)) = 0.65

        [Header(Break-up)]
        _MacroTiling    ("Wind-scour tiling (metres)", Float) = 42
        _MacroStrength  ("Wind-scour strength", Range(0,1)) = 0.22
        _DetailMap      ("Detail normal", 2D) = "bump" {}
        _DetailTiling   ("Detail tiling (metres)", Float) = 0.7
        _DetailScale    ("Detail strength", Range(0,2)) = 0.6
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        // ------------------------------------------------------------------ shared code
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        // Everything the material can set lives in ONE cbuffer, or the SRP Batcher silently refuses to
        // batch this shader — which on a mesh this size is a real cost for an invisible reason.
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseColor;
            float4 _RockColor;
            float4 _DriftColor;
            float  _SnowSmoothness;
            float  _RockSmoothness;
            float  _SnowTiling;
            float  _RockTiling;
            float  _SnowNormalScale;
            float  _RockNormalScale;
            float  _SlopeStart;
            float  _SlopeEnd;
            float  _SlopeJitter;
            float  _DriftHeight;
            float  _DriftDepth;
            float  _DriftStrength;
            float  _MacroTiling;
            float  _MacroStrength;
            float  _DetailTiling;
            float  _DetailScale;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag

            // The standard URP lighting keyword set. Miss one of these and the failure is always
            // quiet and always looks like an art bug: no shadows, or no campfire, or no AO.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            // Forward+ light loop. The keyword was renamed between URP versions and declaring an
            // unused one costs nothing, so both are listed rather than betting on the Unity version.
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Snowpack.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float4 tangentWS  : TEXCOORD2;
                float  fogFactor  : TEXCOORD3;
            };

            Varyings Vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                VertexPositionInputs pos = GetVertexPositionInputs(v.positionOS.xyz);
                VertexNormalInputs nrm = GetVertexNormalInputs(v.normalOS, v.tangentOS);
                o.positionCS = pos.positionCS;
                o.positionWS = pos.positionWS;
                o.normalWS = nrm.normalWS;
                o.tangentWS = float4(nrm.tangentWS, v.tangentOS.w * GetOddNegativeScale());
                o.fogFactor = ComputeFogFactor(pos.positionCS.z);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float3 geoNormal = normalize(i.normalWS);

                // Named `surf`, not `mix` — `mix` is the GLSL name for lerp, and this shader gets
                // cross-compiled to GLSL/Metal for other targets. Shadowing an intrinsic there is a
                // platform-specific failure that never shows up on the machine you author on.
                SurfaceMix surf = MixSnowpack(i.positionWS, geoNormal);

                // Tangent basis, for both normal maps.
                float sgn = i.tangentWS.w;
                float3 tangent = normalize(i.tangentWS.xyz);
                float3 bitangent = sgn * cross(geoNormal, tangent);
                float3x3 tbn = float3x3(tangent, bitangent, geoNormal);

                SurfaceData surface = (SurfaceData)0;
                surface.albedo = surf.albedo;
                surface.smoothness = surf.smoothness;
                surface.occlusion = 1.0;
                surface.alpha = 1.0;
                surface.normalTS = surf.normalTS;

                InputData input = (InputData)0;
                input.positionWS = i.positionWS;
                input.normalWS = normalize(TransformTangentToWorld(surf.normalTS, tbn));
                input.viewDirectionWS = GetWorldSpaceNormalizeViewDir(i.positionWS);
                // Per-pixel cascade selection. Cheaper to reason about than the interpolated variant
                // and correct at every cascade split, which matters with 4 cascades over 55 m.
                input.shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                input.fogCoord = i.fogFactor;
                input.bakedGI = SampleSH(input.normalWS);
                // Required by _SCREEN_SPACE_OCCLUSION — without it SSAO samples garbage and the
                // ground gets blotches that move with the camera.
                input.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
                input.shadowMask = half4(1, 1, 1, 1);

                half4 color = UniversalFragmentPBR(input, surface);
                color.rgb = MixFog(color.rgb, input.fogCoord);
                return color;
            }
            ENDHLSL
        }

        // Shadow casting. The ground casts onto itself — ridges shadowing the hollows below them is
        // a large part of what makes the terrain's shape readable at all under a low moon.
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
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            float4 ShadowVert(ShadowAttributes v) : SV_POSITION
            {
                float3 positionWS = TransformObjectToWorld(v.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(v.normalOS);

                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDir = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDir = _LightDirection;
                #endif

                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDir));
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                return positionCS;
            }

            half4 ShadowFrag() : SV_Target { return 0; }
            ENDHLSL
        }

        // Depth + depth-normals, so depth-prepass features and the non-AfterOpaque SSAO path both
        // work if the renderer is ever reconfigured.
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float4 DepthVert(float4 positionOS : POSITION) : SV_POSITION
            {
                return TransformObjectToHClip(positionOS.xyz);
            }

            half4 DepthFrag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex DepthNormalsVert
            #pragma fragment DepthNormalsFrag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Snowpack.hlsl"

            struct DNAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
            };

            struct DNVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float4 tangentWS  : TEXCOORD2;
            };

            DNVaryings DepthNormalsVert(DNAttributes v)
            {
                DNVaryings o = (DNVaryings)0;
                VertexPositionInputs pos = GetVertexPositionInputs(v.positionOS.xyz);
                VertexNormalInputs nrm = GetVertexNormalInputs(v.normalOS, v.tangentOS);
                o.positionCS = pos.positionCS;
                o.positionWS = pos.positionWS;
                o.normalWS = nrm.normalWS;
                o.tangentWS = float4(nrm.tangentWS, v.tangentOS.w * GetOddNegativeScale());
                return o;
            }

            half4 DepthNormalsFrag(DNVaryings i) : SV_Target
            {
                float3 geoNormal = normalize(i.normalWS);
                SurfaceMix surf = MixSnowpack(i.positionWS, geoNormal);
                float3 tangent = normalize(i.tangentWS.xyz);
                float3 bitangent = i.tangentWS.w * cross(geoNormal, tangent);
                float3x3 tbn = float3x3(tangent, bitangent, geoNormal);
                float3 n = normalize(TransformTangentToWorld(surf.normalTS, tbn));
                return half4(NormalizeNormalPerPixel(n), 0.0);
            }
            ENDHLSL
        }
    }

    Fallback "Universal Render Pipeline/Lit"
}
