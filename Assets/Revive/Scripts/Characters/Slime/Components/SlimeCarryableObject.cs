using MoreMountains.Feedbacks;
using UnityEngine;

namespace Revive.Slime
{
    [DisallowMultipleComponent] 
    [RequireComponent(typeof(SlimeCarryableWindFieldAffect))]
    public class SlimeCarryableObject : MonoBehaviour
    {
        public enum CarryableType
        {
            Apple = 0,
            Mushroom = 1,
            Stone = 2,
            Flower = 3,
        }

        public enum ThrowDirectionMode
        {
            MouseXZ = 0,
            CharacterForward = 1,
        }

        [Header("类型")]
        public CarryableType Type = CarryableType.Apple;

        [Header("拾取")]
        public bool PickupEnabled = true;
        public ThrowDirectionMode DirectionMode = ThrowDirectionMode.MouseXZ;

        [Header("投掷")]
        [Min(0.01f)]
        public float MaxThrowRange = 10f;

        [ChineseHeader("反馈")]
        [ChineseLabel("投掷后落地反馈")]
        [SerializeField] private MMFeedbacks thrownImpactGroundFeedbacks;

        [ChineseLabel("投掷后撞击反馈")]
        [SerializeField] private MMFeedbacks thrownImpactOtherFeedbacks;

        [ChineseLabel("投掷碰撞节流(秒)")]
        [SerializeField, Min(0f), DefaultValue(0.08f)]
        private float thrownImpactCooldownSeconds = 0.08f;

        [ChineseLabel("投掷碰撞最小相对速度")]
        [SerializeField, Min(0f), DefaultValue(1.5f)]
        private float thrownImpactMinRelativeSpeed = 1.5f;

        [ChineseLabel("落地法线阈值(Y)")]
        [SerializeField, Range(0f, 1f), DefaultValue(0.6f)]
        private float thrownImpactGroundNormalYThreshold = 0.6f;

        [ChineseLabel("投掷碰撞有效窗口(秒)")]
        [SerializeField, Min(0f), DefaultValue(6f)]
        private float thrownImpactWindowSeconds = 6f;

        public Rigidbody Rigidbody { get; private set; }
        public Collider[] Colliders { get; private set; }

        public bool IsHeld { get; internal set; }

        public Transform LastThrowerTransform { get; private set; }
        public Vector3 LastThrowerPositionWorld { get; private set; }
        public float LastThrowTime { get; private set; }

        private float _nextAllowedThrownImpactTime;

        private void Awake()
        {
            Rigidbody = GetComponentInChildren<Rigidbody>();
            Colliders = GetComponentsInChildren<Collider>(true);

            if (Colliders == null || Colliders.Length == 0)
            {
                Debug.LogError("[SlimeCarryableObject] No Collider found on this object or its children.", this);
            }
        }

        public void RecordThrower(Transform thrower)
        {
            LastThrowerTransform = thrower;
            LastThrowerPositionWorld = thrower != null ? thrower.position : Vector3.zero;
            LastThrowTime = Time.time;
            _nextAllowedThrownImpactTime = Time.time;
        }

        public void ArmImpactWindow(float startDelaySeconds = 0f)
        {
            LastThrowerTransform = null;
            LastThrowerPositionWorld = Vector3.zero;
            LastThrowTime = Time.time;
            _nextAllowedThrownImpactTime = Time.time + Mathf.Max(0f, startDelaySeconds);
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

            if (!IsHeld)
            {
                TryPlayThrownImpactFeedbacks(collision);
            }

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

        private void TryPlayThrownImpactFeedbacks(Collision collision)
        {
            if (collision == null)
                return;

            if (IsHeld)
                return;

            if (LastThrowTime <= 0f)
                return;

            float window = Mathf.Max(0f, thrownImpactWindowSeconds);
            if (window > 0f && Time.time > LastThrowTime + window)
                return;

            if (Time.time < _nextAllowedThrownImpactTime)
                return;

            float minSpeed = Mathf.Max(0f, thrownImpactMinRelativeSpeed);
            if (minSpeed > 0f && collision.relativeVelocity.sqrMagnitude < (minSpeed * minSpeed))
                return;

            float bestNy = float.NegativeInfinity;
            var contacts = collision.contacts;
            if (contacts != null && contacts.Length > 0)
            {
                for (int i = 0; i < contacts.Length; i++)
                {
                    float ny = contacts[i].normal.y;
                    if (ny > bestNy)
                        bestNy = ny;
                }
            }

            bool isGroundLike = bestNy >= Mathf.Clamp01(thrownImpactGroundNormalYThreshold);
            Vector3 pos = collision.contactCount > 0 ? collision.GetContact(0).point : transform.position;

            if (isGroundLike)
            {
                thrownImpactGroundFeedbacks?.PlayFeedbacks(pos);
            }
            else
            {
                thrownImpactOtherFeedbacks?.PlayFeedbacks(pos);
            }

            _nextAllowedThrownImpactTime = Time.time + Mathf.Max(0f, thrownImpactCooldownSeconds);
        }
    }
}

