// The two figures — a searcher and the Yeti — assembled from CharacterMesh parts, plus the
// procedural gait that moves them.
//
// WHY A RIG AND NOT JUST A BETTER MESH. Replacing a capsule with a well-shaped statue fixes the
// silhouette and leaves the worse half of the problem untouched: a rigid body gliding across snow at
// 5 m/s is uncanny in a way a blocky one isn't, because the eye reads motion long before it reads
// form. There are no animation assets in this project and there will not be until Phase 6's rigged
// models, so the gait here is computed — limb angles driven from the figure's own ground speed. It is
// not a mocap walk cycle and is not trying to be. It only has to be a body whose legs go where the
// ground is going, which is the entire difference between "a person" and "a prop on a rail".
//
// This is CLIENT-SIDE PRESENTATION ONLY. Nothing here is replicated, nothing here is stepped by the
// shared sim, and nothing here can be read by any gameplay system — the gait is derived from a
// position delta that the sim has already decided. A hacked or lagging client gets an ugly walk, not
// an advantage.
//
// Proportions are in metres and are anchored to the sim: a searcher's eye is Sim.Player.EyeHeight
// (1.7) and the Yeti's is 2.4, so the heads are built to land there. Change one and the camera sits
// in the wrong part of the skull.
using System.Collections.Generic;
using UnityEngine;

namespace Metoh.Game
{
    public class CharacterRig
    {
        // --- the pieces the outside world needs to hang things off ------------------

        /// <summary>The animated body root. Parented under the caller's visual root, never to it.</summary>
        public Transform Root { get; private set; }

        /// <summary>The head, for eyeshine, the REC bead, and anything else that should follow it.</summary>
        public Transform Head { get; private set; }

        /// <summary>Where a remote searcher's flashlight lives — the right hand, held forward.</summary>
        public Transform TorchAnchor { get; private set; }

        /// <summary>Height of the crown above the figure's feet. Used to place things over the head.</summary>
        public float Height { get; private set; }

        // --- animated joints --------------------------------------------------------

        private Transform _hips, _torso, _legL, _legR, _kneeL, _kneeR, _armL, _armR, _elbowL, _elbowR;

        // --- materials --------------------------------------------------------------
        //
        // Every material is created per figure (they get individually tinted by status, so they
        // cannot be shared between players) and every one of them is destroyed in Dispose. `new
        // Material(...)` allocates a native object Unity will not collect on its own — the same leak
        // WorldBuilder.ReleaseWorldMaterials exists to plug, and six players re-rolling a role would
        // bleed the same way.
        private readonly List<(Material mat, Color baseColor)> _tint = new List<(Material, Color)>();
        private readonly List<Material> _owned = new List<Material>();
        private readonly List<Mesh> _meshes = new List<Mesh>();
        private Material _coatMat; // the searcher's parka — recoloured when the specialty is dealt
        private int _coatTintIndex = -1;

        // --- gait state ---------------------------------------------------------------

        private float _phase;      // stride phase, radians
        private float _speedSmooth;
        private float _crouch01;
        private float _limp01;
        private readonly float _cycleMetres; // ground covered by one full two-step cycle
        private readonly float _swingDeg;
        private readonly float _bobAmp;
        private readonly bool _isYeti;
        /// <summary>Rest height of the hip pivot. Read back by the pose so bob and crouch are offsets
        /// from where the builder actually put it, rather than from a second copy of the number.</summary>
        private float _hipHeight;

        private CharacterRig(bool yeti, float cycleMetres, float swingDeg, float bobAmp)
        {
            _isYeti = yeti;
            _cycleMetres = cycleMetres;
            _swingDeg = swingDeg;
            _bobAmp = bobAmp;
        }

        // ==================================================================== searcher

