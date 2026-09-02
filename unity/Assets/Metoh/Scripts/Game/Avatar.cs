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
        // Ankles. Optional — only bodies whose foot is worth posing create them, and Tick guards on
        // null. A foot welded rigidly to the shin is one of the loudest "no animation" tells there is:
        // real weight transfer is heel-strike, roll, toe-off, and all of it happens here.
        private Transform _ankleL, _ankleR;
        private Transform _torchAnchor;

        private readonly System.Collections.Generic.List<Mesh> _meshes = new System.Collections.Generic.List<Mesh>();
        private readonly System.Collections.Generic.List<Renderer> _renderers = new System.Collections.Generic.List<Renderer>();

        // Specialty kit. Built LAZILY, because the specialty is dealt after the figure is — the same
        // reason SetTint has to be re-checked rather than read once in BuildVisuals. Its parts are
        // tracked apart from the body's so a re-deal can tear down only the kit.
        private readonly System.Collections.Generic.List<Mesh> _kitMeshes = new System.Collections.Generic.List<Mesh>();
        private readonly System.Collections.Generic.List<Renderer> _kitRenderers = new System.Collections.Generic.List<Renderer>();
        private readonly System.Collections.Generic.List<GameObject> _kitObjects = new System.Collections.Generic.List<GameObject>();
        private bool _buildingKit;
        private string _kitId = "\u0000";   // not "" — that is a real state (no specialty dealt yet)
        private bool _visible = true;       // so kit parts built while hidden do not pop into view
        // The head, held so its SKIN material can be re-pointed once the character is dealt. The head
        // is built before anyone knows who this is, and skin is not something SetTint can carry —
        // SetTint is the parka, i.e. the specialty COLOUR, which is a different signal doing a
        // different job (telling teammates apart at range, not saying who somebody is up close).
        private Renderer _headRend;

        private bool _isYeti;
        private int _variant;         // the build hash; kit parts jag off it so they vary per player
        private Material _bodyMat;    // the tinted one: fur on the Yeti, parka on a searcher
        private Material _gearMat;    // webbing brown; the kit hangs off the same one
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
        /// The abominable snowman — specifically a knuckle-walking mountain ape, not a tall person in
        /// a fur coat.
        ///
        /// **WHAT THIS REPLACES AND WHY.** The first jointed Yeti got its proportions broadly right and
        /// still drew the owner's report *"a crazy person running through a forest"*, then *"a muppet
        /// running around"*, then *"in no way shape or form the abominable snowman"*. Those are
        /// locomotion and identity reads before they are modelling reads, and there were four causes:
        ///
        /// - **It was dark brown** (0x2a2018) — a Sasquatch colour left over from Hollow Pines, never
        ///   re-themed. A yeti is the thing the snow is named after. The colour is HPPlayer's to set,
        ///   but it is the first reason this never read as the creature it is supposed to be.
        /// - **It walked upright on two legs with human counter-swinging arms.** That IS a person
        ///   running, and no amount of shoulder width argues with it. See the gait note in
        ///   <see cref="Tick"/>: past a jog the figure now drops onto its knuckles.
        /// - **It had a NECK.** A visible gap between skull and shoulder yoke is one of the strongest
        ///   "human" signals there is. The trapezius mass now fills it, so the head sits sunk INTO the
        ///   shoulders the way an ape's does, and the hump is the highest point on the body.
        /// - **Its hands and feet were single ellipsoids.** Once the animal is on its knuckles those
        ///   hands are its leading edge — at ground level, out in front, closest to whoever is being
        ///   charged — and they were spheres. They are now built parts ([legibility], one level down).
        ///
        /// The proportions that carry the read, none negotiable. The shoulder yoke is ~1.85x the hip
        /// width, which is the ratio the eye separates "ape" from "person" by and which survives being
        /// reduced to a black shape in fog. The head sits AHEAD of the spine, not on top of it. And the
        /// arms are LONGER THAN THE LEGS — an intermembral index over 100, which nothing habitually
        /// bipedal has — so the knuckles hang below the knee standing (0.56 m against a 0.63 m knee)
        /// and reach the ground the instant the torso pitches.
        ///
        /// Head height is anchored to the sim's Yeti eye height (2.4 m) so the third-person figure and
        /// its own first-person camera agree about where its eyes are.
        ///
        /// <paramref name="hide"/> is bare skin — muzzle, hands, feet. A white animal with white
        /// extremities is a blob; every real one has dark bare skin at the face and the palms, and here
        /// it doubles as the dark anchor that keeps the silhouette legible against snow.
        /// </summary>
        public static Avatar BuildYeti(Transform parent, Material fur, Material hide, Material eye, int variant)
        {
            var a = new Avatar
            {
                _isYeti = true, _hipHeight = 1.18f, _bodyMat = fur, _gearMat = hide, _variant = variant
            };
            int seg = HPQuality.HighDetail ? 11 : 8;
            int rings = HPQuality.HighDetail ? 8 : 6;
            bool fine = HPQuality.HighDetail;

            a._root = NewJoint(parent, "Body", Vector3.zero);
            a._hips = NewJoint(a._root, "Hips", new Vector3(0f, a._hipHeight, 0f));
            a._torso = NewJoint(a._hips, "Torso", Vector3.zero);

            // The trunk is ONE lathe, seat to collar, for the same reason the searcher's is: stacked
            // ellipsoids cannot produce a continuous profile, and a body without one reads as an
            // assembly of parts. The profile is deliberately the INVERSE of a person's — an ape carries
            // a heavy low gut and an enormous chest with no waist between them, so the widest point is
            // the belly and the second widest the ribcage. A waist here would undo the whole silhouette.
            var trunk = new[]
            {
                new Vector2(-0.12f, 0.29f),   // seat
                new Vector2( 0.06f, 0.38f),   // gut — the widest point, and carried low
                new Vector2( 0.28f, 0.395f),
                new Vector2( 0.50f, 0.445f),  // lower ribs
                new Vector2( 0.72f, 0.485f),  // chest, carried high over the arms
                new Vector2( 0.88f, 0.455f),
                new Vector2( 1.00f, 0.30f),   // closing into the shoulder mass
                new Vector2( 1.08f, 0.15f),
            };
            a.Part(a._torso, "Trunk", MeshUtil.Lathe(trunk, seg, variant + 1, 0.055f, 1f, 0.80f),
                   fur, new Vector3(0f, 0f, 0.01f));

            // Shoulder yoke: 0.74 half-width against the trunk's 0.38 at the hips. This is the 1.85x.
            a.Part(a._torso, "Yoke", MeshUtil.Blob(0.74f, 0.28f, 0.42f, rings, seg, variant + 2, 0.12f),
                   fur, new Vector3(0f, 0.92f, -0.02f));
            // The trapezius hump — the mass that fills the neck and becomes the highest point on the
            // animal. Set BACK of centre so the head reads as thrust forward out of it rather than as
            // sitting on top of it.
            a.Part(a._torso, "Hump", MeshUtil.Blob(0.40f, 0.25f, 0.36f, rings, seg, variant + 6, 0.17f),
                   fur, new Vector3(0f, 1.04f, -0.07f));

            // Head. Skull, sagittal crest and brow ridge weld into one mesh: they are one fur surface,
            // and the crest is what turns a round cranium into a gorilla's. The brow is still the most
            // valuable geometry on the model — it is what puts the eyes in shadow.
            a._head = NewJoint(a._torso, "Head", new Vector3(0f, 1.08f, 0.18f));
            a.Part(a._head, "Skull", YetiSkull(seg, rings, variant + 3, fine), fur, Vector3.zero);
            // Muzzle in bare hide, not fur — the one part of the face that is skin on every ape there
            // is, and the thing that stops the head reading as a furry ball.
            a.Part(a._head, "Muzzle", MeshUtil.Blob(0.150f, 0.115f, 0.185f, 5, seg, variant + 5, 0.06f),
                   hide, new Vector3(0f, 0.010f, 0.180f));
            foreach (float sx in new[] { -0.088f, 0.088f })
                a.Part(a._head, "Eye", MeshUtil.Blob(0.042f, 0.038f, 0.038f, 4, 6, variant, 0f),
                       eye, new Vector3(sx, 0.145f, 0.190f));

            // Shag. Two clusters, both breaking a silhouette that lathes otherwise leave perfectly
            // smooth — see ShagSkirt for why a smooth outline plus a fur normal map reads as upholstery.
            a.Part(a._torso, "ShoulderShag",
                   ShagSkirt(0.66f, 0.62f, fine ? 14 : 9, 0.34f, 0.075f, 34f, variant + 50, fine),
                   fur, new Vector3(0f, 0.98f, -0.02f));
            a.Part(a._torso, "HaunchShag",
                   ShagSkirt(0.37f, 0.80f, fine ? 12 : 8, 0.30f, 0.065f, 22f, variant + 60, fine),
                   fur, new Vector3(0f, 0.10f, 0f));

            // Arms, longer than the legs. Shoulder joints inside the yoke where a real one is.
            a._shoulderL = NewJoint(a._torso, "ShoulderL", new Vector3(-0.62f, 0.90f, 0f));
            a._shoulderR = NewJoint(a._torso, "ShoulderR", new Vector3(0.62f, 0.90f, 0f));
            a._elbowL = a.YetiArm(a._shoulderL, fur, hide, seg, rings, variant + 10, fine, -1f);
            a._elbowR = a.YetiArm(a._shoulderR, fur, hide, seg, rings, variant + 20, fine, 1f);

            // Legs: short, thick, set close under the mass, and ending in an ankle so the foot can
            // actually plant rather than staying a rigid slab welded to the shin.
            a._hipL = NewJoint(a._hips, "HipL", new Vector3(-0.26f, 0f, 0f));
            a._hipR = NewJoint(a._hips, "HipR", new Vector3(0.26f, 0f, 0f));
            a._kneeL = a.YetiLeg(a._hipL, fur, hide, seg, rings, variant + 30, fine, -1f, out a._ankleL);
            a._kneeR = a.YetiLeg(a._hipR, fur, hide, seg, rings, variant + 40, fine, 1f, out a._ankleR);

            a._torchAnchor = a._head;
            return a;
        }

        /// <summary>
        /// Cranium + sagittal crest + brow ridge, welded into one fur mesh.
        ///
        /// The crest is the difference between a round head and an ape's. A big male gorilla's skull is
        /// mostly a bony ridge for jaw muscle to anchor to, and in silhouette it turns the top of the
        /// head into a peak — which is the profile every yeti illustration since the 1950s has drawn.
        /// The cheek flanges do the same job across the width.
        /// </summary>
        private static Mesh YetiSkull(int seg, int rings, int variant, bool fine)
        {
            var g = new MeshUtil.MeshGroup();
            g.Add(MeshUtil.Blob(0.215f, 0.225f, 0.250f, rings, seg, variant, 0.075f), new Vector3(0f, 0.145f, 0.010f));
            // Sagittal crest, running fore-aft along the midline.
            g.Add(MeshUtil.Blob(0.055f, 0.105f, 0.215f, 5, seg, variant + 1, 0.09f), new Vector3(0f, 0.290f, -0.010f));
            // Brow ridge — one continuous shelf across both eyes, which is what casts them into shadow.
            g.Add(MeshUtil.Blob(0.205f, 0.062f, 0.105f, 5, seg, variant + 2, 0.09f), new Vector3(0f, 0.192f, 0.150f));
            if (fine)
            {
                // Cheek flanges. Adult male apes grow them and they widen the face past the cranium,
                // which is a shape a human head simply cannot make.
                foreach (float sx in new[] { -1f, 1f })
                    g.Add(MeshUtil.Blob(0.075f, 0.115f, 0.110f, 5, seg, variant + 3, 0.10f),
                          new Vector3(sx * 0.180f, 0.080f, 0.085f));
            }
            return g.Build();
        }

        /// <summary>
        /// A ring of fur tufts, welded into one mesh — the thing that breaks the outline.
        ///
        /// [legibility]'s argument applied to fur. At night, in fog, the silhouette is very nearly all
        /// the information reaching the player, and a body assembled from lathes has a perfectly smooth
        /// one. Smooth outline plus a fur normal map reads as UPHOLSTERY, which is a large part of the
        /// distance between "animal" and the owner's "muppet" — a muppet is precisely a smooth shape
        /// with a shaggy surface. Ragged edges are what fur actually does to an outline.
        ///
        /// Tufts taper to a point (<c>capScale: 0</c>), which is the one place besides an icicle where
        /// that profile is right: a lock of hair really does come to nothing.
        /// </summary>
        private static Mesh ShagSkirt(float radius, float zScale, int count, float len, float thick,
                                      float outward, int variant, bool fine)
        {
            var g = new MeshUtil.MeshGroup();
            for (int i = 0; i < count; i++)
            {
                float ang = (i + 0.5f) / count * Mathf.PI * 2f;
                float h = MeshUtil.Hash01(variant * 131 + i * 17);
                // Length and splay vary per tuft, hashed from the index — never an RNG stream
                // ([rng-lockstep]), so every client grows the same coat on the same Yeti.
                float l = len * (0.65f + h * 0.7f);
                float tilt = outward * (0.7f + MeshUtil.Hash01(variant * 71 + i * 29) * 0.6f);
                g.Add(MeshUtil.Limb(thick, thick * 0.22f, l, 4, fine ? 6 : 4, variant + i, 0.22f, capScale: 0f),
                      new Vector3(Mathf.Sin(ang) * radius, 0f, Mathf.Cos(ang) * radius * zScale),
                      // Euler is Ry*Rx*Rz: the X term tilts the tuft off vertical toward +Z, the Y term
                      // then spins that tilt round to the tuft's own angle, so every one splays outward.
                      new Vector3(180f - tilt, ang * Mathf.Rad2Deg, 0f));
            }
            return g.Build();
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
            var a = new Avatar { _isYeti = false, _hipHeight = 0.95f, _bodyMat = cloth, _gearMat = gear, _variant = variant };
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
            // The chest stays where it was (~41 cm across): the bulk this pass adds is in the LIMBS
            // and in the skirt, not in the ribcage. What changed is the hip band — a heavy parka
            // flares over the hips rather than tucking under them, and here that flare is load-bearing
            // geometry rather than styling. The thigh is now 20 cm across at its top and the hip joints
            // sit +/-9 cm off the centreline, which puts the outside of the leg at 0.190; the parka has
            // to be at least that wide through the same band or the thigh pokes out through the coat.
            // If you ever narrow the hips here, narrow HipL/HipR by the same amount or that reappears.
            var trunk = new[]
            {
                new Vector2(-0.255f, 0.214f * broad),   // parka hem, well down over the seat
                new Vector2(-0.205f, 0.222f * broad),   // widest point of the flare
                new Vector2(-0.110f, 0.215f * broad),
                new Vector2(-0.020f, 0.200f * broad),   // hips — bulky, and covering the thigh tops
                new Vector2( 0.055f, 0.168f * broad),   // waist — the narrowest point, and the whole trick
                new Vector2( 0.175f, 0.186f * broad),   // lower ribs
                new Vector2( 0.300f, 0.204f * broad),   // chest
                new Vector2( 0.410f, 0.205f * broad),
                new Vector2( 0.490f, 0.188f * broad),
                new Vector2( 0.545f, 0.116f * broad),   // shoulder shelf closing in toward the neck
                new Vector2( 0.575f, 0.070f),
            };
            a.Part(a._torso, "Torso", MeshUtil.Lathe(trunk, seg, variant + 1, 0.025f, 1f, 0.62f),
                   cloth, Vector3.zero);
            // Belt at the waist. Sits proud of the narrowest ring, so it cuts the trunk in two from
            // every angle — the single cheapest "wearing clothes" cue on the whole figure.
            a.Part(a._torso, "Belt", MeshUtil.Torus(0.173f * broad, 0.025f, seg, 6, variant + 2, 0f, 1f, 0.62f),
                   gear, new Vector3(0f, 0.055f, 0f));
            // The hem itself. Same argument as the belt one band lower: the skirt of a parka ends in a
            // thick rolled edge, and that edge is the widest thing on the figure below the shoulders —
            // so it is the break that says "coat over legs" instead of "one continuous body".
            a.Part(a._torso, "Hem", MeshUtil.Torus(0.216f * broad, 0.022f, seg, 6, variant + 13, 0.04f, 1f, 0.62f),
                   cloth, new Vector3(0f, -0.248f, 0f));

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
            //
            // **THE HOOD CAME OFF, AND THAT IS THE POINT.** The previous head was a closed hood shell
            // with a DARK GEAR-COLOURED BLOB for a face, on the reasoning that a hooded face at night
            // is shadow anyway and a procedural face is the one piece of anatomy where "almost right"
            // is worse than "not attempted". That reasoning is defensible in isolation and it produced
            // five identical faceless figures — the owner's *"they all look terrible, we need some
            // actual people"*. A shadow where a face goes is not neutral; it is the strongest possible
            // statement that nobody is in there.
            //
            // So: a real head in a real skin tone, with the features that survive being small (brow,
            // nose, jaw, ears), and the hood pushed DOWN onto the back where a hood actually lives when
            // you need to hear and see. Hair and headwear come with the character, through the kit —
            // see SetSpecialty. The five are named people in STORY.md and they should be recognisable
            // as those people.
            a._head = NewJoint(a._torso, "Head", new Vector3(0f, 0.655f, 0.005f));
            a._headRend = a.Part(a._head, "Head", SearcherHead(seg, rings, variant + 8, fine),
                                 a.SkinMaterial(), Vector3.zero);
            // Eyes. Two millimetre-scale beads that do a wholly disproportionate amount of work: an
            // eyeless face reads as a mannequin at any distance where you can see the face at all, and
            // this is the range a revive happens at.
            foreach (float sx in new[] { -0.030f, 0.030f })
                a.Part(a._head, "Eye", MeshUtil.Blob(0.0115f, 0.0105f, 0.0090f, 4, 6, variant, 0f),
                       EyeDark, new Vector3(sx, 0.0735f, 0.0615f));

            // The hood, down. A thick roll of fabric across the upper back and behind the neck — which
            // is both where a pushed-back hood sits and one more horizontal break in the outline, the
            // same job the belt and hem do lower down.
            a.Part(a._torso, "HoodDown", MeshUtil.Blob(0.140f, 0.085f, 0.090f, 5, seg, variant + 11, 0.06f),
                   gear, new Vector3(0f, 0.520f, -0.115f));
            // The fur ruff, now around the hood's mouth at the back of the neck rather than around a
            // face. Still the most recognisable shape in expedition kit. Euler(90,0,0) takes the torus
            // axis (local +Y) to +Z, so the ring's hole faces forward into the neck.
            if (fine)
                a.PartAt(a._torso, "Ruff", MeshUtil.Torus(0.098f, 0.028f, seg, 6, variant + 12, 0.10f, 1f, 0.80f),
                         gear, new Vector3(0f, 0.572f, -0.070f), new Vector3(72f, 0f, 0f));

            // Shoulder joints sit INSIDE the chest, where a real one is; the deltoid caps the outside
            // and sets the actual shoulder width (0.185 + 0.098 -> ~0.57 m across, a parka over a pack).
            a._shoulderL = NewJoint(a._torso, "ShoulderL", new Vector3(-0.185f * broad, 0.490f, 0f));
            a._shoulderR = NewJoint(a._torso, "ShoulderR", new Vector3(0.185f * broad, 0.490f, 0f));
            a._elbowL = a.SearcherArm(a._shoulderL, cloth, gear, seg, rings, variant + 20, broad, fine, -1f, out _);
            a._elbowR = a.SearcherArm(a._shoulderR, cloth, gear, seg, rings, variant + 40, broad, fine, 1f, out var handR);

            // +/-9 cm is roughly where femoral heads actually sit. It is also the number the parka's
            // hip band above was widened to cover — see the note on the trunk profile before moving it.
            a._hipL = NewJoint(a._hips, "HipL", new Vector3(-0.090f, 0f, 0f));
            a._hipR = NewJoint(a._hips, "HipR", new Vector3(0.090f, 0f, 0f));
            a._kneeL = a.SearcherLeg(a._hipL, cloth, gear, seg, rings, variant + 60, broad, fine, out a._ankleL);
            a._kneeR = a.SearcherLeg(a._hipR, cloth, gear, seg, rings, variant + 80, broad, fine, out a._ankleR);

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
                                      float side, out Transform hand)
        {
            const float upperLen = 0.300f, foreLen = 0.270f;

            // Radii read PROXIMAL first now (see the orientation note on MeshUtil.Limb). These used to
            // be the other way round, which built every sleeve 12 cm across at the shoulder and 14 at
            // the elbow — a limb at its thinnest exactly where it meets the body, which is most of what
            // the "stick people" report was describing. 19 cm at the shoulder tapering to 12 at the
            // wrist is a down sleeve, and against a 41 cm chest that is the ratio a parka actually has.
            Part(shoulder, "Deltoid", MeshUtil.Blob(0.098f * broad, 0.104f, 0.096f * broad, 5, seg, variant, 0.03f),
                 cloth, new Vector3(0f, -0.010f, 0f));
            PartDown(shoulder, "Upper", MeshUtil.Limb(0.095f * broad, 0.076f * broad, upperLen, rings, seg, variant + 1, 0.02f), cloth);

            var elbow = NewJoint(shoulder, "Elbow", new Vector3(0f, -upperLen, 0f));
            PartDown(elbow, "Fore", MeshUtil.Limb(0.076f * broad, 0.060f * broad, foreLen, rings, seg, variant + 2, 0.02f), cloth);
            // Reinforcement patch on the BACK of the elbow, where the olecranon is and where a real
            // shell wears through. Rounded caps already keep the joint solid through its bend, so this
            // is the clothing break on top of that — a nicety, and it stays gated.
            if (fine)
                Part(elbow, "ElbowPatch", MeshUtil.Blob(0.072f * broad, 0.060f, 0.056f, 5, seg, variant + 5, 0.04f),
                     gear, new Vector3(0f, -0.006f, -0.022f));

            hand = NewJoint(elbow, "Hand", new Vector3(0f, -foreLen, 0f));
            if (fine)
                Part(hand, "Cuff", MeshUtil.Torus(0.066f * broad, 0.020f, seg, 6, variant + 3, 0.06f),
                     gear, new Vector3(0f, 0.014f, 0f));
            // A glove with fingers and a thumb, welded into one mesh — see GlovedHand. This used to be
            // a single Blob, which is the owner's "circles/spheres for hands" at searcher scale.
            Part(hand, "Glove", GlovedHand(seg, variant + 4, fine, side), gear, Vector3.zero);
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
                                      int seg, int rings, int variant, float broad, bool fine,
                                      out Transform ankle)
        {
            const float thighLen = 0.470f, shinLen = 0.440f;

            // Proximal radius first, same correction as the arm: 20 cm at the hip down to 12.6 at the
            // ankle. It used to run the other way and put the leg at its widest at the KNEE.
            PartDown(hip, "Thigh", MeshUtil.Limb(0.100f * broad, 0.080f * broad, thighLen, rings, seg, variant, 0.02f), cloth);
            var knee = NewJoint(hip, "Knee", new Vector3(0f, -thighLen, 0f));
            PartDown(knee, "Shin", MeshUtil.Limb(0.080f * broad, 0.063f * broad, shinLen, rings, seg, variant + 1, 0.02f), cloth);
            // NOT gated, unlike the other small parts. The knee is the most-bent joint on the figure,
            // and it is where the old point-ended limbs showed daylight straight through the leg — so
            // this is carrying the fix rather than decorating it. It doubles as the clothing break that
            // stops the leg reading as one smooth taper from hip to boot.
            Part(knee, "KneePad", MeshUtil.Blob(0.082f * broad, 0.072f, 0.066f, 5, seg, variant + 4, 0.04f),
                 gear, new Vector3(0f, -0.004f, 0.026f));

            ankle = NewJoint(knee, "Ankle", new Vector3(0f, -shinLen, 0f));
            if (fine)
                Part(ankle, "BootCuff", MeshUtil.Torus(0.078f * broad, 0.024f, seg, 6, variant + 2, 0.07f),
                     gear, new Vector3(0f, 0.014f, 0f));
            // A boot with a sole, an instep, a toe cap and a heel, welded into one mesh — see BootMesh.
            // The ankle is handed back now so the foot can actually roll through the step; a foot
            // welded rigidly to the shin slides flat across the snow no matter what the leg does.
            Part(ankle, "Boot", BootMesh(seg, variant + 3, fine), gear, Vector3.zero);
            return knee;
        }

        /// <summary>Yeti arm: shoulder -> deltoid -> upper -> elbow -> forearm -> wrist -> hand.</summary>
        private Transform YetiArm(Transform shoulder, Material fur, Material hide,
                                  int seg, int rings, int variant, bool fine, float side)
        {
            const float upperLen = 0.80f, foreLen = 0.72f;
            // A deltoid cap, so the arm emerges from the yoke instead of looking speared into it. On a
            // body this wide through the shoulders it does more work than it does on a searcher.
            Part(shoulder, "Deltoid", MeshUtil.Blob(0.205f, 0.200f, 0.195f, 5, seg, variant, 0.09f),
                 fur, new Vector3(0f, -0.02f, 0f));
            // Proximal radius first (see the orientation note on MeshUtil.Limb) — thick where it meets
            // the yoke, tapering to the wrist. An ape's arm is a column, not a spindle.
            PartDown(shoulder, "Upper", MeshUtil.Limb(0.185f, 0.152f, upperLen, rings, seg, variant + 1, 0.05f), fur);
            var elbow = NewJoint(shoulder, "Elbow", new Vector3(0f, -upperLen, 0f));
            PartDown(elbow, "Fore", MeshUtil.Limb(0.158f, 0.126f, foreLen, rings, seg, variant + 2, 0.05f), fur);
            // A real wrist joint rather than hanging the hand off the elbow: the hand has to be able to
            // roll flat onto its knuckles at the plant, and it cannot do that welded to the forearm.
            var wrist = NewJoint(elbow, "Wrist", new Vector3(0f, -foreLen, 0f));
            Part(wrist, "Hand", ApeHand(seg, variant + 3, fine, side), hide, Vector3.zero);
            return elbow;
        }

        /// <summary>Yeti leg: hip -> thigh -> knee -> shin -> ankle -> foot. Hands back the ankle.</summary>
        private Transform YetiLeg(Transform hip, Material fur, Material hide,
                                  int seg, int rings, int variant, bool fine, float side,
                                  out Transform ankle)
        {
            const float thighLen = 0.55f, shinLen = 0.50f;
            PartDown(hip, "Thigh", MeshUtil.Limb(0.245f, 0.192f, thighLen, rings, seg, variant, 0.05f), fur);
            var knee = NewJoint(hip, "Knee", new Vector3(0f, -thighLen, 0f));
            PartDown(knee, "Shin", MeshUtil.Limb(0.196f, 0.150f, shinLen, rings, seg, variant + 1, 0.05f), fur);
            ankle = NewJoint(knee, "Ankle", new Vector3(0f, -shinLen, 0f));
            Part(ankle, "Foot", ApeFoot(seg, variant + 2, fine, side), hide, Vector3.zero);
            return knee;
        }

        /// <summary>
        /// A knuckle-walker's hand, welded into one mesh.
        ///
        /// The single ellipsoid this replaces is the owner's *"circles/spheres for hands"*, and it
        /// matters more here than anywhere else in the game: once the animal is on its knuckles these
        /// are the closest part of it to whoever it is charging, at ground level and moving fast.
        ///
        /// Built in the WRIST's frame, hanging along -Y. The fingers are curled BACK under the knuckle
        /// row rather than extended, because that is the posture the whole animal rests its weight on —
        /// an ape walks on the middle phalanges, not on its palm and not on its fingertips. It is also
        /// what makes the hand read as a fist-sized block from any distance, which is the shape you
        /// want when the alternative is a sphere.
        /// </summary>
        private static Mesh ApeHand(int seg, int variant, bool fine, float side)
        {
            var g = new MeshUtil.MeshGroup();
            int s = fine ? seg : 5, ds = fine ? 6 : 4;
            // Back of the hand: broad and flat, not round.
            g.Add(MeshUtil.Blob(0.150f, 0.118f, 0.092f, 5, s, variant, 0.07f), new Vector3(0f, -0.108f, 0.005f));
            // The knuckle row it walks on — proud of the back of the hand and pushed forward.
            g.Add(MeshUtil.Blob(0.146f, 0.058f, 0.076f, 5, s, variant + 1, 0.09f), new Vector3(0f, -0.200f, 0.050f));
            // Four fingers, folded back under the knuckles. Rotating -62 degrees about X points each
            // one up-and-back from the knuckle row, tucking it beneath the palm.
            for (int f = 0; f < 4; f++)
            {
                float u = (f + 0.5f) / 4f;
                float x = Mathf.Lerp(-0.100f, 0.100f, u);
                float len = 0.132f - Mathf.Abs(u - 0.45f) * 0.10f;   // middle pair longest
                g.Add(MeshUtil.Limb(0.034f, 0.029f, len, 4, ds, variant + 10 + f, 0.05f),
                      new Vector3(x, -0.216f, 0.072f), new Vector3(-62f, 0f, 0f));
            }
            // Thumb: shorter, set inboard and further back up the hand, angled across the palm.
            g.Add(MeshUtil.Limb(0.037f, 0.030f, 0.088f, 4, ds, variant + 20, 0.05f),
                  new Vector3(-0.088f * side, -0.150f, 0.020f), new Vector3(-140f, 0f, -32f * side));
            return g.Build();
        }

        /// <summary>
        /// The foot that leaves the footprint the entire game is about, welded into one mesh.
        ///
        /// An ape's foot is a HAND: a long flat sole, four toes across the front, and a big divergent
        /// hallux set back down the inside edge. That outline is the most recognisable thing about a
        /// yeti track, and `ClueMarker` has been stamping footprints into the snow as the searchers'
        /// win condition since the first build — so the foot casting them should agree with them.
        ///
        /// Built in the ANKLE's frame. The sole is long rather than thick: at the distance this is read
        /// from, length is the only property that separates a foot from a lump.
        /// </summary>
        private static Mesh ApeFoot(int seg, int variant, bool fine, float side)
        {
            var g = new MeshUtil.MeshGroup();
            int s = fine ? seg : 5, ds = fine ? 6 : 4;
            g.Add(MeshUtil.Blob(0.150f, 0.070f, 0.245f, 6, s, variant, 0.05f), new Vector3(0f, -0.055f, 0.072f));
            // Heel pad, dropped and set back — it is what the weight lands on.
            g.Add(MeshUtil.Blob(0.118f, 0.082f, 0.108f, 5, s, variant + 1, 0.07f), new Vector3(0f, -0.045f, -0.085f));
            // Four toes across the front. Euler(90,0,0) takes the limb's +Y to +Z, i.e. forward.
            for (int t = 0; t < 4; t++)
            {
                float u = (t + 0.5f) / 4f;
                float x = Mathf.Lerp(-0.098f, 0.098f, u) * side;
                float len = 0.086f - Mathf.Abs(u - 0.35f) * 0.042f;
                g.Add(MeshUtil.Limb(0.031f, 0.026f, len, 4, ds, variant + 10 + t, 0.05f),
                      new Vector3(x, -0.060f, 0.278f), new Vector3(90f, 0f, 0f));
            }
            // The hallux: bigger than the others, set BACK along the inside edge and swung inward.
            // This is the toe that makes the print unmistakably not a bear's, which is exactly the
            // question Mara's analysis specialty exists to answer.
            g.Add(MeshUtil.Limb(0.043f, 0.034f, 0.108f, 4, ds, variant + 20, 0.05f),
                  new Vector3(-0.126f * side, -0.058f, 0.118f), new Vector3(90f, -52f * side, 0f));
            return g.Build();
        }

        // ------------------------------------------------------------------ rig plumbing

        private static Transform NewJoint(Transform parent, string name, Vector3 localPos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            return go.transform;
        }

        /// <summary>Hands back the renderer so a caller can re-point its material later — the head does
        /// this, because the skin tone belongs to a character that has not been dealt yet at build
        /// time. Every other caller ignores the return.</summary>
        private Renderer Part(Transform parent, string name, Mesh mesh, Material mat, Vector3 localPos)
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
            // A renderer created while the body is hidden must stay hidden. The owner's own figure is
            // invisible in first person, so without this their specialty kit would pop into their own
            // view the moment the deal landed.
            r.enabled = _visible;
            if (_buildingKit) { _kitMeshes.Add(mesh); _kitRenderers.Add(r); _kitObjects.Add(go); }
            else { _meshes.Add(mesh); _renderers.Add(r); }
            return r;
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

        // ------------------------------------------------------------------ the five, as faces

        /// <summary>
        /// One character's colouring and hair. Keyed by SPECIALTY id, because in this game the
        /// specialty *is* the character — "analysis" is Mara Okonkwo and nobody else — so the same
        /// person looks the same in every match instead of being reshuffled by a network id.
        ///
        /// This is the one place appearance is NOT hashed from the variant. Everything else about a
        /// searcher (build, lumpiness, kit jag) varies per player so five bodies are not one body five
        /// times; identity does the opposite job and has to be stable.
        /// </summary>
        private struct Look
        {
            public int Skin, Hair;
            public byte Style;   // see HairMesh
            public Look(int skin, int hair, byte style) { Skin = skin; Hair = hair; Style = style; }
        }

        // STORY.md's five. Tones are picked to separate at NIGHT, which is a narrower range than
        // daylight — the darkest and lightest here still have to be distinguishable under a torch.
        private static readonly System.Collections.Generic.Dictionary<string, Look> Looks =
            new System.Collections.Generic.Dictionary<string, Look>
            {
                { "analysis",  new Look(0x6b4a34, 0x151009, 3) },  // Dr. Mara Okonkwo
                { "photo",     new Look(0xc49a78, 0x6d4c2e, 0) },  // Eli Vance
                { "tracking",  new Look(0xa87c58, 0x3b2519, 1) },  // Wren Castellano
                { "sound",     new Look(0xd0a37c, 0x181310, 0) },  // Theo Park
                { "endurance", new Look(0x8c5f42, 0x1b1510, 2) },  // Sam Reyes
                { "",          new Look(0xa87c58, 0x2b2018, 0) },  // not dealt yet / lobby
            };

        private static Look LookFor(string id)
        {
            return Looks.TryGetValue(id ?? "", out var l) ? l : Looks[""];
        }

        // Skin, hair and eye materials. Shared statics keyed by colour, for the same reason the kit's
        // are — SRP Batcher keys on material — and there are at most eleven of them for the whole
        // process. Each lookup re-tests for a destroyed object, so a scene reload self-heals.
        private static System.Collections.Generic.Dictionary<int, Material> _skinCache, _hairCache;
        private static Material _eyeDark;

        private static Material SkinMat(int hex)
        {
            if (_skinCache == null) _skinCache = new System.Collections.Generic.Dictionary<int, Material>();
            if (_skinCache.TryGetValue(hex, out var m) && m != null) return m;
            // Skin is NOT cloth, and the difference is entirely in the specular response: it has a
            // soft sheen and a very fine grain, so a torch beam glances off a cheekbone. Left matte it
            // reads as clay, which is the failure mode a flat face colour would have had anyway.
            m = MeshUtil.Surface(MeshUtil.Rgb(hex), 0.30f, ProcTex.FabricNormal, 0.30f, 16f);
            _skinCache[hex] = m;
            return m;
        }

        private static Material HairMat(int hex)
        {
            if (_hairCache == null) _hairCache = new System.Collections.Generic.Dictionary<int, Material>();
            if (_hairCache.TryGetValue(hex, out var m) && m != null) return m;
            // FurNormal is a directional strand pattern, which is exactly what hair is — this is the
            // one place in the project where the Yeti's coat texture is the right answer for a person.
            m = MeshUtil.Surface(MeshUtil.Rgb(hex), 0.34f, ProcTex.FurNormal, 0.85f, 9f);
            _hairCache[hex] = m;
            return m;
        }

        /// <summary>Eyes. Near-black with a trace of sheen, so they catch a torch and read as wet.</summary>
        private static Material EyeDark => _eyeDark != null ? _eyeDark
            : (_eyeDark = MeshUtil.Surface(MeshUtil.Rgb(0x14100e), 0.78f));

        private Material SkinMaterial() { return SkinMat(LookFor(_kitId).Skin); }

        /// <summary>
        /// A searcher's head, welded into one mesh: cranium, brow, nose, cheeks, jaw and ears.
        ///
        /// Every feature here is one to three centimetres, and past about eight metres none of them is
        /// individually resolvable — which is not the argument against them it sounds like. What they
        /// produce collectively is a head whose SHADING breaks up: a brow that shadows the eyes, a nose
        /// that catches a torch from one side, a jawline that separates the head from the neck. A
        /// smooth ovoid takes one even wash of light from any angle, and that is what "mannequin"
        /// actually looks like. Up close — a revive, the title cinematic — they read as a face.
        ///
        /// One mesh, one renderer, so all of it costs exactly what the single ellipsoid did.
        /// </summary>
        private static Mesh SearcherHead(int seg, int rings, int variant, bool fine)
        {
            var g = new MeshUtil.MeshGroup();
            // Cranium. Slightly taller than wide and flattened at the back, like a real one.
            g.Add(MeshUtil.Blob(0.079f, 0.094f, 0.086f, rings, seg, variant, 0.022f), new Vector3(0f, 0.095f, -0.012f));
            // The face mass: forehead down to the mouth, pushed forward of the cranium.
            g.Add(MeshUtil.Blob(0.066f, 0.062f, 0.060f, 5, seg, variant + 1, 0.018f), new Vector3(0f, 0.052f, 0.026f));
            // Brow ridge — the single most valuable feature, here for the same reason it is on the
            // Yeti: it is what puts the eyes in shadow instead of leaving them stuck on a curve.
            g.Add(MeshUtil.Blob(0.066f, 0.015f, 0.024f, 5, seg, variant + 2, 0.03f), new Vector3(0f, 0.089f, 0.052f));
            // Nose. Small, and the only thing on the head that breaks the profile silhouette.
            g.Add(MeshUtil.Blob(0.015f, 0.023f, 0.026f, 5, seg, variant + 3, 0.02f), new Vector3(0f, 0.058f, 0.070f));
            // Jaw and chin, which is what stops the head from being a ball sitting on a neck.
            g.Add(MeshUtil.Blob(0.052f, 0.034f, 0.046f, 5, seg, variant + 4, 0.025f), new Vector3(0f, 0.019f, 0.032f));
            if (fine)
            {
                // Cheekbones and ears. Pure close-up detail, and the first thing to drop on the cheap
                // tier — but ears in particular are what a head silhouetted against snow is missing.
                foreach (float sx in new[] { -1f, 1f })
                {
                    g.Add(MeshUtil.Blob(0.020f, 0.018f, 0.026f, 5, seg, variant + 5, 0.02f),
                          new Vector3(sx * 0.048f, 0.062f, 0.044f));
                    g.Add(MeshUtil.Blob(0.009f, 0.023f, 0.016f, 5, seg, variant + 6, 0.03f),
                          new Vector3(sx * 0.077f, 0.080f, -0.008f));
                }
            }
            return g.Build();
        }

        /// <summary>
        /// Hair, welded into one mesh, in the head's frame. Style comes from the character.
        ///
        /// WHY IT IS WORTH GEOMETRY. Hair is the fastest identity cue a human silhouette has — faster
        /// than the face, because it changes the OUTLINE of the head, and outline is what survives fog
        /// and darkness ([legibility]). It is also most of the difference between "a person" and "a
        /// figure": a bare procedural skull reads as a shop dummy no matter how good the face on it is.
        ///
        /// Styles: 0 crop, 1 tied-back tail, 2 short under a knot, 3 full natural. All of them start
        /// from the same skull cap, which is the part that actually does the work; the rest is
        /// silhouette on top of it.
        /// </summary>
        private static Mesh HairMesh(byte style, int seg, int variant, bool fine)
        {
            var g = new MeshUtil.MeshGroup();
            // Skull cap: sits just proud of the cranium and stops short of the brow, so there is a
            // hairline rather than hair growing out of the eyebrows.
            float bulk = style == 3 ? 0.020f : 0.008f;
            g.Add(MeshUtil.Blob(0.081f + bulk, 0.092f + bulk, 0.088f + bulk, 6, seg, variant, 0.05f),
                  new Vector3(0f, 0.102f, -0.020f));
            switch (style)
            {
                case 1:  // tied back — a tail clear of the collar, which reads at range as movement
                    g.Add(MeshUtil.Blob(0.030f, 0.034f, 0.030f, 5, seg, variant + 1, 0.07f), new Vector3(0f, 0.088f, -0.098f));
                    g.Add(MeshUtil.Limb(0.026f, 0.014f, 0.115f, 5, fine ? 7 : 5, variant + 2, 0.09f),
                          new Vector3(0f, 0.080f, -0.112f), new Vector3(160f, 0f, 0f));
                    break;
                case 2:  // knotted up, out of the way — the practical one
                    g.Add(MeshUtil.Blob(0.040f, 0.036f, 0.038f, 5, seg, variant + 1, 0.08f), new Vector3(0f, 0.150f, -0.070f));
                    break;
                case 3:  // full natural, carried above the crown
                    g.Add(MeshUtil.Blob(0.086f, 0.062f, 0.086f, 6, seg, variant + 1, 0.10f), new Vector3(0f, 0.150f, -0.020f));
                    break;
            }
            if (fine && style != 3)
            {
                // Sideburns. Two centimetres of geometry that close the gap between the cap and the
                // jaw, which is otherwise a bare stripe up the side of the head.
                foreach (float sx in new[] { -1f, 1f })
                    g.Add(MeshUtil.Blob(0.012f, 0.028f, 0.026f, 5, seg, variant + 3, 0.04f),
                          new Vector3(sx * 0.074f, 0.072f, 0.006f));
            }
            return g.Build();
        }

        /// <summary>
        /// A gloved hand with digits, welded into one mesh, in the wrist's frame hanging along -Y.
        ///
        /// Same complaint as the Yeti's — the owner's *"circles/spheres for hands"* — and the same
        /// answer, at a fifth the scale. A mitt is a shape; a sphere is a primitive, and the eye names
        /// a primitive instantly ([legibility]). This is also the part of a teammate you are closest to
        /// during a revive, which is the one moment in the game two searchers are within a metre.
        /// </summary>
        private static Mesh GlovedHand(int seg, int variant, bool fine, float side)
        {
            var g = new MeshUtil.MeshGroup();
            int s = fine ? seg : 5, ds = fine ? 6 : 4;
            // Back of the hand — flat front-to-back, which is what a hand is and a ball is not.
            g.Add(MeshUtil.Blob(0.053f, 0.062f, 0.031f, 5, s, variant, 0.035f), new Vector3(0f, -0.052f, 0.006f));
            // Four fingers, held together the way a glove holds them, curled slightly forward.
            for (int f = 0; f < 4; f++)
            {
                float u = (f + 0.5f) / 4f;
                float x = Mathf.Lerp(-0.036f, 0.036f, u);
                float len = 0.058f - Mathf.Abs(u - 0.42f) * 0.030f;
                g.Add(MeshUtil.Limb(0.0145f, 0.0125f, len, 4, ds, variant + 10 + f, 0.03f),
                      new Vector3(x, -0.098f, 0.008f), new Vector3(166f, 0f, 0f));
            }
            // Thumb, inboard and forward — the one digit that has to be separate to read as a hand.
            g.Add(MeshUtil.Limb(0.017f, 0.014f, 0.050f, 4, ds, variant + 20, 0.03f),
                  new Vector3(-0.044f * side, -0.062f, 0.014f), new Vector3(146f, 0f, -30f * side));
            return g.Build();
        }

        /// <summary>
        /// A mountaineering boot, welded into one mesh, in the ankle's frame.
        ///
        /// The ellipsoid this replaces was doing one job well — the dark foot is what visually PLANTS a
        /// figure on snow, the same tell SSAO went in to fix — and it still does. What it could not do
        /// is look like footwear: a boot is a stiff sole, a raised instep, a reinforced toe and a heel,
        /// and every one of those is a hard edge that catches a raking torch beam.
        /// </summary>
        private static Mesh BootMesh(int seg, int variant, bool fine)
        {
            var g = new MeshUtil.MeshGroup();
            int s = fine ? seg : 5;
            g.Add(MeshUtil.Blob(0.070f, 0.021f, 0.134f, 5, s, variant, 0.02f), new Vector3(0f, -0.056f, 0.036f));      // sole
            g.Add(MeshUtil.Blob(0.063f, 0.047f, 0.098f, 5, s, variant + 1, 0.03f), new Vector3(0f, -0.024f, 0.018f));  // instep
            g.Add(MeshUtil.Blob(0.057f, 0.031f, 0.048f, 5, s, variant + 2, 0.03f), new Vector3(0f, -0.038f, 0.104f));  // toe cap
            g.Add(MeshUtil.Blob(0.051f, 0.030f, 0.040f, 5, s, variant + 3, 0.03f), new Vector3(0f, -0.040f, -0.046f)); // heel
            return g.Build();
        }

        // ------------------------------------------------------------------ the specialty kit

        // Shared by every searcher rather than owned per-avatar. URP's SRP Batcher keys on MATERIAL,
        // so five searchers each carrying their own copy of "dark instrument casing" is five batches
        // where one would do. They are static and deliberately never destroyed — four
        // process-lifetime materials — which is also why each getter re-tests for null: a scene
        // reload destroys them behind our back, Unity's == reports a destroyed object as null, and
        // the next access rebuilds. Same self-healing shape as Weather.EnsureSystems.
        private static Material _kitHard, _kitGlass, _kitHiVis, _kitRope;

        private static Material KitHard => _kitHard != null ? _kitHard
            : (_kitHard = MeshUtil.Surface(MeshUtil.Rgb(0x1b1c20), 0.42f, ProcTex.MetalNormal, 0.7f, 6f, metallic: 0.35f));
        private static Material KitGlass => _kitGlass != null ? _kitGlass
            : (_kitGlass = MeshUtil.Emissive(MeshUtil.Rgb(0x080c12), MeshUtil.Rgb(0x35708f), 0.9f));
        private static Material KitHiVis => _kitHiVis != null ? _kitHiVis
            : (_kitHiVis = MeshUtil.Surface(MeshUtil.Rgb(0xd8562c), 0.30f, ProcTex.FabricNormal, 0.8f, 4f));
        private static Material KitRope => _kitRope != null ? _kitRope
            : (_kitRope = MeshUtil.Surface(MeshUtil.Rgb(0xb8a274), 0.18f, ProcTex.FabricNormal, 1.4f, 10f));

        /// <summary>
        /// Hang the searcher's specialty kit on the figure — Theo's cans and boom, Eli's camera,
        /// Wren's rope and flags, Sam's litter roll, Mara's sample case.
        ///
        /// WHY THIS IS GEOMETRY AND NOT JUST THE PARKA COLOUR. The colour already tells you that two
        /// figures are different people. It does not tell you WHICH person, because at the range and
        /// light level this game is played at, a specialty hue on a parka is a dark shape next to
        /// another dark shape. A silhouette prop survives that: a boom mic sticking up off a pack
        /// reads as Theo through fog, at night, in monochrome. "Who is that across the valley" is a
        /// tactical question here — it decides whether you walk over for a revive or keep filming —
        /// so it deserves a signal that works at the distance the question actually gets asked.
        ///
        /// LAZY, and it has to be. The specialty is dealt AFTER the body is built (the same fact that
        /// forces SetTint to be re-checked rather than read once in BuildVisuals), so a kit cannot be
        /// part of BuildSearcher. Building all five and hiding four would cost five times the
        /// geometry on every searcher to show one. This builds on CHANGE and tears the previous kit
        /// down first, so a re-deal mid-session is clean and an idle frame costs nothing.
        ///
        /// NOTHING HERE IS REPLICATED. It reads the specialty the match already agreed on, and adds
        /// no SyncVar and no RPC — exactly like the rest of the animation layer.
        /// </summary>
        public void SetSpecialty(string id)
        {
            if (_isYeti || _root == null) return;
            id = id ?? "";
            if (_kitId == id) return;   // called from a per-frame re-check; do nothing on that path
            _kitId = id;

            ClearKit();

            // The character's own colouring. Re-pointed rather than rebuilt: the head is geometry that
            // does not change between people, and swapping a shared material reference is free.
            var look = LookFor(id);
            if (_headRend != null) _headRend.sharedMaterial = SkinMat(look.Skin);

            _buildingKit = true;
            try
            {
                int seg = HPQuality.HighDetail ? 10 : 7;
                bool fine = HPQuality.HighDetail;

                // Hair is dealt with the character and torn down with it, which is why it lives here
                // and not in BuildSearcher — the style belongs to the person, and nobody knows who
                // this is at build time. It runs for the EMPTY id too (the lobby, an undealt player):
                // a searcher with no specialty is still a person and still has hair, and the early-out
                // that used to sit here left them bald.
                Part(_head, "Hair", HairMesh(look.Style, seg, _variant + 90, fine),
                     HairMat(look.Hair), Vector3.zero);

                switch (id)
                {
                    case "sound": BuildSoundKit(seg, _variant); break;
                    case "photo": BuildPhotoKit(seg, fine, _variant); break;
                    case "tracking": BuildTrackingKit(seg, fine, _variant); break;
                    case "endurance": BuildEnduranceKit(seg, fine, _variant); break;
                    case "analysis": BuildAnalysisKit(seg, fine, _variant); break;
                }
            }
            finally { _buildingKit = false; }
        }

        /// <summary>Drop the current kit's meshes, renderers and joints. Safe to call with no kit.</summary>
        private void ClearKit()
        {
            for (int i = 0; i < _kitObjects.Count; i++)
                if (_kitObjects[i] != null) Object.Destroy(_kitObjects[i]);
            for (int i = 0; i < _kitMeshes.Count; i++)
                if (_kitMeshes[i] != null) Object.Destroy(_kitMeshes[i]);
            _kitObjects.Clear();
            _kitMeshes.Clear();
            _kitRenderers.Clear();
        }

        /// <summary>An empty transform a kit hangs parts off, tracked so ClearKit drops it too.</summary>
        private Transform KitJoint(Transform parent, string name, Vector3 pos, Vector3 euler)
        {
            var t = NewJoint(parent, name, pos);
            t.localRotation = Quaternion.Euler(euler);
            _kitObjects.Add(t.gameObject);
            return t;
        }

        /// <summary>Theo — headphone cans, a band over the crown, a shotgun mic off the pack.</summary>
        private void BuildSoundKit(int seg, int v)
        {
            foreach (float sx in new[] { -0.116f, 0.116f })
                Part(_head, "Can", MeshUtil.Blob(0.050f, 0.062f, 0.038f, 5, seg, v + 61, 0.03f),
                     KitHard, new Vector3(sx, 0.074f, -0.012f));
            // Euler(0,0,90) takes the ring's own axis (+Y) onto X, so the hoop stands upright in the
            // plane you view it from and arcs over the crown instead of lying flat like a halo.
            PartAt(_head, "Band", MeshUtil.Torus(0.118f, 0.014f, seg, 6, v + 62, 0f, 1f, 0.85f),
                   KitHard, new Vector3(0f, 0.086f, -0.014f), new Vector3(0f, 0f, 90f));

            // The boom is the tallest thing on any searcher, on purpose: it breaks the head-and-
            // shoulders outline all five otherwise share, which is what makes Theo nameable at range.
            var boom = KitJoint(_torso, "Boom", new Vector3(0.088f, 0.430f, -0.196f), new Vector3(-26f, 0f, -13f));
            Part(boom, "Shaft", MeshUtil.Limb(0.019f, 0.014f, 0.40f, 5, 7, v + 63, 0.015f), KitHard, Vector3.zero);
            // Fat, matte and obviously fabric against the hard shaft. That contrast is the thing that
            // says "microphone" rather than "aerial".
            Part(boom, "Windshield", MeshUtil.Blob(0.042f, 0.055f, 0.042f, 5, seg, v + 64, 0.09f),
                 _gearMat, new Vector3(0f, 0.425f, 0f));
        }

        /// <summary>Eli — a camera on a neck strap, lens forward, flash on the shoulder.</summary>
        private void BuildPhotoKit(int seg, bool fine, int v)
        {
            Part(_torso, "CamStrap", MeshUtil.Torus(0.104f, 0.013f, seg, 6, v + 71, 0.03f, 1f, 0.62f),
                 _gearMat, new Vector3(0f, 0.487f, 0.004f));
            Part(_torso, "CamBody", MeshUtil.Blob(0.072f, 0.054f, 0.042f, 5, seg, v + 72, 0.02f),
                 KitHard, new Vector3(0f, 0.322f, 0.150f));
            // Euler(90,0,0) takes the lathe's build axis (+Y) onto +Z, so the barrel points where the
            // searcher is facing rather than at the sky.
            var lens = KitJoint(_torso, "Lens", new Vector3(0f, 0.322f, 0.176f), new Vector3(90f, 0f, 0f));
            Part(lens, "Barrel", MeshUtil.Limb(0.034f, 0.030f, 0.056f, 5, seg, v + 73, 0.01f), KitHard, Vector3.zero);
            // A cold glint on the front element — the one emissive thing on a searcher, and worth the
            // exception: a lens catching light is how a camera reads at night in a single frame.
            Part(lens, "Glass", MeshUtil.Blob(0.028f, 0.007f, 0.028f, 5, seg, v + 74, 0f),
                 KitGlass, new Vector3(0f, 0.058f, 0f));
            if (fine)
                Part(_torso, "Flash", MeshUtil.Blob(0.038f, 0.032f, 0.026f, 5, seg, v + 75, 0.02f),
                     KitHard, new Vector3(-0.148f, 0.502f, 0.046f));
        }

        /// <summary>Wren — a rope coil worn bandolier-style, and marker flags off the pack.</summary>
        private void BuildTrackingKit(int seg, bool fine, int v)
        {
            // Rotated 58 degrees so the coil crosses the chest on the diagonal. Worn level it reads as
            // a hoop stuck onto a torso; the diagonal is what makes it something SLUNG.
            PartAt(_torso, "RopeCoil", MeshUtil.Torus(0.148f, 0.027f, seg, 7, v + 81, 0.06f, 1f, 0.58f),
                   KitRope, new Vector3(0.010f, 0.330f, 0.012f), new Vector3(0f, 0f, 58f));

            int flags = fine ? 3 : 2;
            for (int i = 0; i < flags; i++)
            {
                // Splayed rather than parallel: a fan of thin rods survives being one pixel wide,
                // where three rods in a line collapse into a single stroke.
                var rod = KitJoint(_torso, "FlagRod", new Vector3(-0.104f + i * 0.052f, 0.440f, -0.198f),
                                   new Vector3(-16f, 0f, -12f + i * 9f));
                Part(rod, "Rod", MeshUtil.Limb(0.006f, 0.005f, 0.30f, 5, 5, v + 82 + i, 0.02f), KitHard, Vector3.zero);
                Part(rod, "Cloth", MeshUtil.Blob(0.030f, 0.038f, 0.005f, 5, 5, v + 85 + i, 0.04f),
                     KitHiVis, new Vector3(0.026f, 0.268f, 0f));
            }
        }

        /// <summary>Sam — a rolled litter across the top of the pack, and a med pouch on the belt.</summary>
        private void BuildEnduranceKit(int seg, bool fine, int v)
        {
            // The widest horizontal in any of the five kits, and that is the point: Sam is who you
            // look for when somebody is down, so Sam has to be identifiable FROM BEHIND and at range
            // — the one angle none of the other four kits work from.
            var roll = KitJoint(_torso, "Litter", new Vector3(0.215f, 0.472f, -0.186f), new Vector3(0f, 0f, 90f));
            Part(roll, "Roll", MeshUtil.Limb(0.055f, 0.055f, 0.43f, 5, seg, v + 91, 0.03f), _gearMat, Vector3.zero);
            Part(roll, "Tie", MeshUtil.Torus(0.060f, 0.010f, seg, 6, v + 92, 0.04f), KitHard, new Vector3(0f, 0.115f, 0f));
            Part(roll, "Tie2", MeshUtil.Torus(0.060f, 0.010f, seg, 6, v + 93, 0.04f), KitHard, new Vector3(0f, 0.315f, 0f));

            Part(_torso, "MedPouch", MeshUtil.Blob(0.060f, 0.068f, 0.044f, 5, seg, v + 94, 0.03f),
                 _gearMat, new Vector3(0.152f, 0.108f, 0.062f));
            if (fine)
            {
                // Two bars of geometry, not a painted cross. At this size a texture is a smudge, while
                // a cross built from shapes still reads as a cross once it is three red pixels wide.
                Part(_torso, "CrossH", MeshUtil.Blob(0.030f, 0.010f, 0.006f, 5, 5, v + 95, 0f),
                     KitHiVis, new Vector3(0.152f, 0.108f, 0.104f));
                Part(_torso, "CrossV", MeshUtil.Blob(0.010f, 0.030f, 0.006f, 5, 5, v + 96, 0f),
                     KitHiVis, new Vector3(0.152f, 0.108f, 0.104f));
            }
        }

        /// <summary>Mara — a hard sample case on the hip, and a vial bandolier across the chest.</summary>
        private void BuildAnalysisKit(int seg, bool fine, int v)
        {
            // FOUR segments on the lathe is not a saving, it is the shape. A 4-sided surface of
            // revolution is a box, and a box is the one silhouette among these five that cannot be
            // mistaken for another piece of soft mountain kit — which is precisely Mara's read: the
            // person carrying laboratory equipment up a mountain.
            var caseProfile = new[]
            {
                new Vector2(-0.112f, 0.004f), new Vector2(-0.104f, 0.086f),
                new Vector2( 0.104f, 0.086f), new Vector2( 0.112f, 0.004f),
            };
            var box = KitJoint(_torso, "SampleCase", new Vector3(-0.196f, 0.118f, 0.014f), new Vector3(0f, 34f, -9f));
            Part(box, "Case", MeshUtil.Lathe(caseProfile, 4, v + 101, 0f, 1f, 0.52f), KitHard, Vector3.zero);
            Part(box, "Handle", MeshUtil.Torus(0.040f, 0.009f, seg, 6, v + 102, 0f, 1f, 0.35f),
                 _gearMat, new Vector3(0f, 0.112f, 0f));

            var band = KitJoint(_torso, "Bandolier", new Vector3(0f, 0.330f, 0.088f), new Vector3(0f, 0f, 62f));
            Part(band, "Strap", MeshUtil.Limb(0.020f, 0.020f, 0.34f, 5, 6, v + 103, 0.02f),
                 _gearMat, new Vector3(0f, -0.170f, 0f));
            int vials = fine ? 3 : 2;
            for (int i = 0; i < vials; i++)
                Part(band, "Vial", MeshUtil.Limb(0.011f, 0.011f, 0.046f, 5, 5, v + 104 + i, 0f),
                     KitGlass, new Vector3(0.026f, -0.086f + i * 0.070f, 0f));
        }

        public void SetVisible(bool on)
        {
            _visible = on;
            for (int i = 0; i < _renderers.Count; i++)
                if (_renderers[i] != null) _renderers[i].enabled = on;
            for (int i = 0; i < _kitRenderers.Count; i++)
                if (_kitRenderers[i] != null) _kitRenderers[i].enabled = on;
        }

        /// <summary>
        /// Destroy the generated meshes. `new Mesh()` allocates a native object Unity's GC does not
        /// collect — the same leak the realism pass found in the world's materials, and worse here
        /// because a body is ~19 meshes and role changes rebuild it.
        /// </summary>
        public void Dispose()
        {
            ClearKit();   // the specialty kit's meshes are tracked apart from the body's
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

            if (_roarT > 0f) _roarT -= dt;
            // Fast attack, slow release: the chest is up almost immediately and comes down over a
            // second, which is the shape of the sound it goes with.
            float roarW = _roarT <= 0f ? 0f : Mathf.Clamp01(Mathf.Min((1.5f - _roarT) * 6f, _roarT * 1.4f));

            // --- THE QUADRUPEDAL BLEND, and the whole answer to "a crazy person running" ---
            //
            // The old cycle was a human walk with bigger numbers: upright spine, feet alternating,
            // arms counter-swinging one-for-one against the legs. That is not "an ape moving fast", it
            // is a person moving fast, and dressing it in fur gets you the owner's second report —
            // a muppet, which is exactly what a smooth shape doing a jaunty human walk looks like.
            //
            // So above a jog the Yeti goes down onto its knuckles. `quad` drives every part of that at
            // once: the torso pitches over, the hips drop and the knees fold to keep the feet planted,
            // the arms swing to VERTICAL so they reach the ground, the limbs re-phase from diagonal
            // couplets into a bound, and the vertical bob changes shape from two dips a cycle to one.
            //
            // A roar cancels it — the animal rears up onto two legs to bellow, which is the pose the
            // whole silhouette is worth showing off in, and it is the moment players actually look at.
            float quad = 0f;
            if (_isYeti) quad = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1.6f, 5.0f, inp.Speed)) * (1f - roarW);
            if (inp.Carrying) quad *= 0.25f;   // hauling a body, it has to stay up on two legs

            // --- gait phase: advances with GROUND COVERED, not with time ---
            //
            // Stride LENGTHENS with the gait rather than the cycle just spinning faster. That is what
            // real animals do and it is most of what makes a big one look heavy: at 7.5 m/s the old
            // fixed 3.4 m stride was 2.2 cycles a second, which is a flail no matter what the limbs
            // are doing. A 5.4 m bound is 1.4, and the difference reads as mass.
            float stride = _isYeti ? Mathf.Lerp(2.9f, 5.4f, quad) : SearcherStride;
            if (!frozen && !incap && inp.Speed > 0.05f)
                _gaitPhase += inp.Speed / stride * Mathf.PI * 2f * dt;
            _breathe += dt * (inp.Sprinting ? 2.6f : 1.1f);

            // How much of the walk cycle to apply. Below a slow walk the legs should settle rather than
            // shuffle in place, which is what a raw speed->amplitude map produces at a stop.
            float top = _isYeti ? 7.5f : 5.5f;
            float walkW = frozen || incap ? 0f : Mathf.Clamp01(inp.Speed / (top * 0.55f));
            float s = Mathf.Sin(_gaitPhase);

            // Per-limb phase offsets, blended from a WALK (diagonal couplets — left arm with right leg,
            // what every quadruped does at low speed, and what a person does too) to a BOUND (fore pair
            // together, hind pair together, hinds landing about a third of a cycle after the fores).
            // The small stagger inside each pair is deliberate: a bounding animal leads with one hand,
            // and a perfectly symmetric pair is the single most mechanical-looking thing a rig can do.
            float phArmL = _gaitPhase;
            float phArmR = _gaitPhase + Mathf.Lerp(Mathf.PI, 0.38f, quad);
            float phLegL = _gaitPhase + Mathf.Lerp(Mathf.PI, 2.10f, quad);
            float phLegR = _gaitPhase + Mathf.Lerp(0f, 2.48f, quad);

            // --- hips: bob, crouch, the quadrupedal drop, and the fall into a lying pose ---
            float crouchDrop = inp.Crouched ? (_isYeti ? 0.30f : 0.34f) : 0f;
            // Going down onto the knuckles lowers the body. Paired with the knee fold below: a 0.20 m
            // drop against two 0.5 m leg segments needs about 50 degrees more knee to keep the feet on
            // the ground, and if you retune one without the other the Yeti skates on buried shins.
            float quadDrop = quad * 0.20f;
            // Two dips per cycle is a WALK. A bound has one — the body falls onto the fore plant and
            // floats after the hind push — and that single long rise-and-fall is what reads as weight.
            float bobWalk = -Mathf.Abs(s);
            float bobBound = -(0.5f + 0.5f * Mathf.Cos(_gaitPhase));
            float bobAmp = _isYeti ? Mathf.Lerp(0.075f, 0.150f, quad) : 0.045f;
            float bob = Mathf.Lerp(bobWalk, bobBound, quad) * bobAmp * walkW;
            float breathY = Mathf.Sin(_breathe) * 0.012f * (1f - walkW);
            _hips.localPosition = Vector3.Lerp(_hips.localPosition,
                new Vector3(0f, _hipHeight - crouchDrop - quadDrop + bob + breathY, 0f),
                1f - Mathf.Exp(-14f * dt));

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

            // --- torso: lean into the turn, pitch over into the run, roll and counter-yaw ---
            _leanTarget = Mathf.Clamp(-inp.YawRate * 9f, -18f, 18f);
            _lean = Mathf.Lerp(_lean, _leanTarget, 1f - Mathf.Exp(-6f * dt));
            // The Yeti's forward pitch is now driven by `quad` rather than creeping up with raw speed:
            // 20 degrees standing and hunched, 58 on the knuckles. 58 is not a stylistic choice — it is
            // what puts the shoulder joint 0.48 m above the hips and 0.76 m ahead of them, which is
            // exactly where a 1.52 m arm hanging vertically reaches the ground. Change one, re-derive
            // the other or the hands float.
            float fwdLean = _isYeti
                ? Mathf.Lerp(20f, 58f, quad) + (inp.Crouched ? 10f : 0f) + (inp.Carrying ? 8f : 0f)
                : 6f + inp.Speed * 1.1f + (inp.Crouched ? 14f : 0f) + (inp.Carrying ? 8f : 0f);
            float torsoPitch = Mathf.Lerp(fwdLean, -22f, roarW);          // roar throws the chest up
            // Spine flex: a bounding quadruped's back arches and extends once per cycle. It is a small
            // number and it does a lot — a rigid trunk over moving limbs is a puppet on sticks.
            torsoPitch += quad * 7f * Mathf.Sin(_gaitPhase + 1.2f) * walkW;
            float torsoRoll = _lean + s * 4f * walkW * (1f - quad * 0.6f);
            // Shoulders counter the hips — but only on two legs. A galloping quadruped flexes its spine
            // VERTICALLY, not horizontally, so the human counter-yaw fades out as the animal goes down.
            float torsoYaw = -s * (_isYeti ? 7f : 5f) * walkW * (1f - quad);
            _torso.localRotation = Damp(_torso.localRotation,
                Quaternion.Euler(torsoPitch, torsoYaw, torsoRoll), 10f, dt);

            // --- head: leads the turn, and comes up on a roar ---
            // There is no replicated head pitch (the schema carries yaw only), so the head cannot track
            // a look direction. Leading the turn is the honest substitute: it is driven by yaw rate,
            // which IS replicated, and it reads as the thing looking where it is going.
            //
            // The counter-pitch term matters far more now that the torso can be over at 58 degrees:
            // without it the head follows the chest and the animal charges face-down at the snow. At
            // 0.82 the muzzle stays up and forward, which is both what a running ape does and what
            // keeps the eyeshine pointed at whoever is being hunted.
            float headYaw = Mathf.Clamp(inp.YawRate * 14f, -28f, 28f);
            float headCounter = _isYeti ? Mathf.Lerp(0.45f, 0.82f, quad) : 0.45f;
            float headPitch = Mathf.Lerp(_isYeti ? -6f : -2f, -34f, roarW) - torsoPitch * headCounter;
            _head.localRotation = Damp(_head.localRotation, Quaternion.Euler(headPitch, headYaw, 0f), 8f, dt);

            // --- limbs ---
            // Sign convention, derived once so the rest reads cleanly: a limb hangs along -Y, so a
            // POSITIVE X rotation swings it BACKWARD and a positive Z rotation swings it to the right.
            // The torso is the opposite (it points +Y), which is why torsoPitch above is positive to
            // lean forward.
            float legSwing = (_isYeti ? Mathf.Lerp(30f, 40f, quad) : 40f) * walkW;
            float armSwing = (_isYeti ? Mathf.Lerp(22f, 44f, quad) : 30f) * walkW;

            // Knees bend one way only, and the bend belongs at MID-SWING — the moment the foot has to
            // clear the ground. Peaking it off `-cos` of the limb's own phase puts it there: the leg is
            // fully back at phase pi/2 (toe-off), under the body and travelling forward at pi (peak
            // bend), fully forward at 3pi/2 (extending for the plant), and near-straight through
            // mid-stance where it is carrying weight. The previous version peaked half a cycle out from
            // this, which bent the knee as the foot came down and straightened it as the foot swung —
            // the leg reaching for the ground with a locked knee is a real part of the "stick" read.
            float kneeFold = _isYeti ? Mathf.Lerp(58f, 76f, quad) : 72f;
            float kneeBase = (inp.Crouched ? 34f : 4f) + quad * 50f;   // pairs with quadDrop above
            float kneeL = Mathf.Max(0f, -Mathf.Cos(phLegL)) * kneeFold * walkW + kneeBase;
            float kneeR = Mathf.Max(0f, -Mathf.Cos(phLegR)) * kneeFold * walkW + kneeBase;

            _hipL.localRotation = Damp(_hipL.localRotation, Quaternion.Euler(Mathf.Sin(phLegL) * legSwing, 0f, 0f), 16f, dt);
            _hipR.localRotation = Damp(_hipR.localRotation, Quaternion.Euler(Mathf.Sin(phLegR) * legSwing, 0f, 0f), 16f, dt);
            _kneeL.localRotation = Damp(_kneeL.localRotation, Quaternion.Euler(kneeL, 0f, 0f), 16f, dt);
            _kneeR.localRotation = Damp(_kneeR.localRotation, Quaternion.Euler(kneeR, 0f, 0f), 16f, dt);

            // Ankles, where a body has them. Toes down at push-off, up for the plant — a rigid foot
            // welded to the shin is one of the loudest "nothing is animated here" tells there is, and
            // on the Yeti the foot is a 0.35 m paddle that would otherwise stay flat while the leg
            // swung underneath it.
            if (_ankleL != null && _ankleR != null)
            {
                float ankleAmp = (_isYeti ? 24f : 18f) * walkW;
                _ankleL.localRotation = Damp(_ankleL.localRotation,
                    Quaternion.Euler(Mathf.Sin(phLegL) * ankleAmp, 0f, 0f), 14f, dt);
                _ankleR.localRotation = Damp(_ankleR.localRotation,
                    Quaternion.Euler(Mathf.Sin(phLegR) * ankleAmp, 0f, 0f), 14f, dt);
            }

            // Arms. At a walk they counter-swing against the legs; on the knuckles the pair moves
            // together and the whole arm is lifted back to VERTICAL, because the torso it hangs from is
            // pitched 58 degrees over and an arm hanging in that frame would point at the horizon
            // instead of at the ground.
            float armVertical = _isYeti ? torsoPitch * Mathf.Lerp(0.55f, 1f, quad) : 0f;
            float armXL = armVertical + Mathf.Sin(phArmL) * armSwing;
            float armXR = armVertical + Mathf.Sin(phArmR) * armSwing;
            // Resting flare off the ribs. It opens up as the animal goes down: knuckle-walkers plant
            // OUTSIDE the shoulder line, which is what gives the gait its rolling width.
            float armZL = _isYeti ? -Mathf.Lerp(13f, 22f, quad) : -5f, armZR = -armZL;
            // The elbow locks near-straight to take weight at the plant and folds on the recovery,
            // which is the difference between an arm bearing the animal and an arm waving about.
            float elbowSwing = _isYeti
                ? Mathf.Lerp(26f, 10f + 46f * Mathf.Max(0f, Mathf.Cos(phArmL)), quad) : 16f;
            float elbowL = elbowSwing;
            float elbowR = _isYeti
                ? Mathf.Lerp(26f, 10f + 46f * Mathf.Max(0f, Mathf.Cos(phArmR)), quad) : 16f;

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
            // Feet go slack too. A dropped body with its ankles still holding a walk pose is a
            // mannequin lying down, which is the exact opposite of what this pose is for.
            if (_ankleL != null) _ankleL.localRotation = Damp(_ankleL.localRotation, Quaternion.Euler(-22f, 0f, 0f), 5f, dt);
            if (_ankleR != null) _ankleR.localRotation = Damp(_ankleR.localRotation, Quaternion.Euler(-14f, 0f, 0f), 5f, dt);
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
