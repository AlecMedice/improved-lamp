// The seam between gameplay and what a character LOOKS like.
//
// WHY THIS EXISTS. Every body in the game is generated at runtime from lathed profiles (Avatar.cs),
// which is what makes "clone the repo and press play" work with no asset files in it. That property
// is worth keeping, but it should not be a CEILING — the moment a real rigged model exists for a
// searcher, dropping it in should be a five-minute job, not a rewrite of the animation layer.
//
// So gameplay no longer talks to Avatar. It talks to ICharacterBody, and it hands over exactly one
// thing: an AvatarInput, filled from state the match has already replicated. Two implementations sit
// behind it — the procedural Avatar, and ModelBody, which drives an imported prefab's Animator from
// the same struct. Nothing in HPPlayer, GameManager or TitleActors knows or cares which one it got.
//
// ------------------------------------------------------------------------------------------------
// HOW TO IMPORT A CHARACTER MODEL (the whole procedure)
//
//   1. Import the FBX. In its Inspector -> Rig tab, set Animation Type = HUMANOID, hit Apply. This
//      is the step that matters: a humanoid avatar is what lets Unity name the bones for us, and it
//      is also what lets any humanoid animation clip (Mixamo's whole library, for instance) retarget
//      onto a model it was not authored for.
//   2. Drag the model into a scene, add an Animator Controller with the parameters listed on
//      CharacterAnchors below (all optional — a controller that only has "Speed" works fine, it just
//      gets less), and drag the result into Assets/Resources/Metoh/Characters/.
//   3. Name it exactly "Searcher" or "Yeti".
//   4. Press play. There is no step 5, and no code change anywhere.
//
// Anchors (where the torch rides, where breath comes from) are read off the humanoid rig for free.
// Add a CharacterAnchors component only when you want to override them, tint the model with the
// searcher's specialty colour, or rename the animator parameters.
//
// If the prefab is missing or fails to load, the procedural body is used. That is not a degraded
// fallback path bolted on for safety — it is the shipping default, and it stays that way until real
// models exist for every role.
using System.Collections.Generic;
using UnityEngine;

namespace Metoh.Game
{
    /// <summary>
    /// Everything gameplay is allowed to ask of a character's visual representation.
    ///
    /// Deliberately tiny, and deliberately one-directional: gameplay pushes state down, and nothing
    /// here ever reads back up. A body that could report something to the sim would be a body that
    /// could disagree with the server about it.
    /// </summary>
    public interface ICharacterBody
    {
        /// <summary>Breath vapour, the Yeti's eyeshine, the REC bead. Never null.</summary>
        Transform HeadAnchor { get; }

        /// <summary>Where a searcher's torch and beam ride — the hand, so the beam swings with the arm.</summary>
        Transform TorchAnchor { get; }

        /// <summary>Advance the pose. <paramref name="inp"/> is entirely derived from replicated state.</summary>
        void Tick(in AvatarInput inp, float dt);

        /// <summary>Kick the roar pose/animation, from the roar RPC every client already receives.</summary>
        void TriggerRoar();

        void SetVisible(bool on);

        /// <summary>
        /// Push the searcher's specialty colour. Called on a change rather than every frame, because
        /// the specialty can be dealt AFTER the figure was built.
        /// </summary>
        void SetTint(Color bodyColor);

        /// <summary>
        /// Push the searcher's specialty id ("photo", "sound", ...; "" for none or for the Yeti).
        /// Called on a change, for the same reason SetTint is: the specialty is dealt after the body
        /// exists. The procedural body answers by building that character's kit; an imported model is
        /// free to ignore it, or to show gear its own author authored.
        /// </summary>
        void SetSpecialty(string specialtyId);

        /// <summary>Release generated meshes and GameObjects. Meshes are native objects the GC never collects.</summary>
        void Dispose();
    }

    /// <summary>
    /// An imported prefab, driven from the same AvatarInput the procedural body takes.
    ///
    /// The translation is intentionally shallow: AvatarInput fields become Animator parameters and
    /// that is all. Anything cleverer — IK, layered poses, blend trees composed here in code — is the
    /// model author's job to express in the controller, where it can be authored and previewed,
    /// rather than something this file guesses at on their behalf.
    /// </summary>
    public class ModelBody : ICharacterBody
    {
        private GameObject _instance;
        private Animator _animator;
        private CharacterAnchors _anchors;
        private Renderer[] _renderers;
        private Renderer[] _tintTargets;
        private MaterialPropertyBlock _mpb;

        // Only parameters the controller actually declares get driven. A controller downloaded with a
        // model will not have "Dazzled" or "Carrying", and SetBool on a missing parameter logs a
        // warning EVERY FRAME, which buries the console the first time anyone imports anything.
        private readonly HashSet<string> _params = new HashSet<string>();

        public Transform HeadAnchor { get; private set; }
        public Transform TorchAnchor { get; private set; }