        /// <summary>
        /// A searcher: a person in expedition kit. Hood, pack, boots, mittens, and a torch held out in
        /// the right hand — the right arm is deliberately posed and does NOT swing, because someone
        /// carrying a light keeps that hand still, and a swinging beam looks like a fault.
        /// </summary>
        public static CharacterRig BuildSearcher(Transform parent, Color coat, int variant)
        {
            var rig = new CharacterRig(false, cycleMetres: 1.55f, swingDeg: 30f, bobAmp: 0.045f)
            {
                Height = 1.82f,
            };
            rig.Root = NewChild(parent, "Body");

            // Technical outerwear: a little sheen, woven surface. (The value this replaced was the
            // whole searcher.)
            rig._coatMat = MeshUtil.Surface(coat, 0.24f, ProcTex.FabricNormal, 0.8f, 3f);
            Material trouser = MeshUtil.Surface(MeshUtil.Rgb(0x39414b), 0.20f, ProcTex.FabricNormal, 0.7f, 3.4f);
            Material gear = MeshUtil.Surface(MeshUtil.Rgb(0x22262b), 0.30f, ProcTex.FabricNormal, 0.9f, 4f);
            Material skin = MeshUtil.Surface(MeshUtil.Rgb(0xc9a184), 0.16f, ProcTex.FabricNormal, 0.25f, 9f);
            // Retroreflective tape. Real expedition kit is covered in it, and it is the one material
            // in the game that is FOR being hit by a torch: near-mirror smoothness means a teammate's
            // beam picks a searcher out of the dark at range in a way no flat fabric can. It is also,
            // usefully, a legibility win — those bands are how you tell a person from a rock at 50 m.
            Material tape = MeshUtil.Surface(MeshUtil.Rgb(0xd8dee4), 0.92f, ProcTex.FabricNormal, 0.2f, 6f, metallic: 0.15f);

            rig._coatTintIndex = rig._tint.Count;
            rig.Track(rig._coatMat, coat);
            rig.Track(trouser, MeshUtil.Rgb(0x39414b));
            rig.Track(gear, MeshUtil.Rgb(0x22262b));
            rig.Track(skin, MeshUtil.Rgb(0xc9a184));
            rig.Track(tape, MeshUtil.Rgb(0xd8dee4));

            // --- hips + legs ---------------------------------------------------------
            rig._hipHeight = 0.92f;
            rig._hips = NewChild(rig.Root, "Hips");
            rig._hips.localPosition = new Vector3(0f, rig._hipHeight, 0f);

            for (int side = -1; side <= 1; side += 2)
            {
                var hip = NewChild(rig._hips, side < 0 ? "LegL" : "LegR");
                hip.localPosition = new Vector3(side * 0.115f, -0.02f, 0f);
                rig.Part(hip, "Thigh", CharacterMesh.LimbDown(0.46f, 0.105f, 0.115f, 0.078f), trouser);

                var knee = NewChild(hip, side < 0 ? "KneeL" : "KneeR");
                knee.localPosition = new Vector3(0f, -0.46f, 0f);
                rig.Part(knee, "Shin", CharacterMesh.LimbDown(0.44f, 0.078f, 0.088f, 0.058f), trouser);

                // Sole on the ground: the shin ends at world y 0, the boot is 0.13 tall, so its centre
                // sits 0.065 above the ankle. Eyeballing this is how figures end up shin-deep in snow.
                var boot = rig.Part(knee, "Boot", CharacterMesh.Blob(new Vector3(0.14f, 0.13f, 0.31f), 0.78f), gear);
                boot.localPosition = new Vector3(0f, -0.375f, 0.05f);

                if (side < 0) { rig._legL = hip; rig._kneeL = knee; }
                else { rig._legR = hip; rig._kneeR = knee; }
            }

            // --- torso ----------------------------------------------------------------
            rig._torso = NewChild(rig._hips, "Torso");
            rig.Part(rig._torso, "Parka",
                CharacterMesh.Torso(0.62f, 0.163f, 0.148f, 0.188f, 0.216f, depthRatio: 0.62f, lean: 0.03f),
                rig._coatMat);

            // The hem of the parka, breaking the join between coat and trousers.
            var hem = rig.Part(rig._torso, "Hem", CharacterMesh.Ruff(0.163f, 0.178f, 0.10f, 14, variant + 5, 0.10f, 0.62f), rig._coatMat);
            hem.localPosition = new Vector3(0f, 0.03f, 0f);

            // Chest tape band — the bit a teammate's torch catches.
            var band = rig.Part(rig._torso, "TapeBand", CharacterMesh.Ruff(0.192f, 0.192f, 0.045f, 14, variant, 0f, 0.63f), tape);
            band.localPosition = new Vector3(0f, 0.47f, 0f);

            var pack = rig.Part(rig._torso, "Pack", CharacterMesh.Blob(new Vector3(0.30f, 0.44f, 0.20f), 0.9f), gear);
            pack.localPosition = new Vector3(0f, 0.40f, -0.19f);
            var packTape = rig.Part(rig._torso, "PackTape", CharacterMesh.Ruff(0.10f, 0.10f, 0.035f, 10, variant + 1, 0f, 1f), tape);
            packTape.localPosition = new Vector3(0f, 0.52f, -0.29f);

            // --- head + hood -----------------------------------------------------------
            rig.Head = NewChild(rig._torso, "Head");
            rig.Head.localPosition = new Vector3(0f, 0.64f, 0.01f);
            rig.Part(rig.Head, "Face", CharacterMesh.Head(0.25f, 0.104f, 1.02f, muzzle: 0.018f, brow: 0.012f), skin);
            // The hood is a second, larger shell sitting BACK off the skull, so the face stays in a
            // recess. That recess is the shape — a parka hood at 40 m is a head with no neck and a
            // dark hole in the front, and that reads as human from much farther away than a bare head.
            var hood = rig.Part(rig.Head, "Hood", CharacterMesh.Head(0.29f, 0.128f, 1.06f, muzzle: 0f, brow: 0.03f), rig._coatMat);
            hood.localPosition = new Vector3(0f, -0.03f, -0.045f);
            var collar = rig.Part(rig.Head, "Collar", CharacterMesh.Ruff(0.115f, 0.155f, 0.09f, 14, variant + 2, 0.22f, 1f), gear);
            collar.localPosition = new Vector3(0f, 0.045f, -0.01f);

            // --- arms -------------------------------------------------------------------
            for (int side = -1; side <= 1; side += 2)
            {
                var shoulder = NewChild(rig._torso, side < 0 ? "ArmL" : "ArmR");
                shoulder.localPosition = new Vector3(side * 0.202f, 0.545f, 0f);
                rig.Part(shoulder, "UpperArm", CharacterMesh.LimbDown(0.30f, 0.080f, 0.084f, 0.062f), rig._coatMat);

                var elbow = NewChild(shoulder, side < 0 ? "ElbowL" : "ElbowR");
                elbow.localPosition = new Vector3(0f, -0.30f, 0f);
                rig.Part(elbow, "Forearm", CharacterMesh.LimbDown(0.28f, 0.062f, 0.066f, 0.050f), rig._coatMat);
                var cuff = rig.Part(elbow, "Cuff", CharacterMesh.Ruff(0.058f, 0.058f, 0.035f, 10, variant + 3 + side, 0f, 0.9f), tape);
                cuff.localPosition = new Vector3(0f, -0.24f, 0f);
                var mitt = rig.Part(elbow, "Mitt", CharacterMesh.Blob(new Vector3(0.10f, 0.15f, 0.09f), 0.8f), gear);
                mitt.localPosition = new Vector3(0f, -0.33f, 0.01f);

                if (side < 0) { rig._armL = shoulder; rig._elbowL = elbow; }
                else { rig._armR = shoulder; rig._elbowR = elbow; }
            }

            // The torch hand: posed forward once, here, and left alone by the gait.
            rig._armR.localRotation = Quaternion.Euler(-42f, 0f, -6f);
            rig._elbowR.localRotation = Quaternion.Euler(-38f, 0f, 0f);
            rig.TorchAnchor = NewChild(rig._elbowR, "TorchAnchor");
            rig.TorchAnchor.localPosition = new Vector3(0f, -0.33f, 0.06f);
            // Cancel the arm's pose so the beam points along the BODY's forward, not the forearm's.
            // A torch aimed down the axis of a bent arm shines at the ground two metres ahead, which
            // looks like the searcher is inspecting their own boots from every other player's view.
            // Order matters: the anchor's world rotation is torso * arm * elbow * anchor, so the
            // cancelling term is Inverse(arm * elbow) — Inverse(elbow * arm) is a different rotation
            // and points the beam somewhere plausible-looking but wrong.
            rig.TorchAnchor.localRotation = Quaternion.Inverse(rig._armR.localRotation * rig._elbowR.localRotation);

            rig.ApplyPose(0f, 0f, 0f);
            return rig;
        }

