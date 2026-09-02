// Prayer flags and route-marker flags, with wind.
//
// WHAT THIS REPLACES. Every flag in the game was `MeshUtil.UnitCube()` scaled flat — a 13 cm solid
// BOX of flat unlit colour on the trail poles, and a 55 cm one on the marker masts — and none of
// them moved. Owner's note: we are in the mountains, the flags should have wind in them. Both halves
// of that are right, and the second is the one that matters: a lung-ta strung on a ridge is never
// still, and a row of motionless rectangles is the single most obviously dead thing in a valley
// where the snow, the smoke and the trees are all already moving.
//
// WHY A VERTEX SHADER AND NOT A TRANSFORM ANIMATION. The flags are welded into per-chunk combined
// meshes (WorldBuilder.BuildUndergrowth) — hundreds of them share one renderer, so there is no
// transform left to animate, and giving each flag its own GameObject to wave would trade one draw
// call for hundreds. Cloth also does not move like a rigid body: it ripples ALONG itself, and only
// the free edge travels far. That is a per-vertex function, which is exactly what a vertex shader is.
//
// THE HOIST EDGE MUST NOT MOVE. uv.x is 0 at the edge lashed to the pole and 1 at the fly, and every
// displacement below is scaled by uv.x*uv.x. Squared rather than linear for the same reason the tree
// sway squares its weight: a linear ramp bends from the very first vertex, which reads as a flag
// hinged at the pole rather than gripped by it.
//
// The gust term is deliberately the SAME shape as Sway.hlsl's — a travelling wave sampled from world
// position, so wind arrives somewhere before it arrives everywhere. Flags and trees are the two
// things in frame that advertise the wind, and if they disagree about when it is gusting, both stop
// reading as weather. Keep _WindDir here equal to TreeSway's.
Shader "Metoh/Flag"
{
    Properties
    {
        _BaseColor    ("Colour", Color) = (0.9, 0.9, 0.9, 1)
        _WindDir      ("Wind direction (xz)", Vector) = (-0.86, 0, 0.51, 0)
        _Amplitude    ("Ripple metres at the fly", Float) = 0.085
        _WindSpeed    ("Ripple rate", Float) = 2.6
        _Waves        ("Waves along the flag", Float) = 1.6
        _GustScale    ("Gust wavelength (m)", Float) = 55
        _Droop        ("Droop at the fly (m)", Float) = 0.05
        _Emission     ("Self-lift (keeps colour readable at night)", Range(0, 1)) = 0.18
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "FlagForward"
            Tags { "LightMode" = "UniversalForward" }
            // A flag is a SHEET. Culling one side makes half of every flag vanish as you walk round it.
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float  fogFactor  : TEXCOORD2;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _WindDir;
                float  _Amplitude;
                float  _WindSpeed;
                float  _Waves;
                float  _GustScale;
                float  _Droop;
                float  _Emission;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 nrmWS = TransformObjectToWorldNormal(IN.normalOS);

                // How free this vertex is. 0 at the hoist, 1 at the fly.
                float free = IN.uv.x * IN.uv.x;

                float t = _Time.y * _WindSpeed;
                // Per-flag phase from world position, so neighbouring flags on one pole — and poles a
                // hundred metres apart — never ripple in lockstep.
                float phase = frac(dot(posWS.xz, float2(0.113, 0.079))) * 6.2831853;

                // Same travelling gust as the trees.
                float travel = dot(posWS.xz, normalize(_WindDir.xz)) / max(_GustScale, 1.0);
                float gust = 0.65 + 0.35 * sin(t * 0.14 - travel);

                // The ripple runs ALONG the flag and off the fly edge, which is what makes it cloth
                // rather than a waving plank. The second, faster wave keeps the crest from being a
                // single clean sine — real cloth carries more than one wavelength at once.
                float wave = sin(IN.uv.x * _Waves * 6.2831853 - t + phase)
                           + 0.35 * sin(IN.uv.x * _Waves * 11.0 - t * 1.7 + phase * 1.3);

                // Displace across the sheet, using its own normal so this works whatever yaw the flag
                // was baked at inside a combined chunk mesh.
                posWS += nrmWS * wave * _Amplitude * free * gust;
                // Cloth has weight: the fly end hangs, and hangs LESS as the gust lifts it.
                posWS.y -= _Droop * free * (1.25 - gust);

                OUT.positionWS = posWS;
                OUT.normalWS = nrmWS;
                OUT.positionCS = TransformWorldToHClip(posWS);
                OUT.fogFactor = ComputeFogFactor(OUT.positionCS.z);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // Two-sided lambert: abs() so the back of the flag is lit rather than black. A sheet
                // has no inside, and the alternative is half of every flag reading as a hole.
                float3 n = normalize(IN.normalWS);
                Light main = GetMainLight();
                half ndl = abs(dot(n, main.direction));

                half3 ambient = SampleSH(n);
                half3 col = _BaseColor.rgb * (ambient + main.color * ndl);
                // These are NAVIGATION markers — the whole reason the poles exist is to be picked out
                // at range in the dark, so a little self-lift keeps the lung-ta colours from going to
                // mud once they are past the reach of the moon.
                col += _BaseColor.rgb * _Emission;

                col = MixFog(col, IN.fogFactor);
                return half4(col, 1);
            }
            ENDHLSL
        }
    }

    Fallback "Universal Render Pipeline/Unlit"
}
