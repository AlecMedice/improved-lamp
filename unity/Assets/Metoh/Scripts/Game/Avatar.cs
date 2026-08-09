// Jointed procedural bodies for the Yeti and the searchers, and the animation that drives them.
//
// WHAT THIS REPLACES. Every player in the game was ONE Unity primitive capsule — the Yeti a capsule
// scaled to 1.3/1.35/1.3 with two spheres stuck on for eyes, a searcher the same capsule at 0.8/0.9.
// No head, no arms, no legs, and no animation of any kind anywhere in the project: no Animator, no
// SkinnedMeshRenderer, not even a procedural limb. Bodies slid across the snow in a fixed pose.
//
// The silhouette argument from UNITY_NOTES [legibility] applies here harder than it did to the trees. At
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
//
// THIS IS NOW ONE OF TWO IMPLEMENTATIONS. Gameplay holds an ICharacterBody (CharacterBody.cs) and
// builds through CharacterFactory, so an imported rigged model can take a role over without a line
// changing here or in HPPlayer. This stays the default, and stays the thing that has to keep working
// with no asset files in the repo.
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

    public class Avatar : ICharacterBody
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
        private Material _bodyMat;    // the tinted one: fur on the Yeti, parka on a searcher
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
            var a = new Avatar { _isYeti = true, _hipHeight = 1.18f, _bodyMat = fur };
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
        /// A searcher: 1.8 m, a person in expedition kit. Head height is anchored to the sim's 1.7 m
        /// eye height for the same reason the Yeti's is.
        ///
        /// WHAT THIS REPLACES AND WHY. The first pass built a searcher from five lumpy ellipsoids —
        /// chest, yoke, pack, skull, hood — with tapered cylinders for limbs and a blob stuck on each
        /// end for a hand or a foot. Owner's report: "just blobs with blob arms". That is the right
        /// read, and the diagnosis is the same one [legibility] reached about the trees, arriving one
        /// level down: an ellipsoid is a shape the eye names instantly, and a figure assembled from
        /// six of them reads as an assembly of primitives no matter what material is on it.
        ///
        /// Three things fix it, and none of them is triangle count:
        ///
        /// - **One torso, with a waist.** The trunk is a single lathe running hem to collar through a
        ///   real profile — flared hem, narrow waist, wide chest, shoulders closing to the neck. Two
        ///   stacked ellipsoids can make a shape that is wide at the top, but they cannot make a
        ///   waist, and the waist is what says "ribcage above, pelvis below" instead of "sack".
        /// - **The places clothing STOPS.** Belt, collar, wrist cuffs, boot cuffs, hood ruff — every
        ///   one of them a hard horizontal break across a taper (MeshUtil.Torus exists for this). A
        ///   figure in fog is an outline, and an outline with breaks in it reads as a dressed person
        ///   while a smooth one reads as a mannequin.
        /// - **A neck.** The old head sat straight on the yoke, which is most of why it read as a
        ///   snowman. It is 11 cm of geometry and it does more than the hood does.
        ///
        /// Lumpiness comes DOWN, hard — 0.07 to ~0.025. It is there to stop a mesh reading as a
        /// revolved primitive, which is a real risk on a boulder and the wrong medicine on a person:
        /// fabric over a body is smooth, and lumps at this scale read as damage rather than as
        /// anatomy. The job lumpiness was doing badly — stopping five searchers from being five
        /// identical mannequins — is done properly below by <c>broad</c>, and by the specialty colour.
        /// </summary>
        public static Avatar BuildSearcher(Transform parent, Material cloth, Material gear, int variant)
        {
            var a = new Avatar { _isYeti = false, _hipHeight = 0.95f, _bodyMat = cloth };
            int seg = HPQuality.HighDetail ? 10 : 7;
            int rings = HPQuality.HighDetail ? 7 : 5;
            bool fine = HPQuality.HighDetail;

            // Per-player BUILD, hashed from the variant. Five searchers have to be five people, and
            // the honest way to get that is proportion, not surface noise — one is broader through the
            // chest and heavier in the limbs than another.
            //
            // It deliberately does NOT touch any height. Every vertical number below is anchored to
            // the sim's 1.7 m eye height, and a figure whose eyes are 5 cm from where its own camera
            // says they are is a figure that ducks behind cover the sim thinks it can see over.
            float broad = 0.94f + MeshUtil.Hash01(variant * 13 + 5) * 0.13f;

            a._root = NewJoint(parent, "Body", Vector3.zero);
            a._hips = NewJoint(a._root, "Hips", new Vector3(0f, a._hipHeight, 0f));
            a._torso = NewJoint(a._hips, "Torso", Vector3.zero);

            // The trunk, hem to collar. zScale 0.62 because a torso is far flatter front-to-back than
            // it is wide — a circular one is a barrel, which was half of the old silhouette problem.
            var trunk = new[]
            {
                new Vector2(-0.180f, 0.170f * broad),   // parka hem, flared and standing off the hips
                new Vector2(-0.140f, 0.183f * broad),
                new Vector2(-0.055f, 0.176f * broad),   // hips
                new Vector2( 0.050f, 0.158f * broad),   // waist — the narrowest point, and the whole trick
                new Vector2( 0.170f, 0.180f * broad),   // lower ribs
                new Vector2( 0.300f, 0.202f * broad),   // chest
                new Vector2( 0.410f, 0.203f * broad),
                new Vector2( 0.490f, 0.186f * broad),
                new Vector2( 0.545f, 0.115f * broad),   // shoulder shelf closing in toward the neck
                new Vector2( 0.575f, 0.070f),
            };
            a.Part(a._torso, "Torso", MeshUtil.Lathe(trunk, seg, variant + 1, 0.025f, 1f, 0.62f),
                   cloth, Vector3.zero);
            // Belt at the waist. Sits proud of the narrowest ring, so it cuts the trunk in two from
            // every angle — the single cheapest "wearing clothes" cue on the whole figure.
            a.Part(a._torso, "Belt", MeshUtil.Torus(0.163f * broad, 0.023f, seg, 6, variant + 2, 0f, 1f, 0.62f),
                   gear, new Vector3(0f, 0.050f, 0f));

            // Pack, in gear colour rather than the specialty colour: it should read as equipment, and
            // the colour is how you tell teammates apart at range — spending it here blurs that.
            a.Part(a._torso, "Pack", MeshUtil.Blob(0.150f, 0.205f, 0.100f, 5, seg, variant + 3, 0.045f),
                   gear, new Vector3(0f, 0.300f, -0.180f));
            if (fine)
            {
                a.Part(a._torso, "PackLid", MeshUtil.Blob(0.130f, 0.052f, 0.088f, 5, seg, variant + 4, 0.05f),
                       gear, new Vector3(0f, 0.455f, -0.172f));
                // Shoulder straps down the front of the chest. Small, but they are the thing that ties
                // the pack to the body instead of leaving it stuck on the back like a shell.
                foreach (float sx in new[] { -0.088f, 0.088f })
                    a.PartAt(a._torso, "Strap", MeshUtil.Limb(0.021f, 0.026f, 0.30f, 5, 6, variant + 5),
                             gear, new Vector3(sx, 0.500f, 0.098f), new Vector3(180f, 0f, 0f));
            }

            // Neck. Eleven centimetres, and the largest single improvement in the whole rebuild — a
            // head sitting straight on the shoulders is a snowman, and no amount of hood fixes it.
            a.Part(a._torso, "Neck", MeshUtil.Limb(0.062f, 0.057f, 0.115f, 5, seg, variant + 6, 0.02f),
                   cloth, new Vector3(0f, 0.545f, -0.004f));
            a.Part(a._torso, "Collar", MeshUtil.Torus(0.080f, 0.029f, seg, 6, variant + 7, 0.05f, 1f, 0.88f),
                   gear, new Vector3(0f, 0.565f, -0.004f));

            // Head. The joint is the base of the skull; the cranium centre lands at 1.695 m, putting
            // the eye line on the sim's 1.7 m and the crown at 1.79 m.
            a._head = NewJoint(a._torso, "Head", new Vector3(0f, 0.655f, 0.005f));
            a.Part(a._head, "Skull", MeshUtil.Blob(0.081f, 0.098f, 0.085f, rings, seg, variant + 8, 0.025f),
                   cloth, new Vector3(0f, 0.092f, -0.020f));
            // The face is deliberately the DARK gear material, not a skin tone. Sitting inside the ruff
            // it reads as shadow, which is both what a hooded face actually looks like at night and a
            // better thing to aim at than a procedural face — the one piece of anatomy where "almost
            // right" is worse than "not attempted".
            a.Part(a._head, "Face", MeshUtil.Blob(0.062f, 0.055f, 0.058f, 5, seg, variant + 9, 0.02f),
                   gear, new Vector3(0f, 0.045f, 0.032f));
            // Hood shell. It has to be a CLOSED ellipsoid (a lathe caps any end with a real radius, so
            // there is no way to leave a hole in one), which means the only way to get a face opening
            // is to push the shell far enough BACK that the face protrudes past its front pole. Hood
            // front lands at z 0.060, face front at 0.090 — 3 cm of face out in front of the fabric.
            // Get this wrong in the other direction and the head is a featureless ball, which is
            // exactly what the first version of this was.
            a.Part(a._head, "Hood", MeshUtil.Blob(0.122f, 0.125f, 0.115f, 5, seg, variant + 11, 0.045f),
                   gear, new Vector3(0f, 0.105f, -0.055f));
            // The fur ruff around the opening — the most recognisable shape in expedition kit, and the
            // thing that makes a hood read as a hood rather than as a helmet. Rotated so the ring's
            // hole faces forward: Euler(90,0,0) takes local +Y (the torus axis) to +Z. Its inner radius
            // (major 0.100 - minor 0.030 = 0.070) is set to just clear the face's 0.062 half-width, and
            // zScale squashes it vertically because after that rotation local Z is world UP.
            a.PartAt(a._head, "Ruff", MeshUtil.Torus(0.100f, 0.030f, seg, 6, variant + 12, 0.09f, 1f, 0.85f),
                     gear, new Vector3(0f, 0.070f, 0.052f), new Vector3(90f, 0f, 0f));

            // Shoulder joints sit INSIDE the chest, where a real one is; the deltoid caps the outside
            // and sets the actual shoulder width (0.185 + 0.078 -> ~0.51 m across).
            a._shoulderL = NewJoint(a._torso, "ShoulderL", new Vector3(-0.185f * broad, 0.490f, 0f));
            a._shoulderR = NewJoint(a._torso, "ShoulderR", new Vector3(0.185f * broad, 0.490f, 0f));
            a._elbowL = a.SearcherArm(a._shoulderL, cloth, gear, seg, rings, variant + 20, broad, fine, out _);
            a._elbowR = a.SearcherArm(a._shoulderR, cloth, gear, seg, rings, variant + 40, broad, fine, out var handR);

            a._hipL = NewJoint(a._hips, "HipL", new Vector3(-0.105f, 0f, 0f));
            a._hipR = NewJoint(a._hips, "HipR", new Vector3(0.105f, 0f, 0f));
            a._kneeL = a.SearcherLeg(a._hipL, cloth, gear, seg, rings, variant + 60, broad, fine);
            a._kneeR = a.SearcherLeg(a._hipR, cloth, gear, seg, rings, variant + 80, broad, fine);

            // The torch rides the RIGHT hand, so a remote searcher's beam swings with their arm and
            // sweeps as they walk. That motion is most of how you read someone else's torch at range.
            // An explicit hand JOINT now, rather than the old "last child of the elbow" lookup — that
            // silently retargeted the torch onto whatever part happened to be added last.
            a._torchAnchor = handR;
            return a;
        }

        /// <summary>
        /// A searcher's arm: shoulder -> deltoid -> upper -> elbow -> forearm -> wrist cuff -> glove.
        /// Returns the elbow joint and hands back the wrist joint the torch mounts on.
        ///
        /// The deltoid is what stops the arm looking speared into the ribs, and the cuff-plus-glove is
        /// what stops it ending in a lump. Both are the same idea as the belt: a limb that tapers
        /// smoothly from shoulder to fingertip is a tube, and a tube is what the old arms were.
        /// </summary>
        private Transform SearcherArm(Transform shoulder, Material cloth, Material gear,
                                      int seg, int rings, int variant, float broad, bool fine,
                                      out Transform hand)
        {
            const float upperLen = 0.300f, foreLen = 0.270f;

            Part(shoulder, "Deltoid", MeshUtil.Blob(0.078f * broad, 0.082f, 0.078f * broad, 5, seg, variant, 0.03f),
                 cloth, new Vector3(0f, -0.015f, 0f));
            PartDown(shoulder, "Upper", MeshUtil.Limb(0.058f * broad, 0.072f * broad, upperLen, rings, seg, variant + 1, 0.02f), cloth);

            var elbow = NewJoint(shoulder, "Elbow", new Vector3(0f, -upperLen, 0f));
            PartDown(elbow, "Fore", MeshUtil.Limb(0.050f * broad, 0.058f * broad, foreLen, rings, seg, variant + 2, 0.02f), cloth);

            hand = NewJoint(elbow, "Hand", new Vector3(0f, -foreLen, 0f));
            if (fine)
                Part(hand, "Cuff", MeshUtil.Torus(0.052f * broad, 0.018f, seg, 6, variant + 3, 0.06f),
                     gear, new Vector3(0f, 0.010f, 0f));
            // A mitt, not a fist: wider than the wrist and longer than it is thick, so the hand is a
            // shape rather than the ball a Blob defaults to.
            Part(hand, "Glove", MeshUtil.Blob(0.056f, 0.055f, 0.070f, 5, seg, variant + 4, 0.05f),
                 gear, new Vector3(0f, -0.048f, 0.008f));
            return elbow;
        }

        /// <summary>
        /// A searcher's leg: hip -> thigh -> knee -> shin -> ankle -> boot cuff -> boot.
        ///
        /// The boot is in gear colour, not the parka colour. Real winter kit is never one colour head
        /// to toe, and the dark foot is what visually PLANTS the figure on the snow — a leg that
        /// fades into a pale boot on pale ground looks like it is hovering, which is the exact tell
        /// SSAO went in to fix on everything else.
        /// </summary>
        private Transform SearcherLeg(Transform hip, Material cloth, Material gear,
                                      int seg, int rings, int variant, float broad, bool fine)
        {
            const float thighLen = 0.470f, shinLen = 0.440f;

            PartDown(hip, "Thigh", MeshUtil.Limb(0.082f * broad, 0.105f * broad, thighLen, rings, seg, variant, 0.02f), cloth);
            var knee = NewJoint(hip, "Knee", new Vector3(0f, -thighLen, 0f));
            PartDown(knee, "Shin", MeshUtil.Limb(0.062f * broad, 0.082f * broad, shinLen, rings, seg, variant + 1, 0.02f), cloth);

            var ankle = NewJoint(knee, "Ankle", new Vector3(0f, -shinLen, 0f));
            if (fine)
                Part(ankle, "BootCuff", MeshUtil.Torus(0.068f * broad, 0.024f, seg, 6, variant + 2, 0.07f),
                     gear, new Vector3(0f, 0.012f, 0f));
            // Elongated forward: it is the length that makes it a foot, since at this size nothing
            // else about it will be visible.
            Part(ankle, "Boot", MeshUtil.Blob(0.062f, 0.046f, 0.115f, 5, seg, variant + 3, 0.03f),
                 gear, new Vector3(0f, -0.030f, 0.036f));
            return knee;
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

        /// <summary>A part placed at an angle — a hood ruff turned to face forward, a pack strap.</summary>
        private void PartAt(Transform parent, string name, Mesh mesh, Material mat,
                            Vector3 localPos, Vector3 euler)
        {
            Part(parent, name, mesh, mat, localPos);
            parent.GetChild(parent.childCount - 1).localRotation = Quaternion.Euler(euler);
        }

        /// <summary>A limb mesh hung DOWN from its joint: MeshUtil.Limb builds along +Y, limbs hang -Y.</summary>
        private void PartDown(Transform parent, string name, Mesh mesh, Material mat)
        {
            PartAt(parent, name, mesh, mat, Vector3.zero, new Vector3(180f, 0f, 0f));
        }

        /// <summary>
        /// Push the specialty colour onto the parka. Only the body material takes it — the gear
        /// material is shared webbing brown on purpose, so pack, hood, boots and gloves stay neutral
        /// and the colour keeps its one job of telling teammates apart at range.
        /// </summary>
        public void SetTint(Color bodyColor)
        {
            if (_bodyMat != null) _bodyMat.color = bodyColor;
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