        /// <summary>Returns null when the prefab has no usable content, so the caller can fall back.</summary>
        public static ModelBody TryBuild(GameObject prefab, Transform parent)
        {
            if (prefab == null) return null;

            var body = new ModelBody();
            body._instance = Object.Instantiate(prefab, parent, false);
            body._instance.transform.localPosition = Vector3.zero;
            body._instance.transform.localRotation = Quaternion.identity;

            body._renderers = body._instance.GetComponentsInChildren<Renderer>(true);
            if (body._renderers.Length == 0)
            {
                Debug.LogWarning($"[CharacterBody] '{prefab.name}' has no renderers — using the procedural body.");
                Object.Destroy(body._instance);
                return null;
            }

            body._animator = body._instance.GetComponentInChildren<Animator>();
            body._anchors = body._instance.GetComponentInChildren<CharacterAnchors>();

            if (body._animator != null && body._animator.runtimeAnimatorController != null)
                foreach (var p in body._animator.parameters) body._params.Add(p.name);

            body.ResolveAnchors();
            body.ApplyFit();

            // Off-screen bodies stop evaluating their state machine. With five searchers plus a Yeti
            // this is most of the animation cost, and the only thing riding on their transforms while
            // invisible is breath vapour nobody is looking at.
            if (body._animator != null)
                body._animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;

            body._tintTargets = body._anchors != null && body._anchors.TintTargets != null &&
                                body._anchors.TintTargets.Length > 0
                ? body._anchors.TintTargets
                : body._renderers;
            body._mpb = new MaterialPropertyBlock();

            return body;
        }

        /// <summary>
        /// Explicit anchors win; otherwise take them off the humanoid rig; otherwise fall back to the
        /// root so nothing downstream ever has to null-check an anchor.
        /// </summary>
        private void ResolveAnchors()
        {
            Transform root = _instance.transform;
            bool humanoid = _animator != null && _animator.isHuman;

            HeadAnchor = _anchors != null && _anchors.Head != null ? _anchors.Head
                       : humanoid ? _animator.GetBoneTransform(HumanBodyBones.Head)
                       : null;
            TorchAnchor = _anchors != null && _anchors.TorchHand != null ? _anchors.TorchHand
                        : humanoid ? _animator.GetBoneTransform(HumanBodyBones.RightHand)
                        : null;

            if (HeadAnchor == null)
            {
                HeadAnchor = root;
                Debug.LogWarning($"[CharacterBody] '{_instance.name}': no head anchor (not a humanoid rig and " +
                                 "no CharacterAnchors.Head) — breath and eyeshine will sit at the feet.");
            }
            if (TorchAnchor == null) TorchAnchor = HeadAnchor;
        }

        /// <summary>
        /// Rescale to the declared height. Worth having because the single most common import problem
        /// is an FBX authored in centimetres arriving 100x too big, and the symptom — a wall of parka
        /// filling the screen — does not obviously read as a units problem to anyone who has not hit
        /// it before. The measured height is logged either way so there is a number to type in.
        /// </summary>
        private void ApplyFit()
        {
            var bounds = _renderers[0].bounds;
            for (int i = 1; i < _renderers.Length; i++) bounds.Encapsulate(_renderers[i].bounds);
            float measured = bounds.size.y;

            float want = _anchors != null ? _anchors.HeightMeters : 0f;
            if (want > 0.01f && measured > 1e-3f)
            {
                float k = want / measured;
                _instance.transform.localScale *= k;
                Debug.Log($"[CharacterBody] '{_instance.name}' measured {measured:0.00} m, scaled x{k:0.000} to {want:0.00} m.");
            }
            else
            {
                Debug.Log($"[CharacterBody] '{_instance.name}' imported at {measured:0.00} m " +
                          "(set CharacterAnchors.HeightMeters to rescale).");
            }
        }

        public void Tick(in AvatarInput inp, float dt)
        {
            if (_animator == null || _params.Count == 0) return;
            var a = _anchors;

            SetFloat(a != null ? a.SpeedParam : "Speed", inp.Speed);
            SetFloat(a != null ? a.TurnParam : "TurnRate", inp.YawRate);
            SetBool(a != null ? a.SprintParam : "Sprinting", inp.Sprinting);
            SetBool(a != null ? a.CrouchParam : "Crouched", inp.Crouched);
            SetBool(a != null ? a.FilmingParam : "Filming", inp.Filming);
            SetBool(a != null ? a.CarryingParam : "Carrying", inp.Carrying);
            SetBool(a != null ? a.FrozenParam : "Frozen", inp.Status == HPPlayer.StatusFrozen);
            SetBool(a != null ? a.DownedParam : "Downed",
                    inp.Status == HPPlayer.StatusIncap || inp.BeingCarried);
        }

        public void TriggerRoar()
        {
            string t = _anchors != null ? _anchors.RoarTrigger : "Roar";
            if (_animator != null && !string.IsNullOrEmpty(t) && _params.Contains(t))
                _animator.SetTrigger(t);
        }

