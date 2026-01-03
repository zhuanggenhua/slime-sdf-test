using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using MoreMountains.TopDownEngine;
using MoreMountains.Feedbacks;

namespace Revive.Slime
{
    [DisallowMultipleComponent]
    public class SlimeCarrySlot : MonoBehaviour
    {
        [Header("绑定")]
        [Tooltip("放置被持有物体的世界坐标锚点；若可用则默认使用 Slime_PBF.trans。")]
        public Transform CenterAnchor;

        public bool PreferSlimePbfCentroid = true;
        public Vector3 HeldAnchorOffset = new Vector3(0f, 0f, 0f);

        [Header("持有")]
        [Min(0f)]
        public float PickupTransitionSeconds = 0.12f;

        [Min(0f)]
        public float StuckAutoDetachSeconds = 0.35f;

        [Min(0f)]
        public float StuckAutoDetachOutsideSlimeMarginWorld = 0.5f;

        [Header("投掷")]
        [Range(1f, 89f)]
        public float FixedThrowAngleDegrees = 45f;

        public LayerMask GroundRaycastLayers = ~0;

        [Min(0.01f)]
        public float GroundRaycastMaxDistance = 200f;

        [Tooltip("投掷后短暂忽略玩家与物体的碰撞，避免重叠抖动。")]
        public bool PostThrowIgnorePlayerCollision = true;

        public bool PostThrowIgnoreSlimeSimulation = true;

        [Min(0f)]
        public float PostThrowIgnoreDurationSeconds = 1.0f;

        [ChineseHeader("反馈")]
        [ChineseLabel("拾取反馈")]
        [SerializeField] private MMFeedbacks pickupFeedbacks;

        [ChineseLabel("投掷反馈")]
        [SerializeField] private MMFeedbacks throwFeedbacks;

        [ChineseLabel("卡住脱手反馈")]
        [SerializeField] private MMFeedbacks autoDetachFeedbacks;

        [ChineseLabel("消耗反馈")]
        [SerializeField] private MMFeedbacks consumeFeedbacks;

        public bool HasHeldObject => _held != null;

        public SlimeCarryableObject HeldObject => _held;

        private SlimeCarryableObject _held;
        private Rigidbody _heldRigidbody;
        private bool _heldWasKinematic;
        private bool _heldUsedGravity;
        private RigidbodyInterpolation _heldInterpolation;
        private bool _heldInterpolationCached;
        private Collider[] _heldColliders;
        private bool[] _heldColliderWasTrigger;
        private bool _heldIndexIgnored;
        private Renderer[] _heldRenderers;
        private bool[] _heldRendererReceiveShadows;
        private ShadowCastingMode[] _heldRendererShadowCastingModes;
        private Material[][] _heldRendererMaterials;
        private float[][] _heldMaterialReceiveShadowsValues;
        private bool[][] _heldMaterialReceiveShadowsKeywordOff;

        private Slime_PBF _slimePbf;

        private SlimeConsumeBuffController _consumeBuffController;

        private Collider[] _playerColliders;
        private Coroutine _restoreCollisionCoroutine;
        private Collider[] _postThrowIgnoredColliders;
        private bool _postThrowIndexIgnored;
        private bool _pickupInTransition;
        private float _pickupTransitionElapsed;
        private Vector3 _pickupTransitionStart;
        private bool _heldMoveBlockedThisFrame;
        private float _heldMoveBlockedSeconds;
        private readonly Collider[] _releaseOverlapBuffer = new Collider[64];

        private void Awake()
        {
            CachePlayerColliders();
            ResolveConsumeBuffControllerIfNeeded();
        }

        private void CachePlayerColliders()
        {
            var root = transform.root;
            if (root != null)
            {
                _playerColliders = root.GetComponentsInChildren<Collider>(true);
            }
            else
            {
                _playerColliders = GetComponentsInParent<Collider>(true);
            }
        }

        private void Start()
        {
            ResolveCenterAnchorIfNeeded();
            ResolveSlimePbfIfNeeded();
            ResolveConsumeBuffControllerIfNeeded();
            ApplyCarrySpecIfNeeded();
        }