        // ======================================================================== Yeti

        /// <summary>
        /// The Yeti: a hominid, not a bear and not a big man. Everything here serves the outline,
        /// because the outline is what a searcher gets — one shape at the edge of a torch beam, for
        /// about a second.
        ///
        /// The four things doing the work, in order of how much they matter:
        /// - **The hunch.** The torso leans forward and the head sits FORWARD of the hips rather than
        ///   above them. This is the single cue that separates an animal from a tall person.
        /// - **Arm length.** The hands hang past the knees. Nothing else says "not human" so fast.
        /// - **No neck.** The skull is set down between two trapezius humps, so the head reads as part
        ///   of the shoulder mass.
        /// - **A ragged edge.** Mane, forearm and haunch fringes chew up the silhouette so it never
        ///   resolves into the smooth machined outline that gave the capsule away.
        /// </summary>
        public static CharacterRig BuildYeti(Transform parent, int variant)
        {
            var rig = new CharacterRig(true, cycleMetres: 2.30f, swingDeg: 26f, bobAmp: 0.075f)
            {
                Height = 2.58f, // crown: hips 1.28 + head pivot 0.84 + skull 0.46
            };
            rig.Root = NewChild(parent, "Body");

            // Matted fur, not painted plastic. Low smoothness so it stays a light SINK — the Yeti
            // reading as a silhouette that swallows the torch is half of what makes it scary.
            Color furCol = MeshUtil.Rgb(0x2a2018);
            Color maneCol = MeshUtil.Rgb(0x3a2e22);
            Color hideCol = MeshUtil.Rgb(0x17120f);
            Material fur = MeshUtil.Surface(furCol, 0.08f, ProcTex.FurNormal, 1.15f, 2.2f);
            Material mane = MeshUtil.Surface(maneCol, 0.10f, ProcTex.FurNormal, 1.35f, 1.6f);
            // Hide: the bare face, palms and soles. Slightly glossier than fur and much finer-grained,
            // so the face catches a highlight the coat never does — which is what makes you find the
            // eyes.
            Material hide = MeshUtil.Surface(hideCol, 0.26f, ProcTex.RockNormal, 0.7f, 7f);

            rig.Track(fur, furCol);
            rig.Track(mane, maneCol);
            rig.Track(hide, hideCol);

            // --- hips + legs ------------------------------------------------------------
            rig._hipHeight = 1.28f;
            rig._hips = NewChild(rig.Root, "Hips");
            rig._hips.localPosition = new Vector3(0f, rig._hipHeight, 0f);

            for (int side = -1; side <= 1; side += 2)
            {
                var hip = NewChild(rig._hips, side < 0 ? "LegL" : "LegR");
                hip.localPosition = new Vector3(side * 0.235f, -0.04f, 0f);
                rig.Part(hip, "Thigh", CharacterMesh.LimbDown(0.66f, 0.215f, 0.235f, 0.170f, 0.92f), fur);

                var knee = NewChild(hip, side < 0 ? "KneeL" : "KneeR");
                knee.localPosition = new Vector3(0f, -0.66f, 0f);
                rig.Part(knee, "Shin", CharacterMesh.LimbDown(0.54f, 0.170f, 0.190f, 0.125f, 0.92f), fur);

                // Shin ends at world y 0.04; the foot is 0.17 tall, so its centre goes 0.045 above that
                // to put the sole on the ground. (A yeti print is the hunters' win condition — the feet
                // had better be touching the snow.)
                var foot = rig.Part(knee, "Foot", CharacterMesh.Blob(new Vector3(0.28f, 0.17f, 0.46f), 0.7f), hide);
                foot.localPosition = new Vector3(0f, -0.495f, 0.09f);
                // Haunch fringe — the fur that hangs over the knee.
                var shaggy = rig.Part(hip, "Haunch", CharacterMesh.Ruff(0.23f, 0.27f, 0.30f, 12, variant + 40 + side, 0.36f), mane);
                shaggy.localPosition = new Vector3(0f, -0.30f, 0f);

                if (side < 0) { rig._legL = hip; rig._kneeL = knee; }
                else { rig._legR = hip; rig._kneeR = knee; }
            }

            // --- torso -------------------------------------------------------------------
            rig._torso = NewChild(rig._hips, "Torso");
            rig.Part(rig._torso, "Chest",
                CharacterMesh.Torso(0.92f, 0.34f, 0.37f, 0.47f, 0.53f, depthRatio: 0.78f, lean: 0.17f),
                fur);

            // Trapezius humps, straddling the shoulder yoke so they crest above it. The head sits in
            // the dip BETWEEN them — set them any narrower and they vanish inside the chest, which is
            // the whole reason the yoke's own radius (~0.38 at the top ring) is worth knowing here.
            for (int side = -1; side <= 1; side += 2)
            {
                var hump = rig.Part(rig._torso, "Hump", CharacterMesh.LimbUp(0.24f, 0.20f, 0.22f, 0.10f, 1.0f), fur);
                hump.localPosition = new Vector3(side * 0.34f, 0.78f, 0.05f);
            }

            var maneRuff = rig.Part(rig._torso, "Mane", CharacterMesh.Ruff(0.50f, 0.64f, 0.46f, 16, variant + 11, 0.34f), mane);
            maneRuff.localPosition = new Vector3(0f, 0.86f, 0.05f);
            var backRuff = rig.Part(rig._torso, "Ridge", CharacterMesh.Ruff(0.40f, 0.48f, 0.34f, 14, variant + 12, 0.40f), mane);
            backRuff.localPosition = new Vector3(0f, 0.42f, -0.06f);

            // --- head ----------------------------------------------------------------------
            rig.Head = NewChild(rig._torso, "Head");
            rig.Head.localPosition = new Vector3(0f, 0.84f, 0.20f); // forward of the spine: the hunch
            rig.Part(rig.Head, "Skull", CharacterMesh.Head(0.46f, 0.205f, 1.12f, muzzle: 0.11f, brow: 0.055f), fur);
            // The face mask: bare hide over the muzzle and brow, so the front of the head is a
            // different material from the coat and the eyes have something to sit in.
            var face = rig.Part(rig.Head, "FaceMask", CharacterMesh.Head(0.26f, 0.135f, 1.05f, muzzle: 0.10f, brow: 0.03f), hide);
            face.localPosition = new Vector3(0f, 0.04f, 0.075f);

            // Eyeshine is OWNED but deliberately NOT tinted: the eyes are the one part that must stay
            // lit when the body goes blue with freeze or black with a drag, because they are the only
            // thing that tells a searcher which way the Yeti is facing.
            Material eyeMat = MeshUtil.Emissive(Color.black, MeshUtil.Rgb(0xffcc55), 3.5f);
            rig.Own(eyeMat);
            foreach (float sx in new[] { -0.075f, 0.075f })
            {
                var eye = rig.Part(rig.Head, "Eye", CharacterMesh.Blob(new Vector3(0.062f, 0.045f, 0.05f), 0.9f), eyeMat);
                eye.localPosition = new Vector3(sx, 0.255f, 0.175f);
            }

            // --- arms --------------------------------------------------------------------------
            for (int side = -1; side <= 1; side += 2)
            {
                var shoulder = NewChild(rig._torso, side < 0 ? "ArmL" : "ArmR");
                shoulder.localPosition = new Vector3(side * 0.50f, 0.72f, 0.03f);
                rig.Part(shoulder, "UpperArm", CharacterMesh.LimbDown(0.72f, 0.20f, 0.215f, 0.155f, 0.94f), fur);

                var elbow = NewChild(shoulder, side < 0 ? "ElbowL" : "ElbowR");
                elbow.localPosition = new Vector3(0f, -0.72f, 0f);
                rig.Part(elbow, "Forearm", CharacterMesh.LimbDown(0.68f, 0.16f, 0.195f, 0.135f, 0.94f), fur);
                var cuff = rig.Part(elbow, "Cuff", CharacterMesh.Ruff(0.18f, 0.26f, 0.30f, 12, variant + 20 + side, 0.38f), mane);
                cuff.localPosition = new Vector3(0f, -0.38f, 0f);
                var hand = rig.Part(elbow, "Hand", CharacterMesh.Blob(new Vector3(0.24f, 0.20f, 0.32f), 0.8f), hide);
                hand.localPosition = new Vector3(0f, -0.76f, 0.04f);

                if (side < 0) { rig._armL = shoulder; rig._elbowL = elbow; }
                else { rig._armR = shoulder; rig._elbowR = elbow; }
            }

            rig.ApplyPose(0f, 0f, 0f);
            return rig;
        }

