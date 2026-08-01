// A spawned clue — Yeti's footprint or a snapped branch. Server places it (position/yaw/type
// set before Spawn so they arrive with the payload) and despawns it when the trail goes cold
// (GameManager owns the lifetime; escalation shortens it on later nights). Client-side this is
// pure visuals: primitive meshes with a faint glow so evidence reads at night.
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace Metoh.Game
{
    public class ClueMarker : NetworkBehaviour
    {
        public const byte TypeFootprint = 0;
        public const byte TypeBranch = 1;
        /// <summary>
        /// A tuft snagged on a branch. The one kind of evidence Yeti genuinely SHEDS rather than
        /// merely presses into the ground — which is why, unlike a cast, anyone can bag it. It exists
        /// so the second win path isn't gated entirely behind Mara being alive and present.
        /// </summary>
        public const byte TypeHair = 2;

        public readonly SyncVar<byte> CType = new SyncVar<byte>(TypeFootprint);
        public readonly SyncVar<float> YawRad = new SyncVar<float>(0f);
        /// <summary>
        /// This print landed in ground soft and deep enough to take a plaster cast — the only kind
        /// worth working. Yeti does not shed evidence; it leaves TRACKS, and a cast is something a
        /// person makes from one. Only Mara (analysis) has the kit and training to do it.
        /// Set by the server at spawn; a limited number are live at once (newer prints override older).
        /// </summary>
        public readonly SyncVar<bool> Castable = new SyncVar<bool>(false);

        /// <summary>Castable prints only — the subset the map, prompts and casting target scan.</summary>
        public static readonly List<ClueMarker> Castables = new List<ClueMarker>();

        /// <summary>Hair samples — same role as Castables, but collectable by any searcher.</summary>
        public static readonly List<ClueMarker> Hairs = new List<ClueMarker>();

        /// <summary>Is this clue something a searcher can turn into proof? (Prompt + map both ask.)</summary>
        public bool IsCollectable => Castable.Value || CType.Value == TypeHair;

        /// <summary>Live clues on this client — the map's "recent trail" reads this (see MapView).</summary>
        public static readonly List<ClueMarker> All = new List<ClueMarker>();
        /// <summary>Time.time this clue appeared here; the map only shows clues younger than the clue window.</summary>
        public float Born { get; private set; }

        public override void OnStopClient()
        {
            All.Remove(this);
            Castables.Remove(this);
            Hairs.Remove(this);
        }

        public override void OnStartClient()
        {
            All.Add(this);
            if (Castable.Value) Castables.Add(this);
            if (CType.Value == TypeHair) Hairs.Add(this);
            Born = Time.time;

            var root = new GameObject("ClueVisual").transform;
            root.SetParent(transform, false);
            root.localRotation = Quaternion.Euler(0f, YawRad.Value * Mathf.Rad2Deg + 180f, 0f);

            if (CType.Value == TypeFootprint)
            {
                // A big two-pad print pressed into the ground, faintly luminous. A CASTABLE print sits
                // deeper in softer ground: bigger, darker, ringed with displaced earth, and marked with
                // a pale glint so it reads as workable from a distance.
                // Snow prints read by their SHADOW, not their colour: a hollow pressed into white
                // pack is blue where the sky doesn't reach it. Hence dark blue bodies against the
                // pale ground rather than the old pale-on-dark forest treatment.
                bool deep = Castable.Value;
                var mat = deep
                    ? MeshUtil.Emissive(MeshUtil.Rgb(0x1b3450), MeshUtil.Rgb(0x9fc4e8), 0.5f)
                    : MeshUtil.Emissive(MeshUtil.Rgb(0x24405c), MeshUtil.Rgb(0x6fb8d8), 0.35f);
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
                    rim.AddComponent<MeshRenderer>().sharedMaterial = MeshUtil.Lit(MeshUtil.Rgb(0xe4eef5));

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
            else if (CType.Value == TypeBranch)
            {
                // Crust broken through where something heavy went past — audible from a distance,
                // which is how a searcher finds the trail. Same role the snapped branch had.
                if (HPAudio.Instance != null)
                    HPAudio.Instance.PlayAt(HPAudio.BranchSnap, transform.position, 0.5f, 14f); // renamed to IceCrack in the audio pass

                // A cracked slab: three tilted plates shoved out of the surface at angles, so the
                // silhouette breaks the flat ground the way the crossed sticks used to.
                var ice = MeshUtil.Emissive(MeshUtil.Rgb(0x7fa8c4), MeshUtil.Rgb(0xbfe0f0), 0.30f);
                AddSlab(root, new Vector3(-0.14f, 0.07f, 0f), Quaternion.Euler(18f, 15f, 6f), new Vector3(0.44f, 0.05f, 0.34f), ice);
                AddSlab(root, new Vector3(0.16f, 0.06f, 0.09f), Quaternion.Euler(-13f, -34f, -9f), new Vector3(0.36f, 0.05f, 0.30f), ice);
                AddSlab(root, new Vector3(0.02f, 0.10f, -0.16f), Quaternion.Euler(26f, 62f, 4f), new Vector3(0.26f, 0.04f, 0.22f), ice);
            }
            else
            {
                // Hair caught where Yeti pushed through: a low broken stub with a dark tuft snagged
                // on it, held at chest height so it reads against the ground rather than lost in it.
                var wood = MeshUtil.Lit(MeshUtil.Rgb(0x4a443c));
                AddStick(root, new Vector3(0f, 0.42f, 0f), Quaternion.Euler(0f, 20f, 14f), 0.9f, wood);

                // Pale coarse hair against dark bark — the Yeti's coat, not Bigfoot's.
                var fur = MeshUtil.Emissive(MeshUtil.Rgb(0x6e6a60), MeshUtil.Rgb(0xd8d2c4), 0.55f);
                for (int i = 0; i < 4; i++)
                {
                    var strand = new GameObject("Strand");
                    strand.transform.SetParent(root, false);
                    strand.transform.localPosition = new Vector3(-0.03f + i * 0.025f, 0.74f + i * 0.015f, 0.02f);
                    strand.transform.localRotation = Quaternion.Euler(64f + i * 9f, 30f * i, 0f);
                    strand.AddComponent<MeshFilter>().sharedMesh = MeshUtil.TaperedCylinder(0.022f, 0.004f, 0.26f, 4);
                    strand.AddComponent<MeshRenderer>().sharedMaterial = fur;
                }

                // Same pale glint the castable prints use — one visual language for "this is workable".
                var glint = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(glint.GetComponent<Collider>());
                glint.transform.SetParent(root, false);
                glint.transform.localScale = Vector3.one * 0.07f;
                glint.transform.localPosition = new Vector3(0f, 1.05f, 0.2f);
                glint.GetComponent<MeshRenderer>().sharedMaterial =
                    MeshUtil.Emissive(Color.black, MeshUtil.Rgb(0xe8d8a0), 2.6f);
                _glint = glint.transform;
                _glintScale = _glint.localScale;
                _glintBaseY = 1.0f;
            }

            CacheMaterials(root); // must run after every piece exists — Update fades these
        }

        private Transform _glint;
        private readonly List<Material> _mats = new List<Material>();
        private readonly List<Color> _baseCols = new List<Color>();
        private readonly List<Color> _emisCols = new List<Color>();
        private Vector3 _glintScale;
        /// <summary>Height the glint bobs around — a print's sits just off the ground, hair's up on the branch.</summary>
        private float _glintBaseY = 0.5f;
        /// <summary>Fraction of the lifetime a clue stays at full strength before it starts fading.</summary>
        private const float HoldFraction = 0.5f;
        /// <summary>Colour a cold trail sinks toward — the forest floor swallowing it.</summary>
        private static readonly Color ColdCol = MeshUtil.Rgb(0x2a3a48);

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
                _glint.localPosition = new Vector3(0f, _glintBaseY + Mathf.Sin(Time.time * 1.7f) * 0.07f, 0.2f);
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

        /// <summary>A tilted plate — like AddPad but rotatable, for broken crust shards.</summary>
        private static void AddSlab(Transform parent, Vector3 pos, Quaternion rot, Vector3 size, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = rot;
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