        private Vector3 ResolveReleasePositionByPenetration(
            Vector3 originalRootWorldPos,
            Vector3 desiredRootWorldPos,
            Collider[] objectColliders
        )
        {
            if (objectColliders == null || objectColliders.Length == 0)
                return desiredRootWorldPos;

            Vector3 rootPos = desiredRootWorldPos;

            for (int iter = 0; iter < 3; iter++)
            {
                bool moved = false;
                Vector3 delta = rootPos - originalRootWorldPos;

                for (int i = 0; i < objectColliders.Length; i++)
                {
                    var colA = objectColliders[i];
                    if (colA == null)
                        continue;

                    Bounds b = colA.bounds;
                    Vector3 center = b.center + delta;
                    Vector3 halfExtents = b.extents;
                    Quaternion rot = colA.transform.rotation;

                    int count = Physics.OverlapBoxNonAlloc(center, halfExtents, _releaseOverlapBuffer, rot, ~0, QueryTriggerInteraction.Ignore);
                    for (int j = 0; j < count; j++)
                    {
                        var colB = _releaseOverlapBuffer[j];
                        if (colB == null)
                            continue;
                        if (IsIgnoredHeldSweepCollider(colB))
                            continue;

                        if (Physics.ComputePenetration(
                                colA,
                                colA.transform.position + delta,
                                colA.transform.rotation,
                                colB,
                                colB.transform.position,
                                colB.transform.rotation,
                                out Vector3 dir,
                                out float dist
                            ))
                        {
                            float skin = 0.001f;
                            rootPos += dir * (dist + skin);
                            moved = true;
                            delta = rootPos - originalRootWorldPos;
                        }
                    }
                }

                if (!moved)
                    break;
            }

            return rootPos;
        }

        private void ApplyCarrySpecIfNeeded()
        {
            SlimeCarryableBuffSpec spec = null;
            if (_held != null)
            {
                spec = _held.GetComponent<SlimeCarryableBuffSpec>();
            }

            ResolveConsumeBuffControllerIfNeeded();
            if (_consumeBuffController == null)
            {
                return;
            }
            _consumeBuffController.SetCarrySpec(spec);
        }

        private void ResolveConsumeBuffControllerIfNeeded()
        {
            if (_consumeBuffController != null)
            {
                return;
            }

            var root = transform.root;
            if (root != null)
            {
                _consumeBuffController = root.GetComponentInChildren<SlimeConsumeBuffController>(true);
                if (_consumeBuffController == null)
                {
                    _consumeBuffController = root.gameObject.AddComponent<SlimeConsumeBuffController>();
                }
            }
            else
            {
                _consumeBuffController = GetComponentInChildren<SlimeConsumeBuffController>(true);
                if (_consumeBuffController == null)
                {
                    _consumeBuffController = gameObject.AddComponent<SlimeConsumeBuffController>();
                }
            }
        }

        public bool ConsumeHeld(out SlimeCarryableObject consumed)
        {
            return ConsumeHeld(out consumed, true);
        }

        public bool ConsumeHeld(out SlimeCarryableObject consumed, bool restoreVisuals)
        {
            consumed = null;

            if (_held == null)
            {
                return false;
            }

            consumed = _held;

            if (consumed != null)
            {
                consumeFeedbacks?.PlayFeedbacks(consumed.transform.position);
            }

            if (restoreVisuals)
            {
                RestoreHeldShadowState();
            }

            if (_heldColliders != null)
            {
                RestoreHeldColliderTriggerState();
                SetIgnorePlayerCollision(_heldColliders, false);
                if (_heldIndexIgnored && SlimeWorldColliderIndex.TryGetInstance(out var index))
                {
                    index.RegisterDynamicColliders(_heldColliders);
                }
            }

            RestoreHeldInterpolation();
            _held.IsHeld = false;

            _held = null;
            _heldRigidbody = null;
            _heldColliders = null;
            _heldColliderWasTrigger = null;
            _heldIndexIgnored = false;
            _heldRenderers = null;
            _heldRendererReceiveShadows = null;
            _heldRendererShadowCastingModes = null;
            _heldRendererMaterials = null;
            _heldMaterialReceiveShadowsValues = null;
            _heldMaterialReceiveShadowsKeywordOff = null;
            _pickupInTransition = false;

            ApplyCarrySpecIfNeeded();

            return true;
        }

        private void RestoreHeldInterpolation()
        {
            if (!_heldInterpolationCached)
                return;
            if (_heldRigidbody == null)
                return;
            _heldRigidbody.interpolation = _heldInterpolation;
            _heldInterpolationCached = false;
        }