        // ================================================================ animation

        /// <summary>
        /// Advance the gait.
        ///
        /// <paramref name="speed"/> is planar ground speed in m/s — for the local player that is the
        /// sim's own movement, for a remote it is the interpolated transform delta. Either way the
        /// stride phase advances by DISTANCE, not by time, which is the whole trick: feet that are
        /// driven by the clock skate whenever the speed changes, and feet driven by the ground do not.
        /// </summary>
        public void Animate(float speed, bool crouched, bool limp, float dt)
        {
            if (Root == null) return;
            dt = Mathf.Clamp(dt, 0f, 0.1f); // a hitch must not fling the limbs through a whole cycle

            // Smooth the speed a little: the raw remote delta is noisy at snapshot boundaries and an
            // un-smoothed stride reads as a stutter in the legs.
            _speedSmooth = Mathf.Lerp(_speedSmooth, speed, 1f - Mathf.Exp(-12f * dt));
            _crouch01 = Mathf.MoveTowards(_crouch01, crouched ? 1f : 0f, dt * 5f);
            _limp01 = Mathf.MoveTowards(_limp01, limp ? 1f : 0f, dt * 3f);

            _phase += (_speedSmooth / _cycleMetres) * Mathf.PI * 2f * dt;
            if (_phase > Mathf.PI * 2f) _phase -= Mathf.PI * 2f;

            ApplyPose(_speedSmooth, _crouch01, _limp01);
        }

