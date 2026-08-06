#ifndef METOH_SNOWPACK_INCLUDED
#define METOH_SNOWPACK_INCLUDED

// The surface mix shared by Snowpack.shader's lit and depth-normals passes.
//
// Kept in its own file for one specific reason: the depth-normals pass MUST produce the same normal
// the lit pass does, or SSAO occludes against a surface that isn't the one being drawn and the ground
// gains a faint moving crust of shadow. Two copies of this maths would drift apart the first time
// anybody tuned a tiling value. One function, two callers.

TEXTURE2D(_BumpMap);    SAMPLER(sampler_BumpMap);
TEXTURE2D(_RockMap);    SAMPLER(sampler_RockMap);
TEXTURE2D(_DetailMap);  SAMPLER(sampler_DetailMap);

struct SurfaceMix
{
    half3 albedo;
    half  smoothness;
    float3 normalTS;
};

// ---------------------------------------------------------------- noise
//
// Value noise on a world-metre lattice. This is break-up, not detail — it exists to stop large flat
// expanses from being literally one value, which is the thing that reads as "untextured".

float MetohHash21(float2 p)
{
    p = frac(p * float2(127.1, 311.7));
    p += dot(p, p + 34.23);
    return frac(p.x * p.y);
}

float MetohValue2(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    f = f * f * (3.0 - 2.0 * f); // smoothstep, so the lattice doesn't show as a grid of creases
    float a = MetohHash21(i);
    float b = MetohHash21(i + float2(1, 0));
    float c = MetohHash21(i + float2(0, 1));
    float d = MetohHash21(i + float2(1, 1));
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

float MetohFbm2(float2 p)
{
    float sum = 0.0, amp = 0.5, norm = 0.0;
    [unroll]
    for (int o = 0; o < 3; o++)
    {
        sum += MetohValue2(p) * amp;
        norm += amp;
        p *= 2.03;   // non-integer, so the octaves don't align into visible cross-hatching
        amp *= 0.5;
    }
    return sum / max(norm, 1e-5);
}

/// Combine two tangent-space normals. Partial-derivative blend: add the slopes, multiply the
/// heights. Cheaper than reoriented-normal-mapping and indistinguishable at these strengths.
float3 MetohBlendNormals(float3 a, float3 b)
{
    return normalize(float3(a.xy + b.xy, a.z * b.z));
}

/// <summary>
/// The whole ground surface, at a world position with its geometric normal.
/// </summary>
SurfaceMix MixSnowpack(float3 positionWS, float3 geoNormal)
{
    float2 uv = positionWS.xz;

    // --- how steep is this ground -------------------------------------------------
    // Gradient (rise/run), NOT `1 - n.y`. See the header comment in Snowpack.shader: on terrain this
    // gentle, `1 - n.y` never leaves the bottom 6% of its range and no threshold in it is tunable.
    float gradient = length(geoNormal.xz) / max(geoNormal.y, 0.05);

    // Break the snow/rock boundary with noise so it follows the land instead of drawing a clean
    // contour line around it. A perfectly smooth threshold on a smooth height field reads as a
    // topographic map, which is a very particular kind of wrong.
    float jitter = (MetohFbm2(uv * 0.045) - 0.5) * _SlopeJitter * 0.24;
    float rock = smoothstep(_SlopeStart, _SlopeEnd, gradient + jitter);

    // --- the deep-snow basin ------------------------------------------------------
    // Straight from Movement.DeepSnowDepth's own constants (WorldBuilder feeds them in). Ground the
    // sim slows you on now LOOKS like ground that would slow you down.
    float drift = saturate((_DriftHeight - positionWS.y) / max(_DriftDepth, 0.01));
    drift *= (1.0 - rock) * _DriftStrength; // bare rock is never a drift

    // --- albedo -------------------------------------------------------------------
    half3 albedo = lerp(_BaseColor.rgb, _DriftColor.rgb, drift);
    albedo = lerp(albedo, _RockColor.rgb, rock);

    // Wind scour: broad, low-contrast value variation. Snow is never one value over 40 m — it is
    // scoured to a crust on the windward side and piled soft on the lee, and that slow undulation is
    // most of what gives an open field any sense of scale.
    float macro = MetohFbm2(uv / max(_MacroTiling, 1.0));
    albedo *= lerp(1.0 - _MacroStrength * 0.5, 1.0 + _MacroStrength * 0.5, macro);

    // --- normals ------------------------------------------------------------------
    float3 snowN = UnpackNormalScale(
        SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uv / max(_SnowTiling, 0.01)), _SnowNormalScale);
    float3 rockN = UnpackNormalScale(
        SAMPLE_TEXTURE2D(_RockMap, sampler_RockMap, uv / max(_RockTiling, 0.01)), _RockNormalScale);
    float3 n = lerp(snowN, rockN, rock);

    // Fine grain, faded out with distance. A 0.7 m tiling is well past its useful range by 60 m and
    // past that it is pure shimmer — it aliases into a crawling sparkle that costs bandwidth to
    // produce and looks worse than nothing.
    float dist = distance(positionWS, _WorldSpaceCameraPos);
    float detailFade = saturate(1.0 - dist / 60.0);
    if (detailFade > 0.001)
    {
        float3 detailN = UnpackNormalScale(
            SAMPLE_TEXTURE2D(_DetailMap, sampler_DetailMap, uv / max(_DetailTiling, 0.01)),
            _DetailScale * detailFade * (1.0 - rock));
        n = MetohBlendNormals(n, detailN);
    }

    // --- response -----------------------------------------------------------------
    // Packed snow is mildly specular and that broken specular lobe IS the glitter. Rock is a light
    // sink. Fresh basin drift is duller than wind-packed snow — it has not been compacted, so it
    // scatters rather than reflecting, and that difference is a second, subtler cue for the slow zone.
    half smoothness = lerp(_SnowSmoothness, _RockSmoothness, rock);
    smoothness = lerp(smoothness, _SnowSmoothness * 0.55, drift);

    SurfaceMix o;
    o.albedo = albedo;
    o.smoothness = smoothness;
    o.normalTS = n;
    return o;
}

#endif // METOH_SNOWPACK_INCLUDED
