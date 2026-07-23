// A searcher's spilled pack — everything they were carrying when Bigfoot put them down.
// The server spawns it at the grab (contents set before Spawn so they arrive with the payload) and
// despawns it when it's recovered or when it goes cold. Client-side this is pure visuals: a burst
// pack with its contents scattered around it, lit brightly enough to find in the dark, because a
// pile nobody can locate is the same thing as destroying it.
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace HollowPines.Game
{
    public class ProofPile : NetworkBehaviour
    {
        public readonly SyncVar<int> Film = new SyncVar<int>(0);
        public readonly SyncVar<int> Casts = new SyncVar<int>(0);
        public readonly SyncVar<int> Hair = new SyncVar<int>(0);
        /// <summary>Who dropped it — the HUD names them, so a recovery run has an owner to shout about.</summary>
        public readonly SyncVar<string> OwnerName = new SyncVar<string>("");

        public int Total => Film.Value + Casts.Value + Hair.Value;

        /// <summary>Live piles on this client — the map and the pickup prompt both read this.</summary>
        public static readonly List<ProofPile> All = new List<ProofPile>();

        private Transform _beacon;
        private Light _lamp;

        public override void OnStopClient() { All.Remove(this); }

        public override void OnStartClient()
        {
            All.Add(this);

            var root = new GameObject("PileVisual").transform;
            root.SetParent(transform, false);

            // The pack itself, tipped over and open.
            var canvas = MeshUtil.Lit(MeshUtil.Rgb(0x3a3228));
            var bag = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(bag.GetComponent<Collider>());
            bag.transform.SetParent(root, false);
            bag.transform.localPosition = new Vector3(0f, 0.16f, 0f);
            bag.transform.localRotation = Quaternion.Euler(-12f, 24f, 8f);
            bag.transform.localScale = new Vector3(0.62f, 0.3f, 0.42f);
            bag.GetComponent<MeshRenderer>().sharedMaterial = canvas;

            // Its contents, thrown clear. Tapes and casts read differently so you can tell at a
            // glance what's lying there — the same split the duffel manifest uses.
            var tapeMat = MeshUtil.Emissive(MeshUtil.Rgb(0x1d1a16), MeshUtil.Rgb(0x4fd08a), 0.5f);
            var castMat = MeshUtil.Emissive(MeshUtil.Rgb(0xd8cfb4), MeshUtil.Rgb(0xc8b88a), 0.35f);
            var hairMat = MeshUtil.Emissive(MeshUtil.Rgb(0x2a221a), MeshUtil.Rgb(0x9a7f5a), 0.4f);

            // Deterministic scatter from the object id, so every client draws the same spill.
            var rng = new System.Random(ObjectId);
            for (int i = 0; i < Film.Value; i++) AddTape(root, rng, tapeMat);
            for (int i = 0; i < Casts.Value; i++) AddCast(root, rng, castMat);
            for (int i = 0; i < Hair.Value; i++) AddTuft(root, rng, hairMat);

            // A soft column of light so it can be found from across the clearing at night. This is the
            // whole reason a dropped pile is recoverable rather than theoretically recoverable.
            var beacon = new GameObject("Beacon");
            beacon.transform.SetParent(root, false);
            beacon.transform.localPosition = new Vector3(0f, 1.5f, 0f);
            beacon.AddComponent<MeshFilter>().sharedMesh = MeshUtil.TaperedCylinder(0.16f, 0.03f, 3f, 6);
            beacon.AddComponent<MeshRenderer>().sharedMaterial =
                MeshUtil.Emissive(Color.black, MeshUtil.Rgb(0xffc46b), 2.2f);
            _beacon = beacon.transform;

            var lampGo = new GameObject("PileLamp");
            lampGo.transform.SetParent(root, false);
            lampGo.transform.localPosition = new Vector3(0f, 0.6f, 0f);
            _lamp = lampGo.AddComponent<Light>();
            _lamp.type = LightType.Point;
            _lamp.color = MeshUtil.Rgb(0xffc46b);
            _lamp.range = 9f;
            _lamp.intensity = 1.4f;
        }

        private static void AddTape(Transform parent, System.Random rng, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Scatter(rng, 0.10f);
            go.transform.localRotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);
            go.transform.localScale = new Vector3(0.20f, 0.05f, 0.13f);
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        }

        private static void AddCast(Transform parent, System.Random rng, Material mat)
        {
            var go = new GameObject("Cast");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Scatter(rng, 0.06f);
            go.transform.localRotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);
            go.AddComponent<MeshFilter>().sharedMesh = MeshUtil.EllipseDisc(0.15f, 0.21f, 12);
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        }

        private static void AddTuft(Transform parent, System.Random rng, Material mat)
        {
            var go = new GameObject("Tuft");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Scatter(rng, 0.09f);
            go.transform.localRotation = Quaternion.Euler(90f, (float)rng.NextDouble() * 360f, 0f);
            go.AddComponent<MeshFilter>().sharedMesh = MeshUtil.TaperedCylinder(0.075f, 0.02f, 0.14f, 5);
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        }

        private static Vector3 Scatter(System.Random rng, float y)
        {
            double a = rng.NextDouble() * System.Math.PI * 2, r = 0.35 + rng.NextDouble() * 0.45;
            return new Vector3((float)(System.Math.Cos(a) * r), y, (float)(System.Math.Sin(a) * r));
        }

        /// <summary>
        /// The beacon breathes so it reads as a live objective rather than scenery, and it climbs as
        /// the pile ages — the last thing a searcher sees before it goes is the light getting urgent.
        /// </summary>
        private void Update()
        {
            if (_beacon == null) return;
            float pulse = 0.75f + 0.25f * Mathf.Sin(Time.time * 2.4f);
            _beacon.localScale = new Vector3(1f, 0.85f + 0.15f * pulse, 1f);
            if (_lamp != null) _lamp.intensity = 1.1f + 0.5f * pulse;
        }
    }
}
