// Procedural night sky — gradient, stars, and an actual moon.
//
// Why a skybox and not geometry: RenderSettings.fog is ExponentialSquared at ~0.0125 density, so a
// world-space moon at any believable distance is attenuated to nothing (300 m -> e^-14). Skyboxes
// render unfogged, behind everything, with no depth cost — the only place a moon can actually live.
//
// Everything is computed from the view direction, so there are no textures and no art assets, and
// the whole sky costs one fullscreen-ish pass with no per-frame CPU work. WorldBuilder drives the
// colours, star brightness and moon direction from the same day-night palette that drives the fog,
// and points the directional "Moon" light down _MoonDir so the shadows agree with where the moon
// visibly is.
Shader "HollowPines/NightSky"
{
    Properties
    {
        _ZenithColor    ("Zenith colour", Color) = (0.03, 0.05, 0.11, 1)
        _HorizonColor   ("Horizon colour", Color) = (0.09, 0.11, 0.18, 1)
        _GroundColor    ("Below-horizon colour", Color) = (0.03, 0.04, 0.06, 1)
        _HorizonExp     ("Horizon falloff", Range(0.15, 2)) = 0.45

        _StarBrightness ("Star brightness", Range(0, 4)) = 1
        _StarDensity    ("Star density", Range(40, 600)) = 240
        _MilkyWay       ("Milky way strength", Range(0, 2)) = 0.55

        _MoonDir        ("Moon direction", Vector) = (0.35, 0.62, -0.7, 0)
        _MoonColor      ("Moon colour", Color) = (1, 0.97, 0.90, 1)
        _MoonSize       ("Moon angular radius (rad)", Range(0.004, 0.12)) = 0.036
        _MoonBrightness ("Moon brightness", Range(0, 8)) = 2.4
        _MoonPhase      ("Moon phase (-1 full..1 new)", Range(-1, 1)) = -0.35
        _MoonGlow       ("Moon glow", Range(0, 3)) = 0.8
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Background"
            "RenderType" = "Background"
            "PreviewType" = "Skybox"
        }
        Cull Off
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 dir : TEXCOORD0;
            };

            float4 _ZenithColor;
            float4 _HorizonColor;
            float4 _GroundColor;
            float  _HorizonExp;

            float  _StarBrightness;
            float  _StarDensity;
            float  _MilkyWay;

            float4 _MoonDir;
            float4 _MoonColor;
            float  _MoonSize;
            float  _MoonBrightness;
            float  _MoonPhase;
            float  _MoonGlow;

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                // The skybox mesh is drawn with the camera's rotation, so object space IS the
                // view direction. Same trick Unity's own skybox shaders use.
                o.dir = v.positionOS.xyz;
                return o;
            }

            // Cheap 3D hash. Deterministic per cell, which is what keeps stars from crawling.
            float hash31(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            float3 StarField(float3 dir)
            {
                float3 sp = dir * _StarDensity;
                float3 cell = floor(sp);
                float h = hash31(cell);

                // Only a small fraction of cells hold a star, else the sky reads as static noise.
                float present = step(0.975, h);

                // Jitter inside the cell so the field isn't visibly a lattice.
                float3 jitter = float3(hash31(cell + 1.7), hash31(cell + 3.1), hash31(cell + 5.3)) - 0.5;
                float d = length(frac(sp) - 0.5 - jitter * 0.55);

                // Magnitude varies: a few bright ones carry the look, most are faint.
                float mag = frac(h * 137.31);
                float radius = lerp(0.055, 0.17, mag * mag);
                float star = saturate(1.0 - d / max(radius, 1e-4));
                star = pow(star, 3.0) * lerp(0.30, 1.0, mag);

                // Twinkle — small, slow, and out of phase per star.
                star *= 0.80 + 0.20 * sin(_Time.y * lerp(1.5, 4.0, mag) + h * 62.8);

                // Atmospheric extinction: stars die out into the haze near the horizon.
                star *= smoothstep(-0.02, 0.20, dir.y);

                // Slightly cool/warm tint by magnitude, so it isn't a field of identical white dots.
                float3 tint = lerp(float3(0.75, 0.85, 1.0), float3(1.0, 0.92, 0.82), frac(h * 51.7));
                return star * present * tint * _StarBrightness;
            }

            float MilkyWayBand(float3 dir)
            {
                // A soft band across the dome, roughly edge-on. Broken up so it doesn't read as a
                // painted stripe — this is what stops the upper sky from looking uniformly empty.
                const float3 axis = normalize(float3(0.35, 0.30, -0.89));
                float band = 1.0 - abs(dot(dir, axis));
                band = pow(saturate(band), 9.0);
                float clump = hash31(floor(dir * 26.0)) * 0.6 + 0.4;
                return band * clump * smoothstep(0.0, 0.25, dir.y);
            }

            float4 frag(Varyings i) : SV_Target
            {
                float3 dir = normalize(i.dir);

                // --- gradient ---------------------------------------------------------
                // Night sky is DARKEST overhead and lifts toward the horizon (scatter + whatever
                // light there is down there). Getting this backwards is the classic tell.
                float up = saturate(dir.y);
                float t = pow(up, _HorizonExp);
                float3 col = lerp(_HorizonColor.rgb, _ZenithColor.rgb, t);
                // Below the horizon the terrain covers almost everything; this only shows through
                // gaps and keeps the seam dark rather than bright.
                col = lerp(col, _GroundColor.rgb, saturate(-dir.y * 6.0));

                // --- stars ------------------------------------------------------------
                col += MilkyWayBand(dir) * _MilkyWay * _StarBrightness * float3(0.55, 0.60, 0.80) * 0.35;
                col += StarField(dir);

                // --- moon -------------------------------------------------------------
                float3 md = normalize(_MoonDir.xyz);
                float cosAng = clamp(dot(dir, md), -1.0, 1.0);
                float ang = acos(cosAng);

                // Soft-edged disc.
                float disc = 1.0 - smoothstep(_MoonSize * 0.90, _MoonSize, ang);

                // Phase terminator. Basis across the disc, so the shadow runs vertically like a
                // real crescent rather than cutting a chord at a random angle.
                float3 right = normalize(cross(float3(0.0, 1.0, 0.0), md));
                float x = dot(dir, right) / max(_MoonSize, 1e-4); // -1..1 across the face
                float lit = smoothstep(_MoonPhase - 0.14, _MoonPhase + 0.14, x);

                // Maria — faint darker blotches. A featureless white circle is the other classic tell.
                float maria = hash31(floor(dir * 420.0)) * 0.5 + hash31(floor(dir * 170.0)) * 0.5;
                float surface = lerp(0.78, 1.0, maria);

                // Limb darkening, so the edge falls off instead of ending flat.
                float limb = sqrt(saturate(1.0 - pow(saturate(ang / max(_MoonSize, 1e-4)), 2.0)));
                surface *= lerp(0.72, 1.0, limb);

                col += _MoonColor.rgb * _MoonBrightness * surface * lit * disc;

                // Halo. Wide, weak, and it also lifts the sky right around the moon.
                float glow = exp(-ang / max(_MoonSize * 7.0, 1e-4)) * _MoonGlow;
                col += _MoonColor.rgb * glow * 0.35;

                return float4(col, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
