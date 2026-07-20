// A hunter's stakeout ping — the marker a searcher drops (Q, or by clicking the map) so the team
// can converge on a spot. Server-owned lifetime (GameManager: one live ping per hunter, 35 s,
// 12 max); this is the client-side beacon, ported from client/src/world/PingField.ts: a bright
// vertical beam plus a ground ring, readable across a dark clearing. MapView draws its map dot.
using System.Collections.Generic;
using FishNet.Object;
using UnityEngine;

namespace HollowPines.Game
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

        public override void OnStartClient()
        {
            All.Add(this);

            var root = new GameObject("PingVisual").transform;
            root.SetParent(transform, false);
            var glow = MeshUtil.Emissive(Color.black, PingColor, 2.4f);

            // Beam: a thin 14 m column so it clears the canopy from a distance.
            var beam = new GameObject("Beam");
            beam.transform.SetParent(root, false);
            beam.transform.localPosition = Vector3.zero;
            beam.AddComponent<MeshFilter>().sharedMesh = MeshUtil.TaperedCylinder(0.12f, 0.12f, 14f, 8);
            beam.AddComponent<MeshRenderer>().sharedMaterial = glow;

            // Ground ring: a flat disc at the base marking the exact spot.
            var ring = new GameObject("Ring");
            ring.transform.SetParent(root, false);
            ring.transform.localPosition = new Vector3(0f, 0.12f, 0f);
            ring.AddComponent<MeshFilter>().sharedMesh = MeshUtil.EllipseDisc(0.9f, 0.9f, 18);
            ring.AddComponent<MeshRenderer>().sharedMaterial = glow;
        }
    }
}
