// Optional metadata you attach to an imported character prefab. See CharacterBody.cs for the full
// import procedure — the short version is that a humanoid FBX with an Animator needs NONE of this,
// because a humanoid rig already names its own bones. Add the component to override an anchor, to
// tint part of the model with the searcher's specialty colour, or to fix the scale.
//
// This lives in its own file because Unity will only let you attach a MonoBehaviour through the
// Inspector when its class name matches the file name.
using UnityEngine;

namespace Metoh.Game
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Metoh/Character Anchors")]
    public class CharacterAnchors : MonoBehaviour
    {
        [Header("Anchors (leave empty to read them off the humanoid rig)")]
        [Tooltip("Breath vapour, the Yeti's eyeshine and the REC bead hang here. " +
                 "Defaults to the humanoid Head bone.")]
        public Transform Head;

        [Tooltip("The torch and its beam parent here, so the beam swings with the arm. " +
                 "Defaults to the humanoid RightHand bone.")]
        public Transform TorchHand;

        [Header("Fit")]
        [Tooltip("Metres from sole to crown. Non-zero rescales the model to match — use it when the " +
                 "FBX was authored in centimetres, which most of them are. A searcher should read 1.8, " +
                 "the Yeti about 2.6. Leave at 0 and the measured height is logged instead.")]
        public float HeightMeters;

        [Header("Tint")]
        [Tooltip("Renderers that take the searcher's specialty colour — normally just the parka. " +
                 "Leave empty and the WHOLE model is tinted, which is wrong for anything with a face, " +
                 "boots or a pack. Ignored for the Yeti.")]
        public Renderer[] TintTargets;

        [Header("Animator parameters (blank, or absent from the controller, = not driven)")]
        [Tooltip("Horizontal speed in m/s. The one parameter worth wiring first: drive a walk/run " +
                 "blend tree from it and the model is already better than the generated body.")]
        public string SpeedParam = "Speed";

        [Tooltip("Signed turn rate in rad/s — for lean and turn-in-place.")]
        public string TurnParam = "TurnRate";

        public string SprintParam = "Sprinting";    // bool
        public string CrouchParam = "Crouched";     // bool
        public string FilmingParam = "Filming";     // bool — camera up at the face
        public string FrozenParam = "Frozen";       // bool — caught by a roar, locked mid-stride
        public string DownedParam = "Downed";       // bool — incapacitated OR being hauled
        public string CarryingParam = "Carrying";   // bool — Yeti hauling a searcher
        public string RoarTrigger = "Roar";         // trigger
    }
}