        private void ApplyPose(float speed, float crouch01, float limp01)
        {
            // How much of the walk to express. Fades in over the first metre-per-second so a figure
            // easing into motion doesn't snap into a full stride, and is killed entirely when the
            // body has gone limp.
            float gait = Mathf.Clamp01(speed / 1.2f) * (1f - limp01);
            float swing = _swingDeg * gait;
            float sinL = Mathf.Sin(_phase), sinR = Mathf.Sin(_phase + Mathf.PI);

            // --- legs. The knee only ever bends one way, and it bends on the BACK half of the swing
            // (the recovery), which is what stops the classic "walking on stilts" look.
            SetSwing(_legL, -sinL * swing);
            SetSwing(_legR, -sinR * swing);
            SetBend(_kneeL, Mathf.Max(0f, sinL) * swing * 1.5f + crouch01 * 46f);
            SetBend(_kneeR, Mathf.Max(0f, sinR) * swing * 1.5f + crouch01 * 46f);

            // --- arms, counter-swung against the legs. The searcher's right arm is holding the
            // torch and keeps its posed angle; the Yeti swings both, wide and low.
            float armSwing = swing * (_isYeti ? 0.85f : 0.7f);
            SetSwing(_armL, sinL * armSwing, -(_isYeti ? 12f : 5f));
            if (_isYeti) SetSwing(_armR, sinR * armSwing, _isYeti ? 12f : 5f);
            SetBend(_elbowL, (_isYeti ? 16f : 22f) + Mathf.Max(0f, -sinL) * armSwing * 0.8f);
            if (_isYeti) SetBend(_elbowR, 16f + Mathf.Max(0f, -sinR) * armSwing * 0.8f);

            // --- body. Two dips per cycle (one per footfall), a small counter-rotation in the torso,
            // and a lean into the direction of travel that grows with speed.
            float bob = -Mathf.Abs(Mathf.Cos(_phase)) * _bobAmp * gait;
            float lean = Mathf.Clamp01(speed / 8f) * (_isYeti ? 14f : 9f);
            float crouchDrop = crouch01 * (_isYeti ? 0.42f : 0.34f);

            if (_hips != null)
            {
                Vector3 p = _hips.localPosition;
                _hips.localPosition = new Vector3(p.x, _hipHeight + bob - crouchDrop, p.z);
                _hips.localRotation = Quaternion.Euler(0f, Mathf.Sin(_phase) * 3.5f * gait, 0f);
            }
            if (_torso != null)
                _torso.localRotation = Quaternion.Euler(
                    lean + crouch01 * 26f + limp01 * 12f,
                    -Mathf.Sin(_phase) * 5f * gait,
                    0f);
            // The head holds level against the torso's lean — people and animals stabilise their gaze,
            // and a head that pitches with the chest is the tell that a body is one rigid object.
            if (Head != null)
                Head.localRotation = Quaternion.Euler(-(lean + crouch01 * 26f) * 0.7f, 0f, 0f);
        }