        private void FixedUpdate()
        {
            if (_held == null || CenterAnchor == null)
            {
                _heldMoveBlockedThisFrame = false;
                _heldMoveBlockedSeconds = 0f;
                ApplyCarrySpecIfNeeded();
                return;
            }

            Vector3 baseAnchor = GetBaseAnchorWorldPosition();
            Vector3 targetPos = baseAnchor + HeldAnchorOffset;
            if (_pickupInTransition && PickupTransitionSeconds > 0f)
            {
                _pickupTransitionElapsed += Time.fixedDeltaTime;
                float t = Mathf.Clamp01(_pickupTransitionElapsed / PickupTransitionSeconds);
                float eased = t * t * (3f - 2f * t);
                targetPos = Vector3.LerpUnclamped(_pickupTransitionStart, targetPos, eased);
                if (t >= 1f)
                    _pickupInTransition = false;
            }
            if (_heldRigidbody != null)
            {
                MoveHeldRigidbodySafely(targetPos);
                if (_heldMoveBlockedThisFrame)
                {
                    _heldMoveBlockedSeconds += Time.fixedDeltaTime;
                    if (_heldMoveBlockedSeconds >= Mathf.Max(0f, StuckAutoDetachSeconds))
                    {
                        AutoDetachHeldBecauseStuck();
                        return;
                    }
                }
                else
                {
                    _heldMoveBlockedSeconds = 0f;
                }
            }
            else
            {
                _held.transform.position = targetPos;
            }

            ApplyCarrySpecIfNeeded();
        }

        private void OnControllerColliderHit(ControllerColliderHit hit)
        {
            if (_held != null)
            {
                return;
            }

            if (hit == null || hit.collider == null)
                return;

            var carryable = hit.collider.GetComponentInParent<SlimeCarryableObject>();
            if (carryable == null)
                return;

            TryPickup(carryable);
        }

        public bool TryPickup(SlimeCarryableObject carryable)
        {
            if (carryable == null)
                return false;
            if (_held != null)
                return false;
            if (!carryable.PickupEnabled || carryable.IsHeld)
                return false;

            CachePlayerColliders();

            ResolveCenterAnchorIfNeeded();
            if (CenterAnchor == null)
                return false;

            if (carryable.Rigidbody == null)
                return false;

            if (carryable.Colliders == null || carryable.Colliders.Length == 0)
                return false;

            if (_restoreCollisionCoroutine != null)
            {
                StopCoroutine(_restoreCollisionCoroutine);
                _restoreCollisionCoroutine = null;
            }

            if (_postThrowIgnoredColliders != null)
            {
                SetIgnorePlayerCollision(_postThrowIgnoredColliders, false);
                if (_postThrowIndexIgnored && SlimeWorldColliderIndex.TryGetInstance(out var index))
                {
                    index.RegisterDynamicColliders(_postThrowIgnoredColliders);
                }
                _postThrowIndexIgnored = false;
                _postThrowIgnoredColliders = null;
            }

            // 确保不会因之前的投掷而遗留忽略碰撞的状态。
            SetIgnorePlayerCollision(carryable.Colliders, false);

            _held = carryable;
            _held.IsHeld = true;

            pickupFeedbacks?.PlayFeedbacks(carryable.transform.position);

            _heldRigidbody = carryable.Rigidbody;
            _heldWasKinematic = _heldRigidbody.isKinematic;
            _heldUsedGravity = _heldRigidbody.useGravity;
            _heldInterpolation = _heldRigidbody.interpolation;
            _heldInterpolationCached = true;
            _heldMoveBlockedThisFrame = false;
            _heldMoveBlockedSeconds = 0f;

            if (!_heldWasKinematic)
            {
                _heldRigidbody.linearVelocity = Vector3.zero;
                _heldRigidbody.angularVelocity = Vector3.zero;
            }

            _heldRigidbody.isKinematic = true;
            _heldRigidbody.useGravity = false;
            _heldRigidbody.interpolation = RigidbodyInterpolation.Interpolate;

            _heldColliders = carryable.Colliders;
            CacheAndSetHeldColliderTriggerState(true);
            SetIgnorePlayerCollision(_heldColliders, true);

            _heldIndexIgnored = false;
            if (SlimeWorldColliderIndex.TryGetInstance(out var pickupIndex))
            {
                pickupIndex.UnregisterDynamicColliders(_heldColliders);
                _heldIndexIgnored = true;
            }

            CacheAndDisableHeldShadowState();

            Vector3 targetPos = GetHoldAnchorWorldPosition();
            float trans = Mathf.Max(0f, PickupTransitionSeconds);
            if (trans <= 0f)
            {
                _pickupInTransition = false;
                MoveHeldRigidbodySafely(targetPos);
            }
            else
            {
                _pickupInTransition = true;
                _pickupTransitionElapsed = 0f;
                _pickupTransitionStart = _heldRigidbody.position;
            }
            return true;
        }

