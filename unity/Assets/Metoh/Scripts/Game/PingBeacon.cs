// A hunter's stakeout ping — the marker a searcher drops (Q, or by clicking the map) so the team
// can converge on a spot. Server-owned lifetime (GameManager: one live ping per hunter, 35 s,
// 12 max); this is the client-side beacon, ported from client/src/world/PingField.ts: a bright
// vertical beam plus a ground ring, readable across a dark clearing. MapView draws its map dot.
using System.Collections.Generic;
using FishNet.Object;
using UnityEngine;

namespace Metoh.Game
{
    public class PingBeacon : NetworkBehaviour
    {
        /// <summary>Live pings on this client (the map reads this; searchers only draw them).</summary>
        public static readonly List<PingBeacon> All = new List<PingBeacon>();

        private static readonly Color PingColor = MeshUtil.Rgb(0xffe24a);

        public override void OnStopClient()
        {
            All.Remove(this);
        }

        // Shared once, not per beacon — see TrailMark for the reasoning. Pings churn: one per hunter,
        // re-pinging moves it (despawn + respawn) and they expire on a 35 s lifetime, so an active
        // team cycles through a lot of them over three nights and every one used to leak two meshes
        // and a material.
        private static Mesh _beamMesh, _ringMesh;
        private static Material _glowMat;

        public override void OnStartClient()
        {
            All.Add(this);

            if (_beamMesh == null) _beamMesh = MeshUtil.TaperedCylinder(0.12f, 0.12f, 14f, 8);
            if (_ringMesh == null) _ringMesh = MeshUtil.EllipseDisc(0.9f, 0.9f, 18);
            if (_glowMat == null) _glowMat = MeshUtil.Emissive(Color.black, PingColor, 2.4f);

            var root = new GameObject("PingVisual").transform;
            root.SetParent(transform, false);

            // Beam: a thin 14 m column so it clears the canopy from a distance.
            var beam = new GameObject("Beam");
            beam.transform.SetParent(root, false);
            beam.transform.localPosition = Vector3.zero;
            beam.AddComponent<MeshFilter>().sharedMesh = _beamMesh;
            beam.AddComponent<MeshRenderer>().sharedMaterial = _glowMat;

            // Ground ring: a flat disc at the base marking the exact spot.
            var ring = new GameObject("Ring");
            ring.transform.SetParent(root, false);
            ring.transform.localPosition = new Vector3(0f, 0.12f, 0f);
            ring.AddComponent<MeshFilter>().sharedMesh = _ringMesh;
            ring.AddComponent<MeshRenderer>().sharedMaterial = _glowMat;
        }
    }
}
