using MoreMountains.Feedbacks;
using Revive.Slime;
using UnityEngine;

namespace Revive.Environment
{
    [DisallowMultipleComponent]
    public sealed class TreeFruitDropOnHit : MonoBehaviour
    {
        [ChineseHeader("掉落")]
        [ChineseLabel("果实预制体")]
        public GameObject FruitPrefab;

        [ChineseLabel("冷却(秒)")]
        [Min(0f), DefaultValue(1f)]
        public float CooldownSeconds = 1f;

        [ChineseHeader("生成范围")]
        [ChineseLabel("圆环内半径(树中心)")]
        [Min(0f), DefaultValue(1f)]
        public float MinRingRadiusFromTree = 1f;

        [ChineseLabel("圆环外半径(树中心)")]
        [Min(0f), DefaultValue(4f)]
        public float MaxRingRadiusFromTree = 4f;

        [ChineseLabel("扇区角度(度)")]
        [Range(0f, 360f), DefaultValue(60f)]
        public float SectorAngleDegrees = 60f;

        [ChineseHeader("落点")]
        [ChineseLabel("生成高度")]
        [Min(0f), DefaultValue(3f)]
        public float SpawnHeightAboveGround = 3f;

        [ChineseHeader("反馈")]
        [ChineseLabel("掉落特效预制体(可选)")]
        public GameObject DropVfxPrefab;

        [ChineseLabel("掉落反馈(MMFeedbacks)")]
        public MMFeedbacks DropFeedbacks;

        private float _nextAllowedTime;

        private void OnTriggerEnter(Collider other)
        {
            if (other == null)
            {
                return;
            }

            TryHandleCollision(other);
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (collision == null)
            {
                return;
            }

            var otherCol = collision.collider;
            if (otherCol == null)
            {
                return;
            }

            TryHandleCollision(otherCol);
        }

        private void TryHandleCollision(Collider otherCol)
        {
            if (Time.time < _nextAllowedTime)
            {
                return;
            }

            var carryable = otherCol.GetComponentInParent<SlimeCarryableObject>();
            if (carryable == null)
            {
                return;
            }

            if (carryable.IsHeld)
            {
                return;
            }

            if (carryable.LastThrowTime <= 0f)
            {
                return;
            }

            if (carryable.Type != SlimeCarryableObject.CarryableType.Stone)
            {
                return;
            }

            if (FruitPrefab == null)
            {
                return;
            }

            Vector3 treePos = transform.position;
            Vector3 playerPos = EstimatePlayerPosition(carryable, treePos);

            if (TrySpawn(playerPos, treePos, carryable))
            {
                _nextAllowedTime = Time.time + Mathf.Max(0f, CooldownSeconds);
            }
        }

        private Vector3 EstimatePlayerPosition(SlimeCarryableObject carryable, Vector3 treePos)
        {
            if (carryable != null)
            {
                if (carryable.LastThrowerTransform != null)
                {
                    return carryable.LastThrowerTransform.position;
                }
                if (carryable.LastThrowTime > 0f)
                {
                    return carryable.LastThrowerPositionWorld;
                }
            }

            return treePos;
        }

        private bool TrySpawn(Vector3 playerPos, Vector3 treePos, SlimeCarryableObject carryable)
        {
            float maxRadius = Mathf.Max(0f, MaxRingRadiusFromTree);
            float minRadius = Mathf.Clamp(MinRingRadiusFromTree, 0f, maxRadius);

            Vector3 forward = playerPos - treePos;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f)
            {
                if (carryable != null && carryable.Rigidbody != null)
                {
                    Vector3 v = carryable.Rigidbody.linearVelocity;
                    v.y = 0f;
                    if (v.sqrMagnitude > 1e-4f)
                    {
                        forward = -v;
                    }
                }
            }
            if (forward.sqrMagnitude < 1e-4f)
            {
                forward = transform.forward;
                forward.y = 0f;
            }
            if (forward.sqrMagnitude < 1e-4f)
            {
                forward = Vector3.forward;
            }
            forward.Normalize();

            float halfAngle = Mathf.Clamp(SectorAngleDegrees, 0f, 360f) * 0.5f;
            float yaw = Random.Range(-halfAngle, halfAngle);
            Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * forward;

            float radius = maxRadius > 1e-4f ? Random.Range(minRadius, maxRadius) : 0f;
            Vector3 basePos = treePos + dir * radius;

            float height = Mathf.Max(0f, SpawnHeightAboveGround);
            Vector3 spawnPos = new Vector3(basePos.x, treePos.y + height, basePos.z);

            var fruit = Instantiate(FruitPrefab, spawnPos, Quaternion.identity);
            if (fruit != null)
            {
                var rb = fruit.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.isKinematic = false;
                    rb.useGravity = true;
                }
            }

            if (DropVfxPrefab != null)
            {
                Instantiate(DropVfxPrefab, spawnPos, Quaternion.identity);
            }

            DropFeedbacks?.PlayFeedbacks(spawnPos);

            return true;
        }

        private void OnDrawGizmos()
        {
            DrawSpawnRangeGizmos(false);
        }

        private void OnDrawGizmosSelected()
        {
            DrawSpawnRangeGizmos(true);
        }

        private void DrawSpawnRangeGizmos(bool selected)
        {
            float maxRadius = Mathf.Max(0f, MaxRingRadiusFromTree);
            float minRadius = Mathf.Clamp(MinRingRadiusFromTree, 0f, maxRadius);

            Vector3 fwd = transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f)
            {
                fwd = Vector3.forward;
            }
            fwd.Normalize();

            float height = Mathf.Max(0f, SpawnHeightAboveGround);
            Vector3 treePos = transform.position;
            Vector3 center = new Vector3(treePos.x, treePos.y + height, treePos.z);

            float angle = Mathf.Clamp(SectorAngleDegrees, 0f, 360f);
            float halfAngle = angle * 0.5f;
            int steps = Mathf.Max(12, Mathf.CeilToInt(angle / 6f));
            if (angle <= 1e-3f)
            {
                steps = 1;
            }

            float alpha = selected ? 0.95f : 0.35f;
            Gizmos.color = new Color(1f, 0.9f, 0.2f, alpha);

            if (selected)
            {
                Gizmos.DrawSphere(center, 0.12f);
                Gizmos.DrawLine(center, center + fwd * Mathf.Max(0.2f, maxRadius));
            }

            Vector3 innerPrev = Vector3.zero;
            Vector3 outerPrev = Vector3.zero;

            for (int i = 0; i <= steps; i++)
            {
                float t = steps > 0 ? (float)i / steps : 0f;
                float yaw = Mathf.Lerp(-halfAngle, halfAngle, t);
                Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * fwd;

                Vector3 inner = center + dir * minRadius;
                Vector3 outer = center + dir * maxRadius;

                if (i > 0)
                {
                    Gizmos.DrawLine(innerPrev, inner);
                    Gizmos.DrawLine(outerPrev, outer);
                }

                if (i == 0 || i == steps)
                {
                    Gizmos.DrawLine(inner, outer);
                    if (selected)
                    {
                        Gizmos.DrawSphere(inner, 0.08f);
                        Gizmos.DrawSphere(outer, 0.08f);
                    }
                }

                innerPrev = inner;
                outerPrev = outer;
            }
        }
    }
}