        public bool ThrowHeld()
        {
            if (_held == null || _heldRigidbody == null)
            {
                if (_held != null)
                {
                    RestoreHeldShadowState();

                    if (_heldColliders != null)
                    {
                        RestoreHeldColliderTriggerState();
                        SetIgnorePlayerCollision(_heldColliders, false);
                        if (_heldIndexIgnored && SlimeWorldColliderIndex.TryGetInstance(out var failIndex))
                        {
                            failIndex.RegisterDynamicColliders(_heldColliders);
                        }
                    }

                    _heldIndexIgnored = false;
                    _held.IsHeld = false;
                }

                _held = null;
                _heldRigidbody = null;
                _heldColliders = null;
                _heldColliderWasTrigger = null;
                _heldIndexIgnored = false;
                _heldInterpolationCached = false;
                _heldRenderers = null;
                _heldRendererReceiveShadows = null;
                _heldRendererShadowCastingModes = null;
                _heldRendererMaterials = null;
                _heldMaterialReceiveShadowsValues = null;
                _heldMaterialReceiveShadowsKeywordOff = null;
                _pickupInTransition = false;

                ApplyCarrySpecIfNeeded();

                return false;
            }

            var carryable = _held;
            var rb = _heldRigidbody;
            var objectColliders = _heldColliders;

            RestoreHeldColliderTriggerState();

            RestoreHeldShadowState();

            if (objectColliders != null)
            {
                _postThrowIndexIgnored = false;
                if (SlimeWorldColliderIndex.TryGetInstance(out var index))
                {
                    if (PostThrowIgnoreSlimeSimulation)
                    {
                        index.UnregisterDynamicColliders(objectColliders);
                        _postThrowIndexIgnored = true;
                    }
                    else
                    {
                        index.RegisterDynamicColliders(objectColliders);
                        _postThrowIndexIgnored = false;
                    }
                }
            }
            _heldIndexIgnored = false;

            rb.isKinematic = false;
            rb.useGravity = _heldUsedGravity;
            if (_heldInterpolationCached)
            {
                rb.interpolation = _heldInterpolation;
                _heldInterpolationCached = false;
            }
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;

            carryable.RecordThrower(transform);

            Vector3 impulse = ComputeThrowImpulse(carryable, rb);
            rb.AddForce(impulse, ForceMode.Impulse);

            throwFeedbacks?.PlayFeedbacks(rb.position);

            if (objectColliders != null)
            {
                bool wantIgnorePlayer = PostThrowIgnorePlayerCollision;

                if (wantIgnorePlayer)
                {
                    SetIgnorePlayerCollision(objectColliders, true);
                }

                if (wantIgnorePlayer || _postThrowIndexIgnored)
                {
                    _postThrowIgnoredColliders = objectColliders;
                    float dur = Mathf.Max(0f, PostThrowIgnoreDurationSeconds);
                    _restoreCollisionCoroutine = StartCoroutine(RestoreCollisionAfterTime(objectColliders, carryable.transform, dur));
                }
            }

            carryable.IsHeld = false;
            _held = null;
            _heldRigidbody = null;
            _heldColliders = null;
            _heldColliderWasTrigger = null;
            _heldIndexIgnored = false;
            _heldInterpolationCached = false;
            _heldRenderers = null;
            _heldRendererReceiveShadows = null;
            _heldRendererShadowCastingModes = null;
            _heldRendererMaterials = null;
            _heldMaterialReceiveShadowsValues = null;
            _heldMaterialReceiveShadowsKeywordOff = null;
            _pickupInTransition = false;

            ApplyCarrySpecIfNeeded();

            return true;
        }

        private void ResolveCenterAnchorIfNeeded()
        {
            if (CenterAnchor != null)
                return;

            var slimePbf = GetComponentInChildren<Slime_PBF>();
            if (slimePbf != null && slimePbf.trans != null)
            {
                _slimePbf = slimePbf;
                CenterAnchor = slimePbf.trans;
                return;
            }

            CenterAnchor = transform;
        }

        private void ResolveSlimePbfIfNeeded()
        {
            if (_slimePbf != null)
                return;

            _slimePbf = GetComponentInChildren<Slime_PBF>();
        }

        private Vector3 GetBaseAnchorWorldPosition()
        {
            Vector3 anchor = CenterAnchor != null ? CenterAnchor.position : transform.position;
            if (PreferSlimePbfCentroid)
            {
                ResolveSlimePbfIfNeeded();
                if (_slimePbf != null)
                {
                    Vector3 centroid = _slimePbf.MainBodyCentroidWorld;
                    if ((centroid - anchor).sqrMagnitude < 2500f)
                    {
                        anchor = centroid;
                    }
                }
            }
            return anchor;
        }

        private Vector3 GetHoldAnchorWorldPosition()
        {
            return GetBaseAnchorWorldPosition() + HeldAnchorOffset;
        }

