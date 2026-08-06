// Jointed procedural bodies for the Yeti and the searchers, and the animation that drives them.
//
// WHAT THIS REPLACES. Every player in the game was ONE Unity primitive capsule — the Yeti a capsule
// scaled to 1.3/1.35/1.3 with two spheres stuck on for eyes, a searcher the same capsule at 0.8/0.9.
// No head, no arms, no legs, and no animation of any kind anywhere in the project: no Animator, no
// SkinnedMeshRenderer, not even a procedural limb. Bodies slid across the snow in a fixed pose.
//
// The silhouette argument from UNITY_PORT_NOTES [legibility] applies here harder than it did to the trees. At
// night, in fog, at the distance you actually see the Yeti from, the outline is nearly all the
// information reaching the player — and a capsule's outline is a pill. No amount of fur normal map
// (which [materials] duly added, and which was being applied to a pill) can put arms on it.
//
// WHY THERE IS NO RIG AND NO SKINNING. The project has no asset files and generates everything at
// runtime; a .fbx with a skeleton would break "clone it and it runs". So bodies here are a HIERARCHY
// OF SEPARATE MESHES moved by transform — a shoulder transform with an upper-arm mesh under it, an
// elbow transform under that, and so on. This costs nothing to generate, needs no bone weights, and
// at these viewing distances is indistinguishable from skinning provided the limb ends are ROUNDED so
// neighbouring parts overlap through their range of motion (see MeshUtil.Limb).
//
// EVERYTHING IS DRIVEN FROM DATA THAT IS ALREADY REPLICATED — horizontal speed, body yaw, Status,
// Crouched, Filming, GrabberObjectId. No new SyncVars and no new RPCs: the animation layer is
// strictly a read of state the match already agreed on, so it cannot desync and cannot be cheated.
// The one thing NOT available is head pitch, which the schema has never carried (`ry` is yaw only),
// so the head leads turns rather than tracking a look direction.
using UnityEngine;

namespace Metoh.Game
{
    /// <summary>Everything the animation layer needs, gathered once per frame by HPPlayer.</summary>
    public struct AvatarInput
    {
        /// <summary>Horizontal speed in m/s. Drives stride rate, so the feet match the ground.</summary>
        public float Speed;
        public bool Sprinting;
        public bool Crouched;
        /// <summary>HPPlayer.StatusActive / StatusFrozen / StatusIncap.</summary>
        public byte Status;
        public bool Filming;
        /// <summary>Yeti: hauling a searcher right now.</summary>
        public bool Carrying;
        /// <summary>Searcher: being hauled by the Yeti right now.</summary>
        public bool BeingCarried;
        /// <summary>Body yaw rate in rad/s — the lean into a turn comes from this.</summary>
        public float YawRate;
    }

    public class Avatar
    {
        // ------------------------------------------------------------------ joints
        // Named for the joint, not the mesh: rotating _shoulderL swings the whole arm because the
        // elbow, forearm and hand are parented beneath it.
        private Transform _root;      // the whole figure; lies down when incapacitated
        private Transform _hips;      // bob and crouch live here
        private Transform _torso;     // lean, roll, counter-yaw
        private Transform _head;
        private Transform _shoulderL, _shoulderR, _elbowL, _elbowR;
        private Transform _hipL, _hipR, _kneeL, _kneeR;
        private Transform _torchAnchor;

        private readonly System.Collections.Generic.List<Mesh> _meshes = new System.Collections.Generic.List<Mesh>();
        private readonly System.Collections.Generic.List<Renderer> _renderers = new System.Collections.Generic.List<Renderer>();

        private bool _isYeti;
        private float _hipHeight;     // standing hip height, the base the bob and crouch work from
        private float _gaitPhase;
        private float _roarT;         // seconds left on the roar pose
        private float _lean, _leanTarget;
        private float _breathe;       // idle chest rise, so a standing figure is never truly static

        /// <summary>Where breath vapour is emitted and (on the Yeti) where eyeshine lives.</summary>
        public Transform HeadAnchor => _head;
        /// <summary>Where a remote searcher's torch rides — the hand, so the beam swings with the arm.</summary>
        public Transform TorchAnchor => _torchAnchor;

        // Gait tuning. Stride is in METRES per full cycle: phase advances with distance covered, never
        // with time, which is the whole reason the feet do not skate when the per-night speed
        // multipliers or the deep-snow slow change how fast the body is actually moving.
        private const float YetiStride = 3.4f;
        private const float SearcherStride = 2.2f;

