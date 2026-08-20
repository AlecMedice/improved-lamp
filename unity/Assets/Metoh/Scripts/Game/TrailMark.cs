// Wren's (Tracking) team-visible trail marker — a small survey stake with an orange flag,
// dropped at her feet. Server-gated (specialty + cooldown) and expired by GameManager
// (TRACKING_MARK: 8 s cooldown, 50 s lifetime, 24 max — from shared/sim/specialties.ts).
// Like ClueMarker this is pure visuals client-side; position arrives with the spawn payload.
// MapView draws the amber diamond for each live mark off the registry below.
using System.Collections.Generic;
using FishNet.Object;
using UnityEngine;

namespace Metoh.Game
{
    public class TrailMark : NetworkBehaviour
    {
        /// <summary>Live marks on this client — the map draws a dot per entry (searchers only).</summary>
        public static readonly List<TrailMark> All = new List<TrailMark>();

        public override void OnStopClient()
        {
            All.Remove(this);
        }

        // Every mark is the same object, so its mesh and its two materials are built ONCE and shared.
        // They used to be minted per instance and never released — `new Mesh`/`new Material` are
        // native objects the GC does not collect, and dropping the GameObject does not take them with
        // it, so a match's worth of marks (24 live, expiring and respawning on an 8 s cooldown all
        // night) leaked steadily. Nothing here is mutated per instance, which is what makes sharing
        // safe; the clue trail cannot do the same for its materials because it fades each one.
        //
        // Lazily re-created: entering play mode destroys them, and the `== null` check is what stops
        // the next session handing out a dead reference.
        private static Mesh _stakeMesh;
        private static Material _stakeMat, _flagMat;

        public override void OnStartClient()
        {
            All.Add(this);

            if (_stakeMesh == null) _stakeMesh = MeshUtil.TaperedCylinder(0.035f, 0.02f, 1.0f, 5);
            if (_stakeMat == null) _stakeMat = MeshUtil.Lit(MeshUtil.Rgb(0x8a7a5a));
            if (_flagMat == null)
                _flagMat = MeshUtil.Emissive(MeshUtil.Rgb(0xff7a2a), MeshUtil.Rgb(0xff5a1e), 1.6f);

            var root = new GameObject("MarkVisual").transform;
            root.SetParent(transform, false);

            // Stake.
            var stake = new GameObject("Stake");
            stake.transform.SetParent(root, false);
            stake.AddComponent<MeshFilter>().sharedMesh = _stakeMesh;
            stake.AddComponent<MeshRenderer>().sharedMaterial = _stakeMat;

            // Flag — emissive orange so it reads across a dark clearing.
            var flag = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(flag.GetComponent<UnityEngine.Collider>());
            flag.transform.SetParent(root, false);
            flag.transform.localPosition = new Vector3(0.14f, 0.88f, 0f);
            flag.transform.localScale = new Vector3(0.28f, 0.18f, 0.02f);
            flag.GetComponent<MeshRenderer>().sharedMaterial = _flagMat;
        }
    }
}