        private void MoveHeldRigidbodySafely(Vector3 targetWorldPos)
        {
            if (_heldRigidbody == null)
                return;
            _heldMoveBlockedThisFrame = false;

            Vector3 start = _heldRigidbody.position;
            Vector3 delta = targetWorldPos - start;
            float dist = delta.magnitude;
            if (dist <= 0.00001f)
            {
                _heldRigidbody.MovePosition(targetWorldPos);
                return;
            }

            Vector3 dir = delta / dist;
            var hits = _heldRigidbody.SweepTestAll(dir, dist, QueryTriggerInteraction.Ignore);
            if (hits != null && hits.Length > 0)
            {
                float minDist = float.PositiveInfinity;
                for (int i = 0; i < hits.Length; i++)
                {
                    var hit = hits[i];
                    var col = hit.collider;
                    if (col == null)
                        continue;
                    if (IsIgnoredHeldSweepCollider(col))
                        continue;
                    if (hit.distance < minDist)
                        minDist = hit.distance;
                }

                if (!float.IsPositiveInfinity(minDist))
                {
                    float skin = 0.001f;
                    float safeDist = Mathf.Max(0f, minDist - skin);
                    if (safeDist < dist - 0.0005f)
                    {
                        _heldMoveBlockedThisFrame = true;
                    }
                    targetWorldPos = start + dir * safeDist;
                }
            }

            _heldRigidbody.MovePosition(targetWorldPos);
        }

        private void AutoDetachHeldBecauseStuck()
        {
            if (_held == null)
                return;

            autoDetachFeedbacks?.PlayFeedbacks(_held.transform.position);

            var carryable = _held;
            var rb = _heldRigidbody;
            var objectColliders = _heldColliders;

            RestoreHeldShadowState();

            if (objectColliders != null)
            {
                RestoreHeldColliderTriggerState();
                SetIgnorePlayerCollision(objectColliders, false);
                if (_heldIndexIgnored && SlimeWorldColliderIndex.TryGetInstance(out var index))
                {
                    index.RegisterDynamicColliders(objectColliders);
                }
            }
            _heldIndexIgnored = false;

            carryable.IsHeld = false;

            Vector3 releasePos = rb != null ? rb.position : carryable.transform.position;
            if (objectColliders != null)
            {
                releasePos = ResolveReleasePositionByPenetration(carryable.transform.position, releasePos, objectColliders);
            }

            if (rb != null)
            {
                rb.isKinematic = _heldWasKinematic;
                rb.useGravity = _heldUsedGravity;
                if (_heldInterpolationCached)
                {
                    rb.interpolation = _heldInterpolation;
                    _heldInterpolationCached = false;
                }
                rb.position = releasePos;
                if (!rb.isKinematic)
                {
                    rb.linearVelocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
            }
            else
            {
                carryable.transform.position = releasePos;
            }

            _held = null;
            _heldRigidbody = null;
            _heldColliders = null;
            _heldColliderWasTrigger = null;
            _heldRenderers = null;
            _heldRendererReceiveShadows = null;
            _heldRendererShadowCastingModes = null;
            _heldRendererMaterials = null;
            _heldMaterialReceiveShadowsValues = null;
            _heldMaterialReceiveShadowsKeywordOff = null;
            _pickupInTransition = false;
            _heldMoveBlockedThisFrame = false;
            _heldMoveBlockedSeconds = 0f;
            _heldInterpolationCached = false;

            ApplyCarrySpecIfNeeded();
        }

        private bool IsIgnoredHeldSweepCollider(Collider col)
        {
            if (col == null)
                return true;

            if (_heldColliders != null)
            {
                for (int i = 0; i < _heldColliders.Length; i++)
                {
                    var held = _heldColliders[i];
                    if (held == null)
                        continue;
                    if (held == col)
                        return true;
                }
            }

            if (_playerColliders != null)
            {
                for (int i = 0; i < _playerColliders.Length; i++)
                {
                    var player = _playerColliders[i];
                    if (player == null)
                        continue;
                    if (player == col)
                        return true;
                }
            }

            return false;
        }

        private Vector3 ComputeThrowImpulse(SlimeCarryableObject carryable, Rigidbody rb)
        {
            Vector3 startWorldPos = rb != null ? rb.position : GetBaseAnchorWorldPosition();
            float mass = rb != null ? Mathf.Max(0.0001f, rb.mass) : 1f;

            ResolveConsumeBuffControllerIfNeeded();
            float throwMultiplier = _consumeBuffController != null ? _consumeBuffController.CurrentThrowRangeMultiplier : 1f;
            float maxThrowRange = carryable.MaxThrowRange * throwMultiplier;

            Vector3 targetWorldPos;
            switch (carryable.DirectionMode)
            {
                case SlimeCarryableObject.ThrowDirectionMode.MouseXZ:
                    if (!TryGetMouseAimPointOnGround(out targetWorldPos))
                    {
                        targetWorldPos = GetForwardAimPointOnGround(startWorldPos, maxThrowRange);
                    }
                    break;
                case SlimeCarryableObject.ThrowDirectionMode.CharacterForward:
                default:
                    targetWorldPos = GetForwardAimPointOnGround(startWorldPos, maxThrowRange);
                    break;
            }

            ClampTargetToMaxRangeAndProjectToGround(startWorldPos, maxThrowRange, ref targetWorldPos, out Vector3 deltaXZ, out float distXZ);
            return ComputeBallisticImpulseAutoAngle(startWorldPos, targetWorldPos, deltaXZ, distXZ, mass);
        }

        private void ClampTargetToMaxRangeAndProjectToGround(
            Vector3 startWorldPos,
            float maxThrowRange,
            ref Vector3 targetWorldPos,
            out Vector3 deltaXZ,
            out float distXZ
        )
        {
            Vector3 startXZ = new Vector3(startWorldPos.x, 0f, startWorldPos.z);
            Vector3 targetXZ = new Vector3(targetWorldPos.x, 0f, targetWorldPos.z);
            deltaXZ = targetXZ - startXZ;
            distXZ = deltaXZ.magnitude;

            float maxRange = Mathf.Max(0.01f, maxThrowRange);
            if (distXZ > maxRange)
            {
                deltaXZ = deltaXZ / distXZ * maxRange;
                distXZ = maxRange;
                targetWorldPos = new Vector3(startWorldPos.x + deltaXZ.x, targetWorldPos.y, startWorldPos.z + deltaXZ.z);
                TryProjectPointToGround(ref targetWorldPos);
            }

            if (distXZ <= 0.01f)
            {
                Vector3 fwd = transform.forward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.0001f)
                    fwd = Vector3.forward;
                fwd.Normalize();

                distXZ = 0.01f;
                deltaXZ = fwd * distXZ;
                targetWorldPos = new Vector3(startWorldPos.x + deltaXZ.x, targetWorldPos.y, startWorldPos.z + deltaXZ.z);
                TryProjectPointToGround(ref targetWorldPos);
            }
        }