        // ------------------------------------------------------------------ construction

        /// <summary>
        /// The hunched bruiser: shoulders far wider than hips, head sunk between them and pushed
        /// FORWARD of the spine, arms long enough to hang below the knee, short heavy legs.
        ///
        /// Two proportions carry the whole read and neither is negotiable. The shoulder yoke is wider
        /// than the hips by roughly 1.7x — that ratio is what the eye uses to separate "ape" from
        /// "person" and it survives being reduced to a black shape in fog. And the head sits AHEAD of
        /// the shoulder line, not on top of it: a head centred over the spine reads as upright and
        /// human no matter how big the body around it is.
        ///
        /// Head height is anchored to the sim's Yeti eye height (2.4 m) so the third-person figure and
        /// its own first-person camera agree about where its eyes are.
        /// </summary>
        public static Avatar BuildYeti(Transform parent, Material fur, Material eye, int variant)
        {
            var a = new Avatar { _isYeti = true, _hipHeight = 1.18f };
            int seg = HPQuality.HighDetail ? 11 : 8;
            int rings = HPQuality.HighDetail ? 8 : 6;

            a._root = NewJoint(parent, "Body", Vector3.zero);
            a._hips = NewJoint(a._root, "Hips", new Vector3(0f, a._hipHeight, 0f));
            a._torso = NewJoint(a._hips, "Torso", Vector3.zero);

            // Deep chest, narrow gut: the mass is carried high, which is what makes the arms read as
            // load-bearing rather than as decoration hanging off a barrel.
            a.Part(a._torso, "Chest", MeshUtil.Blob(0.50f, 0.56f, 0.34f, rings, seg, variant + 1, 0.10f),
                   fur, new Vector3(0f, 0.52f, 0.02f));
            a.Part(a._torso, "Yoke", MeshUtil.Blob(0.66f, 0.25f, 0.36f, rings, seg, variant + 2, 0.14f),
                   fur, new Vector3(0f, 0.92f, -0.02f));

            // Head: forward of the spine and low, with a heavy brow and a jaw. The brow is the single
            // most valuable 40 triangles on the model — it is what puts the eyes in shadow.
            a._head = NewJoint(a._torso, "Head", new Vector3(0f, 1.05f, 0.13f));
            a.Part(a._head, "Skull", MeshUtil.Blob(0.21f, 0.23f, 0.25f, rings, seg, variant + 3, 0.09f),
                   fur, new Vector3(0f, 0.16f, 0.04f));
            a.Part(a._head, "Brow", MeshUtil.Blob(0.20f, 0.06f, 0.11f, 5, seg, variant + 4, 0.10f),
                   fur, new Vector3(0f, 0.19f, 0.19f));
            a.Part(a._head, "Jaw", MeshUtil.Blob(0.15f, 0.10f, 0.16f, 5, seg, variant + 5, 0.10f),
                   fur, new Vector3(0f, 0.01f, 0.13f));
            foreach (float sx in new[] { -0.085f, 0.085f })
                a.Part(a._head, "Eye", MeshUtil.Blob(0.045f, 0.04f, 0.04f, 4, 6, variant, 0f),
                       eye, new Vector3(sx, 0.14f, 0.21f));

            // Arms. Upper 0.74 + fore 0.66 puts the knuckles at ~0.55 m with the arm hanging, and the
            // knee is at 0.63 — so the hands clear the knee, which is the proportion the silhouette
            // is actually selling.
            a._shoulderL = NewJoint(a._torso, "ShoulderL", new Vector3(-0.60f, 0.90f, 0f));
            a._shoulderR = NewJoint(a._torso, "ShoulderR", new Vector3(0.60f, 0.90f, 0f));
            a._elbowL = a.Arm(a._shoulderL, fur, seg, rings, variant + 10, 0.74f, 0.66f, 0.16f, 0.135f, 0.13f);
            a._elbowR = a.Arm(a._shoulderR, fur, seg, rings, variant + 20, 0.74f, 0.66f, 0.16f, 0.135f, 0.13f);

            // Legs: short, thick, and set close together under the mass.
            a._hipL = NewJoint(a._hips, "HipL", new Vector3(-0.25f, 0f, 0f));
            a._hipR = NewJoint(a._hips, "HipR", new Vector3(0.25f, 0f, 0f));
            a._kneeL = a.Leg(a._hipL, fur, seg, rings, variant + 30, 0.55f, 0.50f, 0.21f, 0.17f, 0.145f);
            a._kneeR = a.Leg(a._hipR, fur, seg, rings, variant + 40, 0.55f, 0.50f, 0.21f, 0.17f, 0.145f);

            a._torchAnchor = a._head;
            return a;
        }

