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
        [ChineseLabel("生成触发器(盒子)")]
        public BoxCollider[] SpawnBoxes;

        [ChineseHeader("落点")]
        [ChineseLabel("地面射线层")]
        public LayerMask GroundRaycastLayers = ~0;

        [ChineseLabel("地面射线最大距离")]
        [Min(0.01f), DefaultValue(200f)]
        public float GroundRaycastMaxDistance = 200f;

        [ChineseLabel("生成高度")]
        [Min(0f), DefaultValue(3f)]
        public float SpawnHeightAboveGround = 3f;

        [ChineseHeader("碰撞")]
        [ChineseLabel("忽略与树的碰撞")]
        [DefaultValue(true)]
        public bool IgnoreCollisionWithTree = true;

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

            if (carryable.LastThrowTime < 0f)
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

            if (TrySpawn(playerPos))
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
                if (carryable.LastThrowTime >= 0f)
                {
                    return carryable.LastThrowerPositionWorld;
                }
            }

            return treePos;
        }

        private bool TrySpawn(Vector3 playerPos)
        {
            var box = FindNearestSpawnBox(playerPos);
            if (box == null)
            {
                return false;
            }

            Vector3 spawnPos;
            if (!TryGetSpawnPosOnGround(box, out spawnPos))
            {
                spawnPos = RandomPointInBox(box);
            }

            var fruit = Instantiate(FruitPrefab, spawnPos, Quaternion.identity);
            if (fruit != null)
            {
                if (IgnoreCollisionWithTree)
                {
                    ApplyIgnoreTreeCollisions(fruit);
                }

                var rb = fruit.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.isKinematic = false;
                    rb.useGravity = true;
                }

                var spawnedCarryable = fruit.GetComponentInChildren<SlimeCarryableObject>();
                if (spawnedCarryable != null)
                {
                    spawnedCarryable.ArmImpactWindow(0.1f);
                }
            }

            if (DropVfxPrefab != null)
            {
                Instantiate(DropVfxPrefab, spawnPos, Quaternion.identity);
            }

            DropFeedbacks?.PlayFeedbacks(spawnPos);

            return true;
        }

        private void ApplyIgnoreTreeCollisions(GameObject fruit)
        {
            if (fruit == null)
            {
                return;
            }

            var treeCols = GetComponentsInChildren<Collider>(true);
            if (treeCols == null || treeCols.Length == 0)
            {
                return;
            }

            var fruitCols = fruit.GetComponentsInChildren<Collider>(true);
            if (fruitCols == null || fruitCols.Length == 0)
            {
                return;
            }

            for (int i = 0; i < fruitCols.Length; i++)
            {
                var fc = fruitCols[i];
                if (fc == null)
                {
                    continue;
                }

                for (int j = 0; j < treeCols.Length; j++)
                {
                    var tc = treeCols[j];
                    if (tc == null)
                    {
                        continue;
                    }

                    Physics.IgnoreCollision(fc, tc, true);
                }
            }
        }

        private bool TryGetSpawnPosOnGround(BoxCollider box, out Vector3 spawnPos)
        {
            spawnPos = default;

            float height = Mathf.Max(0f, SpawnHeightAboveGround);
            float rayDist = Mathf.Max(0.01f, GroundRaycastMaxDistance);
            int mask = GroundRaycastLayers.value != 0 ? GroundRaycastLayers.value : ~0;

            var treeCols = GetComponentsInChildren<Collider>(true);

            Vector3 half = box.size * 0.5f;
            Vector3 local = box.center + new Vector3(
                Random.Range(-half.x, half.x),
                0f,
                Random.Range(-half.z, half.z)
            );
            Vector3 world = box.transform.TransformPoint(local);

            Vector3 downOrigin = world + Vector3.up * rayDist;

            RaycastHit[] hits = Physics.RaycastAll(downOrigin, Vector3.down, rayDist * 2f, mask, QueryTriggerInteraction.Ignore);
            if (hits != null && hits.Length > 0)
            {
                bool found = false;
                RaycastHit bestHit = default;
                float bestDist = float.PositiveInfinity;

                for (int i = 0; i < hits.Length; i++)
                {
                    var h = hits[i];
                    var hc = h.collider;
                    if (hc == null)
                    {
                        continue;
                    }

                    bool isSelf = false;
                    if (treeCols != null)
                    {
                        for (int j = 0; j < treeCols.Length; j++)
                        {
                            if (treeCols[j] == hc)
                            {
                                isSelf = true;
                                break;
                            }
                        }
                    }

                    if (isSelf)
                    {
                        continue;
                    }

                    if (h.distance < bestDist)
                    {
                        bestDist = h.distance;
                        bestHit = h;
                        found = true;
                    }
                }

                if (found)
                {
                    spawnPos = bestHit.point + Vector3.up * height;
                    return true;
                }
            }

            return false;
        }

        private BoxCollider FindNearestSpawnBox(Vector3 playerPos)
        {
            if (SpawnBoxes == null || SpawnBoxes.Length == 0)
            {
                return null;
            }

            BoxCollider best = null;
            float bestSqr = float.PositiveInfinity;

            for (int i = 0; i < SpawnBoxes.Length; i++)
            {
                var box = SpawnBoxes[i];
                if (box == null)
                {
                    continue;
                }
                if (!box.enabled || !box.gameObject.activeInHierarchy)
                {
                    continue;
                }

                Vector3 p = box.ClosestPoint(playerPos);
                float sqr = (playerPos - p).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = box;
                }
            }

            return best;
        }

        private static Vector3 RandomPointInBox(BoxCollider box)
        {
            Vector3 half = box.size * 0.5f;
            Vector3 local = box.center + new Vector3(
                Random.Range(-half.x, half.x),
                Random.Range(-half.y, half.y),
                Random.Range(-half.z, half.z)
            );
            return box.transform.TransformPoint(local);
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
            if (SpawnBoxes == null || SpawnBoxes.Length == 0)
            {
                return;
            }

            float alpha = selected ? 0.95f : 0.35f;
            Color oldColor = Gizmos.color;
            Matrix4x4 oldMatrix = Gizmos.matrix;

            Gizmos.color = new Color(1f, 0.9f, 0.2f, alpha);

            for (int i = 0; i < SpawnBoxes.Length; i++)
            {
                var box = SpawnBoxes[i];
                if (box == null)
                {
                    continue;
                }
                if (!box.enabled || !box.gameObject.activeInHierarchy)
                {
                    continue;
                }

                Gizmos.matrix = box.transform.localToWorldMatrix;
                Gizmos.DrawWireCube(box.center, box.size);
            }

            Gizmos.matrix = oldMatrix;
            Gizmos.color = oldColor;
        }
    }
}
