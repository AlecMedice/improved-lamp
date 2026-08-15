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

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Sway.hlsl"

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
                float  fogCoord   : TEXCOORD3;
            };

            TEXTURE2D(_BaseMap);      SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap);      SAMPLER(sampler_BumpMap);

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

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);
                posWS = ApplySway(posWS, IN.sway, _WindDir.xyz, _WindStrength, _WindSpeed, _GustScale);

                OUT.positionWS = posWS;
                OUT.positionCS = TransformWorldToHClip(posWS);
                OUT.normalWS   = TransformObjectToWorldNormal(IN.normalOS);
                OUT.uv         = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.fogCoord   = ComputeFogFactor(OUT.positionCS.z);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv) * _BaseColor;

                InputData inputData = (InputData)0;
                inputData.positionWS = IN.positionWS;
                inputData.normalWS = normalize(IN.normalWS);
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                inputData.fogCoord = IN.fogCoord;
                inputData.bakedGI = SampleSH(inputData.normalWS);

                SurfaceData surface = (SurfaceData)0;
                surface.albedo = albedo.rgb;
                surface.smoothness = _Smoothness;
                surface.occlusion = 1.0;
                surface.alpha = 1.0;

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
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex vertShadow
            #pragma fragment fragShadow

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "Sway.hlsl"

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

            float3 _LightDirection;

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
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(posWS, nrmWS, _LightDirection));
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

        // DepthOnly / DepthNormals, so SSAO and the depth prepass see the SWAYED silhouette. Without
        // these the occlusion sticks to where the tree would be if it were rigid, which shows up as a
        // dark halo that lags the trunk.
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex vertDepth
            #pragma fragment fragDepth

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Sway.hlsl"

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
    }

    FallBack "Universal Render Pipeline/Lit"
}