        /// <summary>
        /// A searcher: 1.8 m, human proportions, a pack on the back and a hood. Head height is anchored
        /// to the sim's 1.7 m eye height for the same reason the Yeti's is.
        ///
        /// The pack matters more than it looks. Searchers are the only silhouette a player sees at
        /// close range with a torch on it, and the pack plus hood is what stops five of them reading as
        /// five identical mannequins — the specialty colour does the rest.
        /// </summary>
        public static Avatar BuildSearcher(Transform parent, Material cloth, Material gear, int variant)
        {
            var a = new Avatar { _isYeti = false, _hipHeight = 0.95f };
            int seg = HPQuality.HighDetail ? 9 : 7;
            int rings = HPQuality.HighDetail ? 7 : 5;

            a._root = NewJoint(parent, "Body", Vector3.zero);
            a._hips = NewJoint(a._root, "Hips", new Vector3(0f, a._hipHeight, 0f));
            a._torso = NewJoint(a._hips, "Torso", Vector3.zero);

            a.Part(a._torso, "Chest", MeshUtil.Blob(0.23f, 0.34f, 0.17f, rings, seg, variant + 1, 0.07f),
                   cloth, new Vector3(0f, 0.32f, 0f));
            a.Part(a._torso, "Yoke", MeshUtil.Blob(0.30f, 0.11f, 0.18f, 5, seg, variant + 2, 0.07f),
                   cloth, new Vector3(0f, 0.53f, 0f));
            // Pack, in gear colour rather than the specialty colour: it should read as equipment.
            a.Part(a._torso, "Pack", MeshUtil.Blob(0.20f, 0.24f, 0.13f, 5, seg, variant + 3, 0.08f),
                   gear, new Vector3(0f, 0.36f, -0.20f));

            a._head = NewJoint(a._torso, "Head", new Vector3(0f, 0.62f, 0.02f));
            a.Part(a._head, "Skull", MeshUtil.Blob(0.115f, 0.14f, 0.13f, rings, seg, variant + 4, 0.05f),
                   cloth, new Vector3(0f, 0.15f, 0f));
            // Hood: a slightly larger shell around the back of the skull, open at the face.
            a.Part(a._head, "Hood", MeshUtil.Blob(0.145f, 0.15f, 0.145f, 5, seg, variant + 5, 0.09f),
                   gear, new Vector3(0f, 0.16f, -0.04f));

            a._shoulderL = NewJoint(a._torso, "ShoulderL", new Vector3(-0.245f, 0.53f, 0f));
            a._shoulderR = NewJoint(a._torso, "ShoulderR", new Vector3(0.245f, 0.53f, 0f));
            a._elbowL = a.Arm(a._shoulderL, cloth, seg, rings, variant + 10, 0.31f, 0.29f, 0.07f, 0.06f, 0.055f);
            a._elbowR = a.Arm(a._shoulderR, cloth, seg, rings, variant + 20, 0.31f, 0.29f, 0.07f, 0.06f, 0.055f);

            a._hipL = NewJoint(a._hips, "HipL", new Vector3(-0.11f, 0f, 0f));
            a._hipR = NewJoint(a._hips, "HipR", new Vector3(0.11f, 0f, 0f));
            a._kneeL = a.Leg(a._hipL, cloth, seg, rings, variant + 30, 0.47f, 0.44f, 0.095f, 0.075f, 0.065f);
            a._kneeR = a.Leg(a._hipR, cloth, seg, rings, variant + 40, 0.47f, 0.44f, 0.095f, 0.075f, 0.065f);

            // The torch rides the RIGHT hand, so a remote searcher's beam swings with their arm and
            // sweeps as they walk. That motion is most of how you read someone else's torch at range.
            a._torchAnchor = a._elbowR.GetChild(a._elbowR.childCount - 1);
            return a;
        }

