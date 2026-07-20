// R1 vertical slice — the "cube that moves." Host-authoritative movement: the owning client samples
// input each tick and ships it to the server via a ServerRpc; the server applies the move to the
// transform, and a NetworkTransform component (add it in the prefab, see unity/README.md) replicates
// the authoritative position to everyone.
//
// This is deliberately the SIMPLEST correct pattern for proving the relay — NOT the final movement
// model. In R4 this gets replaced by the ported deterministic sim (HollowPines.Sim.Movement.StepPlayer)
// driven under FishNet's prediction (Replicate/Reconcile) for client-side prediction + reconciliation,
// matching the web build's shared-sim approach.
using FishNet.Object;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace HollowPines.Player
{
    [RequireComponent(typeof(NetworkObject))]
    public class PlayerCube : NetworkBehaviour
    {
        [SerializeField] private float _speed = 6f;

        public override void OnStartNetwork()
        {
            base.TimeManager.OnTick += OnTick;
        }

        public override void OnStopNetwork()
        {
            if (base.TimeManager != null)
                base.TimeManager.OnTick -= OnTick;
        }

        private void OnTick()
        {
            if (!base.IsOwner) return;
            Vector2 dir = ReadMoveInput();
            if (dir.sqrMagnitude > 0.0001f)
                MoveServerRpc(dir.normalized);
        }

        // Unity 6 templates default Active Input Handling to the NEW Input System only, where legacy
        // UnityEngine.Input throws — so read whichever backend is compiled in (WASD + arrows).
        private static Vector2 ReadMoveInput()
        {
#if ENABLE_INPUT_SYSTEM
            Keyboard kb = Keyboard.current;
            if (kb == null) return Vector2.zero;
            float x = (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f)
                    + (kb.rightArrowKey.isPressed ? 1f : 0f) - (kb.leftArrowKey.isPressed ? 1f : 0f);
            float y = (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f)
                    + (kb.upArrowKey.isPressed ? 1f : 0f) - (kb.downArrowKey.isPressed ? 1f : 0f);
            return new Vector2(Mathf.Clamp(x, -1f, 1f), Mathf.Clamp(y, -1f, 1f));
#else
            return new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
#endif
        }

        [ServerRpc]
        private void MoveServerRpc(Vector2 dir)
        {
            // Authoritative move on the host; NetworkTransform pushes the result to all clients.
            // Negated: the R1 scene's auto-created camera faces -Z, so screen-forward is -Z.
            float dt = (float)base.TimeManager.TickDelta;
            transform.position += new Vector3(-dir.x, 0f, -dir.y) * (_speed * dt);
        }
    }
}