        private void SetFloat(string name, float v)
        {
            if (!string.IsNullOrEmpty(name) && _params.Contains(name)) _animator.SetFloat(name, v);
        }

        private void SetBool(string name, bool v)
        {
            if (!string.IsNullOrEmpty(name) && _params.Contains(name)) _animator.SetBool(name, v);
        }

        public void SetVisible(bool on)
        {
            for (int i = 0; i < _renderers.Length; i++)
                if (_renderers[i] != null) _renderers[i].enabled = on;
        }

        /// <summary>
        /// Tint through a MaterialPropertyBlock, never by touching the material.
        ///
        /// An imported model's material is a project ASSET. Writing to `renderer.material` in play
        /// mode clones it (a leak per player, per rebuild); writing to `sharedMaterial` edits the
        /// asset itself and the change survives leaving play mode — so one match would permanently
        /// repaint every searcher in the project the colour of whoever was dealt it last.
        /// </summary>
        public void SetTint(Color bodyColor)
        {
            if (_tintTargets == null) return;
            for (int i = 0; i < _tintTargets.Length; i++)
            {
                var r = _tintTargets[i];
                if (r == null) continue;
                r.GetPropertyBlock(_mpb);
                _mpb.SetColor("_BaseColor", bodyColor); // URP Lit
                _mpb.SetColor("_Color", bodyColor);     // built-in / older shaders
                r.SetPropertyBlock(_mpb);
            }
        }

        /// <summary>
        /// No-op, deliberately. The procedural body answers this by generating a kit; an imported
        /// model already carries whatever gear its author modelled, and bolting lathed props onto a
        /// rigged mesh would put them in the wrong place on a rig this code has never seen. If a
        /// model author does want per-specialty gear, the hook is CharacterAnchors — parent the props
        /// under named child objects and toggle them here.
        /// </summary>
        public void SetSpecialty(string specialtyId) { }

        /// <summary>
        /// Nothing here was generated, so nothing here has to be released by hand — the meshes and
        /// materials belong to the imported asset and outlive the instance.
        /// </summary>
        public void Dispose()
        {
            if (_instance != null) Object.Destroy(_instance);
            _instance = null;
            _animator = null;
            _renderers = null;
            _tintTargets = null;
        }
    }

    /// <summary>
    /// Picks the implementation. The ONLY place in the project that decides whether a body is
    /// imported or generated — every other file holds an ICharacterBody and never asks.
    /// </summary>
    public static class CharacterFactory
    {
        /// <summary>
        /// Where an imported prefab goes. Resources rather than Addressables on purpose: dropping a
        /// prefab into a folder is the entire install step, no manifest to register it in and no
        /// build-time group to configure, which is what keeps "clone it and it runs" true whether or
        /// not any models exist.
        /// </summary>
        public const string ResourcePath = "Metoh/Characters/";

        // Resources.Load hits disk. Cached because BuildVisuals runs on every role change and a miss
        // (the shipping case — no models yet) is just as worth not repeating as a hit.
        private static readonly Dictionary<string, GameObject> _cache = new Dictionary<string, GameObject>();

        private static GameObject Prefab(string name)
        {
            if (_cache.TryGetValue(name, out var p)) return p;
            p = Resources.Load<GameObject>(ResourcePath + name);
            _cache[name] = p;
            if (p != null) Debug.Log($"[CharacterFactory] using imported model '{ResourcePath}{name}'.");
            return p;
        }

        /// <summary>
        /// Build a searcher. <paramref name="cloth"/>/<paramref name="gear"/> are used only by the
        /// procedural body; an imported model brings its own materials and takes the specialty colour
        /// through <see cref="ICharacterBody.SetTint"/> instead.
        /// </summary>
        public static ICharacterBody BuildSearcher(Transform parent, Material cloth, Material gear, int variant)
        {
            return ModelBody.TryBuild(Prefab("Searcher"), parent)
                   ?? (ICharacterBody)Avatar.BuildSearcher(parent, cloth, gear, variant);
        }

        /// <summary>
        /// Build the Yeti. <paramref name="hide"/> is bare skin — muzzle, hands, feet — and it is not
        /// optional decoration: a white animal rendered entirely in white fur has no dark anchor
        /// anywhere in its silhouette against snow. Procedural body only, like the searcher's
        /// materials; an imported model brings its own.
        /// </summary>
        public static ICharacterBody BuildYeti(Transform parent, Material fur, Material hide, Material eye, int variant)
        {
            return ModelBody.TryBuild(Prefab("Yeti"), parent)
                   ?? (ICharacterBody)Avatar.BuildYeti(parent, fur, hide, eye, variant);
        }

        /// <summary>Editor convenience: forget the lookup so a newly added prefab is seen without a restart.</summary>
        public static void ClearCache() => _cache.Clear();
    }
}