        /// <summary>Shoulder -> upper arm -> elbow -> forearm -> hand. Returns the elbow joint.</summary>
        private Transform Arm(Transform shoulder, Material mat, int seg, int rings, int variant,
                              float upperLen, float foreLen, float rTop, float rMid, float rWrist)
        {
            PartDown(shoulder, "Upper", MeshUtil.Limb(rMid, rTop, upperLen, rings, seg, variant), mat);
            var elbow = NewJoint(shoulder, "Elbow", new Vector3(0f, -upperLen, 0f));
            PartDown(elbow, "Fore", MeshUtil.Limb(rWrist, rMid, foreLen, rings, seg, variant + 1), mat);
            // Hand last, so TorchAnchor's "last child" lookup finds it.
            Part(elbow, "Hand", MeshUtil.Blob(rWrist * 1.5f, rWrist * 1.2f, rWrist * 1.9f, 5, seg, variant + 2, 0.14f),
                 mat, new Vector3(0f, -foreLen - rWrist * 0.6f, 0.01f));
            return elbow;
        }

        /// <summary>Hip -> thigh -> knee -> shin -> foot. Returns the knee joint.</summary>
        private Transform Leg(Transform hip, Material mat, int seg, int rings, int variant,
                              float thighLen, float shinLen, float rTop, float rMid, float rAnkle)
        {
            PartDown(hip, "Thigh", MeshUtil.Limb(rMid, rTop, thighLen, rings, seg, variant), mat);
            var knee = NewJoint(hip, "Knee", new Vector3(0f, -thighLen, 0f));
            PartDown(knee, "Shin", MeshUtil.Limb(rAnkle, rMid, shinLen, rings, seg, variant + 1), mat);
            Part(knee, "Foot", MeshUtil.Blob(rAnkle * 1.15f, rAnkle * 0.6f, rAnkle * 2.0f, 5, seg, variant + 2, 0.10f),
                 mat, new Vector3(0f, -shinLen - rAnkle * 0.35f, rAnkle * 0.7f));
            return knee;
        }

        // ------------------------------------------------------------------ rig plumbing

        private static Transform NewJoint(Transform parent, string name, Vector3 localPos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            return go.transform;
        }

        private void Part(Transform parent, string name, Mesh mesh, Material mat, Vector3 localPos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            // Characters must not cast into the shadow cascades at distance — but up close a figure
            // with no contact shadow is exactly the "hovering" tell SSAO was added to fix.
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            _meshes.Add(mesh);
            _renderers.Add(r);
        }

        /// <summary>A limb mesh hung DOWN from its joint: MeshUtil.Limb builds along +Y, limbs hang -Y.</summary>
        private void PartDown(Transform parent, string name, Mesh mesh, Material mat)
        {
            Part(parent, name, mesh, mat, Vector3.zero);
            parent.GetChild(parent.childCount - 1).localRotation = Quaternion.Euler(180f, 0f, 0f);
        }

        public void SetVisible(bool on)
        {
            for (int i = 0; i < _renderers.Count; i++)
                if (_renderers[i] != null) _renderers[i].enabled = on;
        }

        /// <summary>
        /// Destroy the generated meshes. `new Mesh()` allocates a native object Unity's GC does not
        /// collect — the same leak the realism pass found in the world's materials, and worse here
        /// because a body is ~19 meshes and role changes rebuild it.
        /// </summary>
        public void Dispose()
        {
            for (int i = 0; i < _meshes.Count; i++)
                if (_meshes[i] != null) Object.Destroy(_meshes[i]);
            _meshes.Clear();
            _renderers.Clear();
            if (_root != null) Object.Destroy(_root.gameObject);
        }

        /// <summary>Kick the roar pose. Called from the roar RPC, which every client already receives.</summary>
        public void TriggerRoar() { _roarT = 1.5f; }

        // ------------------------------------------------------------------ animation

