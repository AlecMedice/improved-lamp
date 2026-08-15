#ifndef METOH_SWAY_INCLUDED
#define METOH_SWAY_INCLUDED

// The sway displacement, shared by every pass of Metoh/TreeSway.
//
// IT LIVES IN AN INCLUDE FOR A REASON. The forward, shadow and depth passes must apply the EXACT
// same displacement. If they drift even slightly, the tree renders in one place, casts its shadow
// from another and writes depth from a third — which shows up as shadows sliding off their trunks and
// SSAO haloing empty air, and it is miserable to diagnose because each pass looks correct alone.
// One function, included three times, makes that class of bug impossible rather than merely unlikely.
//
// sway.x = weight  (0 at this tree's base, ~1 at its crown — baked by WorldBuilder.BakeSwayData)
// sway.y = phase   (per-tree constant, so neighbours do not move in lockstep)

float3 ApplySway(float3 positionWS, float2 sway, float3 windDir, float strength, float speed, float gustScale)
{
    float weight = sway.x;
    // Cheap early-out for trunk bases, which are the majority of the vertices in a chunk.
    if (weight <= 0.001) return positionWS;

    float phase = sway.y * 6.2831853;
    float t = _Time.y * speed;

    // GUST: a slow travelling wave across the world, so the wind arrives somewhere before it arrives
    // everywhere. Sampled from world position rather than time alone — that is what makes it read as
    // weather moving through the valley instead of the whole forest breathing in unison.
    float travel = dot(positionWS.xz, normalize(windDir.xz)) / max(gustScale, 1.0);
    float gust = 0.65 + 0.35 * sin(t * 0.37 - travel);

    // TRUNK LEAN: the slow one. Weight is squared here so the bend accelerates up the tree — a real
    // trunk is stiff at the base and whippy at the top, and a linear ramp reads as a hinge.
    float lean = sin(t + phase) * weight * weight;

    // BRANCH FLUTTER: faster, much smaller, and deliberately not a harmonic of the lean. Sharing a
    // frequency would make the tree pump like a spring rather than be pushed by air.
    float flutter = sin(t * 3.1 + phase * 1.7) * 0.22 * weight;

    float amount = (lean + flutter) * gust * strength;

    float3 dir = normalize(float3(windDir.x, 0, windDir.z));
    float3 offset = dir * amount;
    // A leaning tree's tip also drops slightly, because the trunk does not stretch. Small, but its
    // absence is what makes naive sway look like the treetops are sliding on a plane.
    offset.y -= abs(amount) * 0.18 * weight;

    return positionWS + offset;
}

#endif
