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

        [Header("拾取")]
        public bool PickupEnabled = true;
        public ThrowDirectionMode DirectionMode = ThrowDirectionMode.MouseXZ;

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
            if (collision == null)
                return;

            if (!PickupEnabled || IsHeld)
                return;

            var otherCol = collision.collider;
            var slot = otherCol != null ? otherCol.GetComponentInParent<SlimeCarrySlot>() : null;
            if (slot == null && collision.gameObject != null)
            {
                slot = collision.gameObject.GetComponentInParent<SlimeCarrySlot>();
            }
            slot?.TryPickup(this);
        }
    }
}