        public void Tick(in AvatarInput inp, float dt)
        {
            if (_root == null) return;
            dt = Mathf.Min(dt, 0.1f); // a hitch must not fling a limb through its whole range

            bool incap = inp.Status == HPPlayer.StatusIncap || inp.BeingCarried;
            bool frozen = inp.Status == HPPlayer.StatusFrozen;

            // --- gait phase: advances with GROUND COVERED, not with time ---
            float stride = _isYeti ? YetiStride : SearcherStride;
            if (!frozen && !incap && inp.Speed > 0.05f)
                _gaitPhase += inp.Speed / stride * Mathf.PI * 2f * dt;
            _breathe += dt * (inp.Sprinting ? 2.6f : 1.1f);

            // How much of the walk cycle to apply. Below a slow walk the legs should settle rather than
            // shuffle in place, which is what a raw speed->amplitude map produces at a stop.
            float top = _isYeti ? 7.5f : 5.5f;
            float walkW = frozen || incap ? 0f : Mathf.Clamp01(inp.Speed / (top * 0.55f));
            float s = Mathf.Sin(_gaitPhase), c = Mathf.Cos(_gaitPhase);

            if (_roarT > 0f) _roarT -= dt;
            // Fast attack, slow release: the chest is up almost immediately and comes down over a
            // second, which is the shape of the sound it goes with.
            float roarW = _roarT <= 0f ? 0f : Mathf.Clamp01(Mathf.Min((1.5f - _roarT) * 6f, _roarT * 1.4f));

            // --- hips: bob, crouch, and the drop into a lying pose ---
            float crouchDrop = inp.Crouched ? (_isYeti ? 0.30f : 0.34f) : 0f;
            float bob = -Mathf.Abs(s) * (_isYeti ? 0.075f : 0.045f) * walkW;
            float breathY = Mathf.Sin(_breathe) * 0.012f * (1f - walkW);
            _hips.localPosition = Vector3.Lerp(_hips.localPosition,
                new Vector3(0f, _hipHeight - crouchDrop + bob + breathY, 0f), 1f - Mathf.Exp(-14f * dt));

            // Incapacitated: the whole figure goes down. A body lying in the snow at the angle it fell
            // is a far stronger "someone is down over there" signal than a capsule tipped on its side,
            // which is what this replaces.
            float rootPitch = incap ? 80f : 0f;
            _root.localRotation = Damp(_root.localRotation, Quaternion.Euler(rootPitch, 0f, 0f), 6f, dt);
            if (incap)
            {
                // Slack everything and stop here — a downed body does not walk, roar or film.
                Slack(dt);
                return;
            }

            // --- torso: lean into the turn, lean into the run, roll and counter-yaw with the stride ---
            _leanTarget = Mathf.Clamp(-inp.YawRate * 9f, -18f, 18f);
            _lean = Mathf.Lerp(_lean, _leanTarget, 1f - Mathf.Exp(-6f * dt));
            float fwdLean = (_isYeti ? 16f : 6f) + inp.Speed * (_isYeti ? 1.5f : 1.1f)
                          + (inp.Crouched ? 14f : 0f) + (inp.Carrying ? 8f : 0f);
            float torsoPitch = Mathf.Lerp(fwdLean, -22f, roarW);          // roar throws the chest up
            float torsoRoll = _lean + s * 4f * walkW;
            float torsoYaw = -s * (_isYeti ? 7f : 5f) * walkW;            // shoulders counter the hips
            _torso.localRotation = Damp(_torso.localRotation,
                Quaternion.Euler(torsoPitch, torsoYaw, torsoRoll), 10f, dt);

            // --- head: leads the turn, and comes up on a roar ---
            // There is no replicated head pitch (the schema carries yaw only), so the head cannot track
            // a look direction. Leading the turn is the honest substitute: it is driven by yaw rate,
            // which IS replicated, and it reads as the thing looking where it is going.
            float headYaw = Mathf.Clamp(inp.YawRate * 14f, -28f, 28f);
            float headPitch = Mathf.Lerp(_isYeti ? -6f : -2f, -34f, roarW) - torsoPitch * 0.45f;
            _head.localRotation = Damp(_head.localRotation, Quaternion.Euler(headPitch, headYaw, 0f), 8f, dt);

            // --- limbs ---
            // Sign convention, derived once so the rest reads cleanly: a limb hangs along -Y, so a
            // POSITIVE X rotation swings it BACKWARD and a positive Z rotation swings it to the right.
            // The torso is the opposite (it points +Y), which is why torsoPitch above is positive to
            // lean forward.
            float legSwing = s * (_isYeti ? 34f : 40f) * walkW;
            float armSwing = s * (_isYeti ? 26f : 30f) * walkW;

            // Knees bend one way only. Driving them off a rectified cosine offset from the leg swing
            // puts the bend on the recovery half of the stride, where it belongs — a knee that bends
            // while the leg is planted is the classic tell of a cycle built from raw sine waves.
            float kneeL = Mathf.Max(0f, -c) * (_isYeti ? 62f : 72f) * walkW + (inp.Crouched ? 34f : 4f);
            float kneeR = Mathf.Max(0f, c) * (_isYeti ? 62f : 72f) * walkW + (inp.Crouched ? 34f : 4f);

            _hipL.localRotation = Damp(_hipL.localRotation, Quaternion.Euler(-legSwing, 0f, 0f), 16f, dt);
            _hipR.localRotation = Damp(_hipR.localRotation, Quaternion.Euler(legSwing, 0f, 0f), 16f, dt);
            _kneeL.localRotation = Damp(_kneeL.localRotation, Quaternion.Euler(kneeL, 0f, 0f), 16f, dt);
            _kneeR.localRotation = Damp(_kneeR.localRotation, Quaternion.Euler(kneeR, 0f, 0f), 16f, dt);

            // Arms counter-swing against the legs (left arm forward with the right leg).
            float armXL = armSwing, armXR = -armSwing;
            float armZL = _isYeti ? -13f : -5f, armZR = -armZL;  // resting flare off the ribs
            float elbowL = _isYeti ? 26f : 16f, elbowR = elbowL;

            if (roarW > 0.01f)
            {
                // Arms thrown out and up, elbows opening.
                armXL = Mathf.Lerp(armXL, -52f, roarW); armXR = Mathf.Lerp(armXR, -52f, roarW);
                armZL = Mathf.Lerp(armZL, -62f, roarW); armZR = Mathf.Lerp(armZR, 62f, roarW);
                elbowL = Mathf.Lerp(elbowL, 42f, roarW); elbowR = elbowL;
            }
            else if (inp.Carrying)
            {
                // Both arms forward and low, holding the haul out in front.
                armXL = Mathf.Lerp(armXL, -74f, 0.85f); armXR = Mathf.Lerp(armXR, -74f, 0.85f);
                armZL = -6f; armZR = 6f;
                elbowL = elbowR = 30f;
            }
            else if (inp.Filming)
            {
                // Camera up to the face: upper arms forward, elbows folded hard, hands at eye line.
                armXL = -62f; armXR = -62f;
                armZL = -16f; armZR = 16f;
                elbowL = elbowR = 96f;
            }
            else if (frozen)
            {
                // Caught mid-stride and locked. Held, not posed — the horror is that it is still you.
                armZL = -20f; armZR = 20f;
                elbowL = elbowR = 34f;
            }

            _shoulderL.localRotation = Damp(_shoulderL.localRotation, Quaternion.Euler(armXL, 0f, armZL), 12f, dt);
            _shoulderR.localRotation = Damp(_shoulderR.localRotation, Quaternion.Euler(armXR, 0f, armZR), 12f, dt);
            _elbowL.localRotation = Damp(_elbowL.localRotation, Quaternion.Euler(elbowL, 0f, 0f), 12f, dt);
            _elbowR.localRotation = Damp(_elbowR.localRotation, Quaternion.Euler(elbowR, 0f, 0f), 12f, dt);
        }

