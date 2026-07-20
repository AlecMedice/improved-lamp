// A spawned clue — Bigfoot's footprint or a snapped branch. Server places it (position/yaw/type
// set before Spawn so they arrive with the payload) and despawns it when the trail goes cold
// (GameManager owns the lifetime; escalation shortens it on later nights). Client-side this is
// pure visuals: primitive meshes with a faint glow so evidence reads at night.
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace HollowPines.Game
{
    public class ClueMarker : NetworkBehaviour
    {
        public const byte TypeFootprint = 0;
        public const byte TypeBranch = 1;

        public readonly SyncVar<byte> CType = new SyncVar<byte>(TypeFootprint);
        public readonly SyncVar<float> YawRad = new SyncVar<float>(0f);
        /// <summary>
        /// This print landed in ground soft and deep enough to take a plaster cast — the only kind
        /// worth working. Bigfoot does not shed evidence; it leaves TRACKS, and a cast is something a
        /// person makes from one. Only Mara (analysis) has the kit and training to do it.
        /// Set by the server at spawn; a limited number are live at once (newer prints override older).
        /// </summary>
        public readonly SyncVar<bool> Castable = new SyncVar<bool>(false);

        /// <summary>Castable prints only — the subset the map, prompts and casting target scan.</summary>
        public static readonly List<ClueMarker> Castables = new List<ClueMarker>();

        /// <summary>Live clues on this client — the map's "recent trail" reads this (see MapView).</summary>
        public static readonly List<ClueMarker> All = new List<ClueMarker>();
        /// <summary>Time.time this clue appeared here; the map only shows clues younger than the clue window.</summary>
        public float Born { get; private set; }

        public override void OnStopClient()
        {
            All.Remove(this);
            Castables.Remove(this);
        }

        public override void OnStartClient()
        {
            All.Add(this);
            if (Castable.Value) Castables.Add(this);
            Born = Time.time;

            var root = new GameObject("ClueVisual").transform;
            root.SetParent(transform, false);
            root.localRotation = Quaternion.Euler(0f, YawRad.Value * Mathf.Rad2Deg + 180f, 0f);

            if (CType.Value == TypeFootprint)
            {
                // A big two-pad print pressed into the ground, faintly luminous. A CASTABLE print sits
                // deeper in softer ground: bigger, darker, ringed with displaced earth, and marked with
                // a pale glint so it reads as workable from a distance.
                bool deep = Castable.Value;
                var mat = deep
                    ? MeshUtil.Emissive(MeshUtil.Rgb(0x120e08), MeshUtil.Rgb(0xc8b88a), 0.5f)
                    : MeshUtil.Emissive(MeshUtil.Rgb(0x1c1710), MeshUtil.Rgb(0x33bb77), 0.35f);
                float s = deep ? 1.25f : 1f;
                AddPad(root, new Vector3(0f, 0.03f, 0.10f), new Vector3(0.34f * s, 0.02f, 0.52f * s), mat); // sole
                AddPad(root, new Vector3(0f, 0.03f, 0.48f * s), new Vector3(0.26f * s, 0.02f, 0.18f * s), mat); // toes

                if (deep)
                {
                    // Displaced earth around the rim — the visual tell of a print worth casting.
                    var rim = new GameObject("Rim");
                    rim.transform.SetParent(root, false);
                    rim.transform.localPosition = new Vector3(0f, 0.012f, 0.16f);
                    rim.AddComponent<MeshFilter>().sharedMesh = MeshUtil.EllipseDisc(0.46f, 0.62f, 16);
                    rim.AddComponent<MeshRenderer>().sharedMaterial = MeshUtil.Lit(MeshUtil.Rgb(0x2b2115));

                    var glint = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    Destroy(glint.GetComponent<Collider>());
                    glint.transform.SetParent(root, false);
                    glint.transform.localScale = Vector3.one * 0.07f;
                    glint.transform.localPosition = new Vector3(0f, 0.55f, 0.2f);
                    glint.GetComponent<MeshRenderer>().sharedMaterial =
                        MeshUtil.Emissive(Color.black, MeshUtil.Rgb(0xe8d8a0), 2.6f);
                    _glint = glint.transform;
                    _glintScale = _glint.localScale;
                }
            }
            else
            {
                // A snapped branch — audible from a distance, which is how a searcher finds the trail.
                if (HPAudio.Instance != null)
                    HPAudio.Instance.PlayAt(HPAudio.BranchSnap, transform.position, 0.5f, 14f);

                // Two crossed sticks with pale broken ends.
                var wood = MeshUtil.Lit(MeshUtil.Rgb(0x6a4a2c));
                AddStick(root, new Vector3(-0.15f, 0.06f, 0f), Quaternion.Euler(0f, 15f, 80f), 0.8f, wood);
                AddStick(root, new Vector3(0.18f, 0.05f, 0.08f), Quaternion.Euler(0f, -40f, 95f), 0.6f, wood);
            }

            CacheMaterials(root); // must run after every piece exists — Update fades these
        }

        private Transform _glint;
        private readonly List<Material> _mats = new List<Material>();
        private readonly List<Color> _baseCols = new List<Color>();
        private readonly List<Color> _emisCols = new List<Color>();
        private Vector3 _glintScale;
        /// <summary>Fraction of the lifetime a clue stays at full strength before it starts fading.</summary>
        private const float HoldFraction = 0.5f;
        /// <summary>Colour a cold trail sinks toward — the forest floor swallowing it.</summary>
        private static readonly Color ColdCol = MeshUtil.Rgb(0x1a160f);

        /// <summary>Cache each renderer's own material instance so this clue can fade independently.</summary>
        private void CacheMaterials(Transform root)
        {
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>())
            {
                Material m = r.sharedMaterial;
                if (m == null || _mats.Contains(m)) continue;
                _mats.Add(m);
                _baseCols.Add(m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : Color.white);
                _emisCols.Add(m.HasProperty("_EmissionColor") ? m.GetColor("_EmissionColor") : Color.black);
            }
        }

        /// <summary>
        /// A trail going cold, made visible. Clues hold full strength for the first half of their
        /// life, then dim and sink toward the forest floor — so freshness is readable at a glance
        /// instead of every print looking identical right up to the instant it vanishes. This is
        /// what makes Wren's longer clue window and Mara's casting deadline mean something in-world.
        /// Driven by the host's own (escalating) lifetime, so the visuals can't drift from the
        /// replicated trail.
        /// </summary>
        private void Update()
        {
            float life = GameManager.Instance != null ? GameManager.Instance.ClueLifetimeSec.Value : 50f;
            float age01 = Mathf.Clamp01((Time.time - Born) / Mathf.Max(1f, life));
            float cold = Mathf.InverseLerp(HoldFraction, 1f, age01); // 0 = fresh, 1 = gone
            float strength = 1f - cold;

            for (int i = 0; i < _mats.Count; i++)
            {
                _mats[i].SetColor("_BaseColor", Color.Lerp(_baseCols[i], ColdCol, cold * 0.85f));
                _mats[i].SetColor("_EmissionColor", _emisCols[i] * strength);
            }

            // Slow bob so a castable print reads as workable, not as scenery — and it shrinks as the
            // print goes cold, which is Mara's cue that this one is about to stop being castable.
            if (_glint != null)
            {
                _glint.localPosition = new Vector3(0f, 0.5f + Mathf.Sin(Time.time * 1.7f) * 0.07f, 0.2f);
                _glint.localScale = _glintScale * Mathf.Max(0.15f, strength);
            }
        }

        private static void AddPad(Transform parent, Vector3 pos, Vector3 size, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = size;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        }

        private static void AddStick(Transform parent, Vector3 pos, Quaternion rot, float len, Material mat)
        {
            var go = new GameObject("Stick");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = rot;
            go.AddComponent<MeshFilter>().sharedMesh = MeshUtil.TaperedCylinder(0.035f, 0.028f, len, 5);
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        }
    }
}