        private Vector3 GetForwardAimPointOnGround(Vector3 startWorldPos, float maxThrowRange)
        {
            Vector3 fwd = transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f)
                fwd = Vector3.forward;
            fwd.Normalize();

            float range = Mathf.Max(0.01f, maxThrowRange);
            Vector3 pos = startWorldPos + fwd * range;
            TryProjectPointToGround(ref pos);
            return pos;
        }

        private void TryProjectPointToGround(ref Vector3 pos)
        {
            float rayDist = Mathf.Max(0.01f, GroundRaycastMaxDistance);
            Vector3 downOrigin = pos + Vector3.up * rayDist;
            if (Physics.Raycast(downOrigin, Vector3.down, out RaycastHit hit, rayDist * 2f, GroundRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                pos = hit.point;
            }
        }

        private Vector3 ComputeBallisticImpulseAutoAngle(
            Vector3 startWorldPos,
            Vector3 targetWorldPos,
            Vector3 deltaXZ,
            float distXZ,
            float mass
        )
        {
            float g = Physics.gravity.y;
            float gAbs = -g;
            if (gAbs <= 0.0001f)
            {
                return Vector3.zero;
            }

            float x = Mathf.Max(0.01f, distXZ);
            float y = targetWorldPos.y - startWorldPos.y;

            float theta = Mathf.Clamp(FixedThrowAngleDegrees, 1f, 89f) * Mathf.Deg2Rad;
            float thetaMin = Mathf.Atan2(Mathf.Max(0f, y) + 0.001f, x);
            if (theta < thetaMin)
            {
                theta = Mathf.Min(thetaMin, 89f * Mathf.Deg2Rad);
            }

            float cos = Mathf.Cos(theta);
            float sin = Mathf.Sin(theta);
            float tan = sin / Mathf.Max(0.0001f, cos);

            float denom = 2f * cos * cos * (x * tan - y);
            denom = Mathf.Max(0.0001f, denom);

            float v2 = gAbs * x * x / denom;
            float v = Mathf.Sqrt(Mathf.Max(0.0001f, v2));

            Vector3 dirXZ = deltaXZ / x;
            Vector3 v0 = dirXZ * (v * cos);
            v0.y = v * sin;
            return v0 * mass;
        }

        private bool TryGetMouseAimPointOnGround(out Vector3 hitWorldPos)
        {
            hitWorldPos = default;

            var mainCam = Camera.main;
            if (mainCam == null)
            {
                return false;
            }

            float rayDist = Mathf.Max(0.01f, GroundRaycastMaxDistance);
            Ray ray = mainCam.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out RaycastHit hit, rayDist, GroundRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                hitWorldPos = hit.point;
                return true;
            }

            return false;
        }

