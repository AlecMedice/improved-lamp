// A searcher's spilled pack — everything they were carrying when Yeti put them down.
// The server spawns it at the grab (contents set before Spawn so they arrive with the payload) and
// despawns it when it's recovered or when it goes cold. Client-side this is pure visuals: a burst
// pack with its contents scattered around it, lit brightly enough to find in the dark, because a
// pile nobody can locate is the same thing as destroying it.
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace Metoh.Game
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

        public override void OnStopClient()
        {
            All.Remove(this);
            // Only the bag is this pile's own — everything else is shared (see below). `new Mesh` is
            // a native object the GC never collects and destroying the GameObject leaves it behind.
            if (_bagMesh != null) { Destroy(_bagMesh); _bagMesh = null; }
        }

        private void OnDestroy()
        {
            if (_bagMesh != null) { Destroy(_bagMesh); _bagMesh = null; }
        }

        /// <summary>This pile's own bag mesh — hashed from ObjectId, so it cannot be shared.</summary>
        private Mesh _bagMesh;

        // Everything else IS identical between piles, so it is built once for the session rather than
        // per spill. Lazily re-created: play mode destroys them and the `== null` check rebuilds.
        private static Mesh _tapeMesh, _castMesh, _tuftMesh, _beaconMesh;
        private static Material _canvasMat, _tapeMat, _castMat, _hairMat, _beaconMat;

        public override void OnStartClient()
        {
            All.Add(this);
            EnsureShared();

            var root = new GameObject("PileVisual").transform;
            root.SetParent(transform, false);

            // The pack itself, tipped over and open. Not a box: this is a canvas bag that has been
            // dropped and spilled, and the whole read of a proof pile is "something went wrong here".
            // A crisp rectangle says the opposite — it says somebody set it down.
            var bag = new GameObject("Bag");
            bag.transform.SetParent(root, false);
            bag.transform.localPosition = new Vector3(0f, 0.15f, 0f);
            bag.transform.localRotation = Quaternion.Euler(-12f, 24f, 8f);
            _bagMesh = MeshUtil.Blob(0.31f, 0.15f, 0.21f, 6, 10, ObjectId * 13, 0.22f);
            bag.AddComponent<MeshFilter>().sharedMesh = _bagMesh;
            bag.AddComponent<MeshRenderer>().sharedMaterial = _canvasMat;

            // Deterministic scatter from the object id, so every client draws the same spill.
            var rng = new System.Random(ObjectId);
            for (int i = 0; i < Film.Value; i++) AddTape(root, rng, _tapeMat);
            for (int i = 0; i < Casts.Value; i++) AddCast(root, rng, _castMat);
            for (int i = 0; i < Hair.Value; i++) AddTuft(root, rng, _hairMat);

            // A soft column of light so it can be found from across the clearing at night. This is the
            // whole reason a dropped pile is recoverable rather than theoretically recoverable.
            var beacon = new GameObject("Beacon");
            beacon.transform.SetParent(root, false);
            beacon.transform.localPosition = new Vector3(0f, 1.5f, 0f);
            beacon.AddComponent<MeshFilter>().sharedMesh = _beaconMesh;
            beacon.AddComponent<MeshRenderer>().sharedMaterial = _beaconMat;
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

        /// <summary>Build the shared meshes and materials if this session hasn't yet.</summary>
        private static void EnsureShared()
        {
            if (_canvasMat == null)
                _canvasMat = MeshUtil.Surface(MeshUtil.Rgb(0x3a3228), 0.18f, ProcTex.FabricNormal, 0.8f, 4f);
            // Tapes, casts and tufts read differently so you can tell at a glance what's lying there
            // — the same split the duffel manifest uses.
            if (_tapeMat == null) _tapeMat = MeshUtil.Emissive(MeshUtil.Rgb(0x1d1a16), MeshUtil.Rgb(0x4fd08a), 0.5f);
            if (_castMat == null) _castMat = MeshUtil.Emissive(MeshUtil.Rgb(0xd8cfb4), MeshUtil.Rgb(0xc8b88a), 0.35f);
            if (_hairMat == null) _hairMat = MeshUtil.Emissive(MeshUtil.Rgb(0x2a221a), MeshUtil.Rgb(0x9a7f5a), 0.4f);
            if (_beaconMat == null) _beaconMat = MeshUtil.Emissive(Color.black, MeshUtil.Rgb(0xffc46b), 2.2f);

            if (_castMesh == null) _castMesh = MeshUtil.EllipseDisc(0.15f, 0.21f, 12);
            if (_tuftMesh == null) _tuftMesh = MeshUtil.TaperedCylinder(0.075f, 0.02f, 0.14f, 5);
            if (_beaconMesh == null) _beaconMesh = MeshUtil.TaperedCylinder(0.16f, 0.03f, 3f, 6);
        }

        private static void AddTape(Transform parent, System.Random rng, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube); // built-in mesh, nothing to own
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
            go.AddComponent<MeshFilter>().sharedMesh = _castMesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        }

        private static void AddTuft(Transform parent, System.Random rng, Material mat)
        {
            var go = new GameObject("Tuft");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Scatter(rng, 0.09f);
            go.transform.localRotation = Quaternion.Euler(90f, (float)rng.NextDouble() * 360f, 0f);
            go.AddComponent<MeshFilter>().sharedMesh = _tuftMesh;
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
