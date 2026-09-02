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
//
// ------------------------------------------------------------------------------------------------
// **THE HALO BUG, AND THE THREE FADES THAT FIX IT.** Owner report: with the flashlight on, a halo sat
// around the whole screen. That was this shader, and it was geometry doing exactly what it was told.
//
// The cone's apex is at the holder's HAND and their camera is ~0.5 m behind it, so the camera sits
// essentially AT the apex of a 62-degree cone. A cone seen from its own apex does not project as a
// cone — its lateral surface spreads across the entire field of view, `Cull Off` draws the inside of
// it, and every one of those pixels adds light. The existing `edge` and `along` fades could not help,
// because both are computed in the cone's own UV space: along the axis they evaluate to their
// BRIGHTEST values right where the surface covers the most screen. The result is a full-screen wash
// centred on the beam, which is precisely a halo. Anyone a teammate pointed a torch at got it too.
//
// The geometric model is only valid from the SIDE — the cone stands in for the depth of glowing air a
// view ray crosses, and that approximation collapses when you look down the axis. So:
//
//   _AxisFade / _AxisFloor  Fade the shaft out as the view direction lines up with the beam axis.
//                           This is the fix. Not to zero: in real fog you do see your own beam, so a
//                           floor keeps a whisper of it, small enough that squaring it in the blend
//                           below leaves a suggestion instead of a wash.
//   _NearFade               Fade in over the first few metres from the CAMERA (world distance, not
//                           beam UV), so nothing is ever pasted across the lens.
//   _DepthFade              Soft-particle fade against the depth buffer, so the cone dissolves as it
//                           approaches a surface instead of showing the hard bright line where a
//                           triangle intersects the snow. This one is not about the halo — it is the
//                           single biggest "that is a piece of geometry, not air" tell the beam had.
//
// The blend and _Intensity are deliberately UNCHANGED. `Blend SrcAlpha One` with a premultiplied
// colour squares the falloff, which is a tuned look, and every term added here can only ever make the
// beam dimmer — the right risk profile for a fix to "it is too bright and it is everywhere".
Shader "Metoh/TorchBeam"
{
    Properties
    {
        _Color      ("Colour", Color) = (1, 0.91, 0.77, 1)
        _Intensity  ("Intensity", Range(0, 2)) = 0.30
        _EdgeFade   ("Edge fade", Range(0.5, 8)) = 2.6
        _RangeFade  ("Range fade", Range(0.5, 8)) = 1.7
        _AxisFade   ("Axis fade", Range(0.5, 8)) = 2.2
        _AxisFloor  ("Axis floor", Range(0, 1)) = 0.16
        _NearFade   ("Near fade (m)", Range(0.05, 12)) = 3.5
        _DepthFade  ("Depth fade (m)", Range(0.05, 12)) = 1.6
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
            // Cull Off because a viewer can be inside the cone — the holder always is, and anyone
            // being swept by a teammate's beam briefly is too. With backface culling they would be the
            // people who never see it.
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            // Gives SampleSceneDepth. The pipeline asset has RequireDepthTexture on
            // (RenderPipelineSetup asserts it), which is what makes the soft fade below possible at
            // all; without it this samples a blank texture and the fade simply reads as fully open.
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float4 screenPos  : TEXCOORD2;
                // View-space depth, carried explicitly. SV_POSITION.w is not reliably the clip w
                // across platforms in a fragment shader, and getting this wrong makes the depth fade
                // subtly wrong at the screen edges rather than obviously broken.
                float  viewZ      : TEXCOORD3;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float  _Intensity;
                float  _EdgeFade;
                float  _RangeFade;
                float  _AxisFade;
                float  _AxisFloor;
                float  _NearFade;
                float  _DepthFade;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionWS = posWS;
                OUT.positionCS = TransformWorldToHClip(posWS);
                OUT.screenPos = ComputeScreenPos(OUT.positionCS);
                OUT.viewZ = -TransformWorldToView(posWS).z;
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // uv.x = 0 on the axis, 1 at the cone wall. uv.y = 0 at the lens, 1 at the far end.
                half edge  = pow(saturate(1.0 - IN.uv.x), _EdgeFade);
                half along = pow(saturate(1.0 - IN.uv.y), _RangeFade);

                // The beam's own axis in world space is the object's +Z — the direction a Unity spot
                // light shines and the axis BuildCone lays the mesh along.
                float3 axis = normalize(float3(unity_ObjectToWorld[0][2],
                                               unity_ObjectToWorld[1][2],
                                               unity_ObjectToWorld[2][2]));
                float3 toCam = _WorldSpaceCameraPos - IN.positionWS;
                float  camDist = length(toCam);
                float3 viewDir = toCam / max(camDist, 1e-4);

                // THE FIX. 1 when looking across the beam, 0 when looking along it.
                half across = pow(saturate(1.0 - abs(dot(viewDir, axis))), _AxisFade);
                half axisFade = lerp(_AxisFloor, 1.0, across);

                // Keep the shaft off the lens. World distance, so it does not depend on where the
                // apex happens to be relative to the eye.
                half nearFade = smoothstep(0.0, _NearFade, camDist);

                // Soft particles: dissolve where the cone meets solid geometry.
                float2 screenUV = IN.screenPos.xy / max(IN.screenPos.w, 1e-4);
                float  sceneEye = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);
                half   depthFade = saturate((sceneEye - IN.viewZ) / _DepthFade);

                half a = edge * along * axisFade * nearFade * depthFade * _Intensity;
                return half4(_Color.rgb * a, a);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