        private void SetIgnorePlayerCollision(Collider[] objectColliders, bool ignore)
        {
            if (objectColliders == null || _playerColliders == null)
                return;

            for (int i = 0; i < _playerColliders.Length; i++)
            {
                var playerCol = _playerColliders[i];
                if (playerCol == null)
                    continue;

                for (int j = 0; j < objectColliders.Length; j++)
                {
                    var objectCol = objectColliders[j];
                    if (objectCol == null)
                        continue;
                    if (playerCol == objectCol)
                        continue;

                    UnityEngine.Physics.IgnoreCollision(playerCol, objectCol, ignore);
                }
            }
        }

        private IEnumerator RestoreCollisionAfterTime(Collider[] objectColliders, Transform thrownObject, float durationSeconds)
        {
            float elapsed = 0f;

            while (true)
            {
                if (thrownObject == null)
                {
                    break;
                }

                elapsed += Time.fixedDeltaTime;
                if (elapsed >= durationSeconds)
                {
                    break;
                }

                yield return new WaitForFixedUpdate();
            }

            SetIgnorePlayerCollision(objectColliders, false);
            if (_postThrowIndexIgnored && SlimeWorldColliderIndex.TryGetInstance(out var index))
            {
                index.RegisterDynamicColliders(objectColliders);
            }
            _postThrowIndexIgnored = false;
            _postThrowIgnoredColliders = null;
            _restoreCollisionCoroutine = null;
        }

        private void OnDisable()
        {
            if (_consumeBuffController != null)
            {
                _consumeBuffController.SetCarrySpec(null);
            }

            RestoreHeldShadowState();

            if (_held != null)
            {
                RestoreHeldShadowState();

                if (_heldColliders != null)
                {
                    RestoreHeldColliderTriggerState();
                    SetIgnorePlayerCollision(_heldColliders, false);
                    if (_heldIndexIgnored && SlimeWorldColliderIndex.TryGetInstance(out var failIndex))
                    {
                        failIndex.RegisterDynamicColliders(_heldColliders);
                    }
                }

                _heldIndexIgnored = false;
                _held.IsHeld = false;
            }

            if (_heldRigidbody != null)
            {
                _heldRigidbody.isKinematic = _heldWasKinematic;
                _heldRigidbody.useGravity = _heldUsedGravity;
                RestoreHeldInterpolation();
            }

            if (_restoreCollisionCoroutine != null)
            {
                StopCoroutine(_restoreCollisionCoroutine);
                _restoreCollisionCoroutine = null;
            }

            if (_postThrowIgnoredColliders != null)
            {
                SetIgnorePlayerCollision(_postThrowIgnoredColliders, false);
                if (_postThrowIndexIgnored && SlimeWorldColliderIndex.TryGetInstance(out var index))
                {
                    index.RegisterDynamicColliders(_postThrowIgnoredColliders);
                }
                _postThrowIndexIgnored = false;
                _postThrowIgnoredColliders = null;
            }

            if (_held != null)
                _held.IsHeld = false;

            _held = null;
            _heldRigidbody = null;
            _heldColliders = null;
            _heldColliderWasTrigger = null;
            _heldIndexIgnored = false;
            _heldRenderers = null;
            _heldRendererReceiveShadows = null;
            _heldRendererShadowCastingModes = null;
            _heldRendererMaterials = null;
            _heldMaterialReceiveShadowsValues = null;
            _heldMaterialReceiveShadowsKeywordOff = null;
            _pickupInTransition = false;
            _heldInterpolationCached = false;
        }

        private bool IsHeldCollider(Collider col)
        {
            if (col == null || _heldColliders == null)
            {
                return false;
            }

            for (int i = 0; i < _heldColliders.Length; i++)
            {
                var held = _heldColliders[i];
                if (held == null)
                {
                    continue;
                }
                if (held == col)
                {
                    return true;
                }
            }

            return false;
        }

        private void CacheAndSetHeldColliderTriggerState(bool isTrigger)
        {
            if (_heldColliders == null || _heldColliders.Length == 0)
            {
                _heldColliderWasTrigger = null;
                return;
            }

            _heldColliderWasTrigger = new bool[_heldColliders.Length];
            for (int i = 0; i < _heldColliders.Length; i++)
            {
                var col = _heldColliders[i];
                if (col == null)
                {
                    _heldColliderWasTrigger[i] = false;
                    continue;
                }

                _heldColliderWasTrigger[i] = col.isTrigger;
                col.isTrigger = isTrigger;
            }
        }

