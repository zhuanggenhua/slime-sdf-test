using UnityEngine;

namespace Revive.Slime
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    public class SlimeCarryableObject : MonoBehaviour
    {
        public enum ThrowDirectionMode
        {
            MouseXZ = 0,
            CharacterForward = 1,
        }

        [Header("Pickup")]
        public bool PickupEnabled = true;

        [Header("Throw")]
        public ThrowDirectionMode DirectionMode = ThrowDirectionMode.MouseXZ;

        [Tooltip("Impulse strength applied when thrown (ForceMode.Impulse).")]
        [Min(0f)]
        public float ThrowImpulse = 10f;

        [Tooltip("Optional upward impulse added on throw.")]
        [Min(0f)]
        public float UpwardImpulse = 0f;

        public Rigidbody Rigidbody { get; private set; }
        public Collider[] Colliders { get; private set; }

        public bool IsHeld { get; internal set; }

        private void Awake()
        {
            Rigidbody = GetComponent<Rigidbody>();
            Colliders = GetComponentsInChildren<Collider>(true);

            if (Colliders == null || Colliders.Length == 0)
            {
                Debug.LogError("[SlimeCarryableObject] No Collider found on this object or its children.", this);
            }
        }

        private void OnEnable()
        {
            if (Colliders == null || Colliders.Length == 0)
                return;

            var index = SlimeWorldColliderIndex.GetOrCreate();
            index.RegisterDynamicColliders(Colliders);
        }

        private void OnDisable()
        {
            if (Colliders == null || Colliders.Length == 0)
                return;

            if (SlimeWorldColliderIndex.TryGetInstance(out var index))
            {
                index.UnregisterDynamicColliders(Colliders);
            }
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (!PickupEnabled || IsHeld)
                return;

            if (collision == null)
                return;

            var slot = collision.collider != null ? collision.collider.GetComponentInParent<SlimeCarrySlot>() : null;
            if (slot == null)
            {
                slot = collision.gameObject != null ? collision.gameObject.GetComponentInParent<SlimeCarrySlot>() : null;
            }

            slot?.TryPickup(this);
        }
    }
}

