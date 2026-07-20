// Wren's (Tracking) team-visible trail marker — a small survey stake with an orange flag,
// dropped at her feet. Server-gated (specialty + cooldown) and expired by GameManager
// (TRACKING_MARK: 8 s cooldown, 50 s lifetime, 24 max — from shared/sim/specialties.ts).
// Like ClueMarker this is pure visuals client-side; position arrives with the spawn payload.
// MapView draws the amber diamond for each live mark off the registry below.
using System.Collections.Generic;
using FishNet.Object;
using UnityEngine;

namespace HollowPines.Game
{
    public class TrailMark : NetworkBehaviour
    {
        /// <summary>Live marks on this client — the map draws a dot per entry (searchers only).</summary>
        public static readonly List<TrailMark> All = new List<TrailMark>();

        public override void OnStopClient()
        {
            All.Remove(this);
        }

        public override void OnStartClient()
        {
            All.Add(this);

            var root = new GameObject("MarkVisual").transform;
            root.SetParent(transform, false);

            // Stake.
            var stake = new GameObject("Stake");
            stake.transform.SetParent(root, false);
            stake.AddComponent<MeshFilter>().sharedMesh = MeshUtil.TaperedCylinder(0.035f, 0.02f, 1.0f, 5);
            stake.AddComponent<MeshRenderer>().sharedMaterial = MeshUtil.Lit(MeshUtil.Rgb(0x8a7a5a));

            // Flag — emissive orange so it reads across a dark clearing.
            var flag = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(flag.GetComponent<UnityEngine.Collider>());
            flag.transform.SetParent(root, false);
            flag.transform.localPosition = new Vector3(0.14f, 0.88f, 0f);
            flag.transform.localScale = new Vector3(0.28f, 0.18f, 0.02f);
            flag.GetComponent<MeshRenderer>().sharedMaterial =
                MeshUtil.Emissive(MeshUtil.Rgb(0xff7a2a), MeshUtil.Rgb(0xff5a1e), 1.6f);
        }
    }
}