        private void RestoreHeldColliderTriggerState()
        {
            if (_heldColliders == null || _heldColliderWasTrigger == null)
            {
                return;
            }

            for (int i = 0; i < _heldColliders.Length; i++)
            {
                var col = _heldColliders[i];
                if (col == null)
                {
                    continue;
                }

                bool wasTrigger = i < _heldColliderWasTrigger.Length && _heldColliderWasTrigger[i];
                col.isTrigger = wasTrigger;
            }
        }

        private void CacheAndDisableHeldShadowState()
        {
            if (_held == null)
                return;

            _heldRenderers = _held.GetComponentsInChildren<Renderer>(true);
            if (_heldRenderers == null || _heldRenderers.Length == 0)
                return;

            _heldRendererReceiveShadows = new bool[_heldRenderers.Length];
            _heldRendererShadowCastingModes = new ShadowCastingMode[_heldRenderers.Length];
            _heldRendererMaterials = new Material[_heldRenderers.Length][];
            _heldMaterialReceiveShadowsValues = new float[_heldRenderers.Length][];
            _heldMaterialReceiveShadowsKeywordOff = new bool[_heldRenderers.Length][];

            for (int i = 0; i < _heldRenderers.Length; i++)
            {
                var r = _heldRenderers[i];
                if (r == null)
                    continue;

                _heldRendererReceiveShadows[i] = r.receiveShadows;
                _heldRendererShadowCastingModes[i] = r.shadowCastingMode;

                var mats = r.materials;
                _heldRendererMaterials[i] = mats;
                if (mats != null && mats.Length > 0)
                {
                    var receiveValues = new float[mats.Length];
                    var keywordOff = new bool[mats.Length];
                    for (int m = 0; m < mats.Length; m++)
                    {
                        var mat = mats[m];
                        if (mat == null)
                        {
                            receiveValues[m] = float.NaN;
                            keywordOff[m] = false;
                            continue;
                        }

                        keywordOff[m] = mat.IsKeywordEnabled("_RECEIVE_SHADOWS_OFF");

                        if (mat.HasProperty("_ReceiveShadows"))
                        {
                            receiveValues[m] = mat.GetFloat("_ReceiveShadows");
                            mat.SetFloat("_ReceiveShadows", 0f);
                        }
                        else
                        {
                            receiveValues[m] = float.NaN;
                        }

                        mat.EnableKeyword("_RECEIVE_SHADOWS_OFF");
                    }

                    _heldMaterialReceiveShadowsValues[i] = receiveValues;
                    _heldMaterialReceiveShadowsKeywordOff[i] = keywordOff;
                }

                r.receiveShadows = false;
                r.shadowCastingMode = ShadowCastingMode.Off;
            }
        }

        private void RestoreHeldShadowState()
        {
            if (_heldRenderers == null || _heldRendererReceiveShadows == null)
                return;

            for (int i = 0; i < _heldRenderers.Length; i++)
            {
                var r = _heldRenderers[i];
                if (r == null)
                    continue;

                if (i < _heldRendererReceiveShadows.Length)
                    r.receiveShadows = _heldRendererReceiveShadows[i];

                if (_heldRendererShadowCastingModes != null && i < _heldRendererShadowCastingModes.Length)
                    r.shadowCastingMode = _heldRendererShadowCastingModes[i];

                if (_heldRendererMaterials != null && i < _heldRendererMaterials.Length)
                {
                    var mats = _heldRendererMaterials[i];
                    var receiveValues = _heldMaterialReceiveShadowsValues != null && i < _heldMaterialReceiveShadowsValues.Length
                        ? _heldMaterialReceiveShadowsValues[i]
                        : null;
                    var keywordOff = _heldMaterialReceiveShadowsKeywordOff != null && i < _heldMaterialReceiveShadowsKeywordOff.Length
                        ? _heldMaterialReceiveShadowsKeywordOff[i]
                        : null;

                    if (mats != null)
                    {
                        for (int m = 0; m < mats.Length; m++)
                        {
                            var mat = mats[m];
                            if (mat == null)
                                continue;

                            if (receiveValues != null && m < receiveValues.Length && !float.IsNaN(receiveValues[m]) && mat.HasProperty("_ReceiveShadows"))
                            {
                                mat.SetFloat("_ReceiveShadows", receiveValues[m]);
                            }

                            bool wasKeywordOff = keywordOff != null && m < keywordOff.Length && keywordOff[m];
                            if (wasKeywordOff)
                                mat.EnableKeyword("_RECEIVE_SHADOWS_OFF");
                            else
                                mat.DisableKeyword("_RECEIVE_SHADOWS_OFF");
                        }
                    }
                }
            }
        }
    }
}
