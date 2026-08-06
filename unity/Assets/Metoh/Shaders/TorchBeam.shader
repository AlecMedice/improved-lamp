// The visible shaft of a searcher's torch.
//
// WHY THIS EXISTS. A Unity spot light is invisible until its light lands on something. Out on open
// snowpack that means a searcher's torch produced a lit patch of ground with no shaft connecting it
// to the person holding it — the one warm thing in the frame had no source. It also cost the Yeti
// the single most useful thing a torch gives it: a beam sweeping in the dark is visible from far
// beyond the range at which you can make out the body carrying it, and that asymmetry is supposed to
// be the trade the searcher accepts for being able to see at all.
//
// It is a cone of geometry with an additive falloff, not volumetric lighting. Real volumetrics need
// a per-frame raymarch against the depth buffer and shadow maps; this is a handful of triangles and
// one multiply, which matters on the integrated-graphics target — and at night, in fog, with bloom
// already running over the top, the two are very hard to tell apart.
//
// ADDITIVE, NEVER ALPHA-BLENDED. Light adds; it cannot darken what is behind it. An alpha-blended
// beam washes the scene toward grey where it crosses a dark trunk, which reads as fog on the lens.
Shader "Metoh/TorchBeam"
{
    Properties
    {
        _Color      ("Colour", Color) = (1, 0.91, 0.77, 1)
        _Intensity  ("Intensity", Range(0, 2)) = 0.30
        _EdgeFade   ("Edge fade", Range(0.5, 8)) = 2.6
        _RangeFade  ("Range fade", Range(0.5, 8)) = 1.7
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "TorchBeam"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha One   // additive
            ZWrite Off
            // Cull Off because the holder's own camera sits inside the cone's apex: with backface
            // culling the owner would be the one person who never sees their own beam.
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float  _Intensity;
                float  _EdgeFade;
                float  _RangeFade;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // uv.x = 0 on the axis, 1 at the cone wall. uv.y = 0 at the lens, 1 at the far end.
                half edge  = pow(saturate(1.0 - IN.uv.x), _EdgeFade);
                half along = pow(saturate(1.0 - IN.uv.y), _RangeFade);

                // Fade the first fraction back IN as well. Without it the cone is at full strength the
                // instant it leaves the apex, and that hard start sits right in front of the camera
                // where it reads as a bright speck stuck to the lens.
                half nearFade = smoothstep(0.0, 0.06, IN.uv.y);

                half a = edge * along * nearFade * _Intensity;
                return half4(_Color.rgb * a, a);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
