using System.Collections.Generic;
using MoreMountains.Feedbacks;
using MoreMountains.TopDownEngine;
using Revive.Slime;
using UnityEngine;

namespace Revive.Environment
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Collider))]
    [AddComponentMenu("Revive/Environment/Wind Field Zone")]
    public sealed class WindFieldZone : MonoBehaviour
    {
        [Revive.ChineseHeader("风向")]
        [Revive.ChineseLabel("方向(本地)")]
        public Vector3 LocalDirection = Vector3.forward;

        [Revive.ChineseHeader("阻碍")]
        [Revive.ChineseLabel("地面阻碍")]
        [Min(0f)]
        public float GroundDrag = 0.8f;

        [Revive.ChineseLabel("空中阻碍")]
        [Min(0f)]
        public float AirDrag = 2.0f;

        [Revive.ChineseHeader("推力")]
        [Revive.ChineseLabel("推力强度")]
        [Min(0f)]
        public float PushStrength = 0f;

        [Revive.ChineseHeader("过滤")]
        [Revive.ChineseLabel("影响层")]
        public LayerMask AffectsLayers = ~0;

        [Revive.ChineseHeader("反馈")]
        [Revive.ChineseLabel("进入风场反馈")]
        [SerializeField] private MMFeedbacks enterFeedbacks;

        [Revive.ChineseLabel("离开风场反馈")]
        [SerializeField] private MMFeedbacks exitFeedbacks;

        [Revive.ChineseLabel("风场持续反馈(Loop)")]
        [SerializeField] private MMFeedbacks loopFeedbacks;

        public Collider ZoneCollider { get; private set; }

        public Bounds WorldBounds => ZoneCollider != null ? ZoneCollider.bounds : default;

        private readonly HashSet<int> _activeTargetIds = new HashSet<int>();

        private void Awake()
        {
            ZoneCollider = GetComponent<Collider>();
        }

        private void OnEnable()
        {
            EnsureTrigger();
            WindFieldRegistry.Register(this);
        }

        private void OnDisable()
        {
            WindFieldRegistry.Unregister(this);

            _activeTargetIds.Clear();
            loopFeedbacks?.StopFeedbacks();
        }

        private void Reset()
        {
            ZoneCollider = GetComponent<Collider>();
            EnsureTrigger();
        }

        private void OnValidate()
        {
            if (AirDrag < GroundDrag)
                AirDrag = GroundDrag;

            if (LocalDirection.sqrMagnitude < 0.000001f)
                LocalDirection = Vector3.forward;

            if (ZoneCollider == null)
                ZoneCollider = GetComponent<Collider>();

            EnsureTrigger();
        }

        public Vector3 GetDirectionWorld()
        {
            Vector3 dir = transform.TransformDirection(LocalDirection);
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.000001f)
                dir = Vector3.forward;
            return dir.normalized;
        }

        public bool ContainsWorldPoint(Vector3 worldPos)
        {
            if (ZoneCollider == null)
                return false;

            if (!ZoneCollider.bounds.Contains(worldPos))
                return false;

            Vector3 closest = ZoneCollider.ClosestPoint(worldPos);
            return (closest - worldPos).sqrMagnitude < 0.000001f;
        }

        public bool AffectsLayer(int layer)
        {
            if (layer < 0 || layer > 31)
                return false;

            return (AffectsLayers.value & (1 << layer)) != 0;
        }

        public bool AffectsGameObject(GameObject go)
        {
            if (go == null)
                return false;

            int layer = go.layer;
            return AffectsLayer(layer);
        }

        public bool AffectsCarryable(SlimeCarryableObject carryable)
        {
            if (carryable == null)
                return false;

            if (carryable.Type == SlimeCarryableObject.CarryableType.Stone)
                return false;

            return AffectsGameObject(carryable.gameObject);
        }

        private void EnsureTrigger()
        {
            if (ZoneCollider != null && !ZoneCollider.isTrigger)
            {
                ZoneCollider.isTrigger = true;
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!TryGetFeedbackTargetId(other, out int id, out Vector3 pos))
                return;

            bool wasEmpty = _activeTargetIds.Count == 0;
            if (_activeTargetIds.Add(id))
            {
                MMFeedbacksHelper.Play(enterFeedbacks, pos);

                if (wasEmpty)
                {
                    MMFeedbacksHelper.Play(loopFeedbacks, pos);
                }
            }
        }

        private void OnTriggerExit(Collider other)
        {
            if (!TryGetFeedbackTargetId(other, out int id, out Vector3 pos))
                return;

            if (_activeTargetIds.Remove(id))
            {
                MMFeedbacksHelper.Play(exitFeedbacks, pos);

                if (_activeTargetIds.Count == 0)
                {
                    MMFeedbacksHelper.Stop(loopFeedbacks);
                }
            }
        }

        private bool TryGetFeedbackTargetId(Collider other, out int targetId, out Vector3 position)
        {
            targetId = 0;
            position = transform.position;

            if (other == null)
                return false;

            GameObject go = other.gameObject;
            if (go == null)
                return false;

            if (!AffectsLayer(go.layer))
                return false;

            var controller = other.GetComponentInParent<TopDownController3D>();
            var carryable = other.GetComponentInParent<SlimeCarryableObject>();
            if (controller == null && carryable == null)
                return false;

            if (carryable != null && !AffectsCarryable(carryable))
                return false;

            if (other.attachedRigidbody != null)
            {
                targetId = other.attachedRigidbody.GetInstanceID();
                position = other.attachedRigidbody.position;
                return true;
            }

            targetId = go.transform.root.gameObject.GetInstanceID();
            position = go.transform.root.position;
            return true;
        }

        private void OnDrawGizmosSelected()
        {
            if (ZoneCollider == null)
                ZoneCollider = GetComponent<Collider>();

            if (ZoneCollider == null)
                return;

            Bounds b = ZoneCollider.bounds;

            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.75f);
            Gizmos.DrawWireCube(b.center, b.size);

            Vector3 dir = GetDirectionWorld();
            float len = Mathf.Max(0.1f, Mathf.Max(b.extents.x, b.extents.z));
            Gizmos.DrawLine(b.center, b.center + dir * len);
        }
    }
}