        private static void SetSwing(Transform joint, float degX, float degZ = 0f)
        {
            if (joint != null) joint.localRotation = Quaternion.Euler(degX, 0f, degZ);
        }

        private static void SetBend(Transform joint, float degX)
        {
            if (joint != null) joint.localRotation = Quaternion.Euler(degX, 0f, 0f);
        }

        // ================================================================ appearance

        /// <summary>
        /// Blend every surface toward a status colour — frozen blue, incapacitated black, dazzled
        /// white. Each material moves from ITS OWN base colour, so a searcher's parka, trousers, skin
        /// and tape all read as the same figure going blue rather than as five things turning one
        /// shade of blue.
        /// </summary>
        public void SetStatusTint(Color tint, float amount)
        {
            foreach (var (mat, baseColor) in _tint)
            {
                if (mat == null) continue;
                mat.SetColor("_BaseColor", amount <= 0f ? baseColor : Color.Lerp(baseColor, tint, amount));
            }
        }

        /// <summary>The parka colour, which is dealt with the specialty and can change mid-session.</summary>
        public void SetCoatColor(Color coat)
        {
            if (_coatMat == null || _coatTintIndex < 0) return;
            if (_tint[_coatTintIndex].baseColor == coat) return;
            _tint[_coatTintIndex] = (_coatMat, coat);
            _coatMat.SetColor("_BaseColor", coat);
        }

