using Revive.Environment;
using UnityEngine;

namespace Revive.Slime
{
    [DisallowMultipleComponent]
    public class SlimeCarryableWindFieldAffect : MonoBehaviour
    {
        private SlimeCarryableObject _carryable;
        private Rigidbody _rb;
        private Collider _col;

        private void Awake()
        {
            _carryable = GetComponent<SlimeCarryableObject>();
            _rb = _carryable != null ? _carryable.Rigidbody : null;
            _col = GetComponentInChildren<Collider>();
        }

        private void FixedUpdate()
        {
            if (_carryable == null || _rb == null)
                return;

            if (_carryable.IsHeld)
                return;

            if (_carryable.Type == SlimeCarryableObject.CarryableType.Stone)
                return;

            WindFieldRegistry.GetCombinedForCarryable(
                _rb.position,
                _carryable,
                out float groundDrag,
                out float airDrag,
                out Vector3 pushVector
            );

            bool grounded = IsGroundedApprox();
            float drag = grounded ? groundDrag : airDrag;

            if (drag > 0f)
            {
                float damp = 1f / (1f + drag * Time.fixedDeltaTime);
                _rb.linearVelocity = _rb.linearVelocity * damp;
            }

            if (pushVector.sqrMagnitude > 0.000001f)
            {
                _rb.AddForce(pushVector, ForceMode.Acceleration);
            }
        }

        private bool IsGroundedApprox()
        {
            Vector3 origin = _rb.position;
            float distance = 0.15f;

            if (_col != null)
            {
                distance = Mathf.Max(distance, _col.bounds.extents.y + 0.05f);
            }

            origin.y += 0.05f;

            return Physics.Raycast(origin, Vector3.down, distance, ~0, QueryTriggerInteraction.Ignore);
        }
    }
}
