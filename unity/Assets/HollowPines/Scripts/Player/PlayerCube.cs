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
            Vector2 dir = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
            if (dir.sqrMagnitude > 0.0001f)
                MoveServerRpc(dir.normalized);
        }

        [ServerRpc]
        private void MoveServerRpc(Vector2 dir)
        {
            // Authoritative move on the host; NetworkTransform pushes the result to all clients.
            float dt = (float)base.TimeManager.TickDelta;
            transform.position += new Vector3(dir.x, 0f, dir.y) * (_speed * dt);
        }
    }
}