        /// <summary>Limbs hang loose — a downed or carried body, which should look dropped, not posed.</summary>
        private void Slack(float dt)
        {
            _torso.localRotation = Damp(_torso.localRotation, Quaternion.Euler(-12f, 0f, 6f), 5f, dt);
            _head.localRotation = Damp(_head.localRotation, Quaternion.Euler(16f, 10f, 0f), 5f, dt);
            _shoulderL.localRotation = Damp(_shoulderL.localRotation, Quaternion.Euler(12f, 0f, -24f), 5f, dt);
            _shoulderR.localRotation = Damp(_shoulderR.localRotation, Quaternion.Euler(16f, 0f, 20f), 5f, dt);
            _elbowL.localRotation = Damp(_elbowL.localRotation, Quaternion.Euler(28f, 0f, 0f), 5f, dt);
            _elbowR.localRotation = Damp(_elbowR.localRotation, Quaternion.Euler(20f, 0f, 0f), 5f, dt);
            _hipL.localRotation = Damp(_hipL.localRotation, Quaternion.Euler(-16f, 0f, 0f), 5f, dt);
            _hipR.localRotation = Damp(_hipR.localRotation, Quaternion.Euler(-8f, 0f, 0f), 5f, dt);
            _kneeL.localRotation = Damp(_kneeL.localRotation, Quaternion.Euler(38f, 0f, 0f), 5f, dt);
            _kneeR.localRotation = Damp(_kneeR.localRotation, Quaternion.Euler(24f, 0f, 0f), 5f, dt);
        }

        /// <summary>
        /// Framerate-independent approach. Lerping by `rate * dt` is the usual shortcut and it makes
        /// the whole rig stiffer at low framerates — exactly where a smooth pose matters most, and
        /// exactly the machine this is meant to run on.
        /// </summary>
        private static Quaternion Damp(Quaternion from, Quaternion to, float rate, float dt)
        {
            return Quaternion.Slerp(from, to, 1f - Mathf.Exp(-rate * dt));
        }
    }
}