        public void SetVisible(bool visible)
        {
            if (Root == null) return;
            foreach (var r in Root.GetComponentsInChildren<Renderer>(true)) r.enabled = visible;
        }

        /// <summary>Release the native meshes and materials. Nothing else owns them.</summary>
        public void Dispose()
        {
            _tint.Clear();
            foreach (var mat in _owned) if (mat != null) Object.Destroy(mat);
            _owned.Clear();
            foreach (var m in _meshes) if (m != null) Object.Destroy(m);
            _meshes.Clear();
            if (Root != null) Object.Destroy(Root.gameObject);
            Root = null;
        }

        // ================================================================ construction helpers

        private static Transform NewChild(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        /// <summary>Attach one mesh under a joint. Registers the mesh for cleanup.</summary>
        private Transform Part(Transform parent, string name, Mesh mesh, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            // Figures are small and always near the camera that cares about them; skipping light
            // probes and reflection probes keeps six of them off the per-renderer cost list.
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            _meshes.Add(mesh);
            return go.transform;
        }

        /// <summary>Register a material for status tinting (and for release).</summary>
        private void Track(Material mat, Color baseColor)
        {
            _tint.Add((mat, baseColor));
            Own(mat);
        }

        /// <summary>Register a material for release only — it keeps its own look through every status.</summary>
        private void Own(Material mat)
        {
            if (mat != null) _owned.Add(mat);
        }
    }
}
