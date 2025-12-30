using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

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

        [Header("投掷")]
        [Range(1f, 89f)]
        public float FixedThrowAngleDegrees = 45f;

        [Min(0.01f)]
        public float MaxThrowRange = 10f;

        public LayerMask GroundRaycastLayers = ~0;

        [Min(0.01f)]
        public float GroundRaycastMaxDistance = 200f;

        [Tooltip("投掷后短暂忽略玩家与物体的碰撞，避免重叠抖动。")]
        public bool PostThrowIgnorePlayerCollision = true;

        public bool PostThrowIgnoreSlimeSimulation = true;

        [Min(0f)]
        public float PostThrowIgnoreDurationSeconds = 1.0f;

        public bool HasHeldObject => _held != null;

        private SlimeCarryableObject _held;
        private Rigidbody _heldRigidbody;
        private bool _heldWasKinematic;
        private bool _heldUsedGravity;
        private Collider[] _heldColliders;
        private bool[] _heldColliderEnabledStates;
        private Renderer[] _heldRenderers;
        private bool[] _heldRendererReceiveShadows;
        private ShadowCastingMode[] _heldRendererShadowCastingModes;
        private Material[][] _heldRendererMaterials;
        private float[][] _heldMaterialReceiveShadowsValues;
        private bool[][] _heldMaterialReceiveShadowsKeywordOff;

        private Slime_PBF _slimePbf;

        private Collider[] _playerColliders;
        private Coroutine _restoreCollisionCoroutine;
        private Collider[] _postThrowIgnoredColliders;
        private bool _postThrowIndexIgnored;
        private bool _pickupInTransition;
        private float _pickupTransitionElapsed;
        private Vector3 _pickupTransitionStart;
        private int _dbgThrowFailLogFrame;

        private void Awake()
        {
            _playerColliders = GetComponentsInChildren<Collider>(true);
            _dbgThrowFailLogFrame = -999999;
        }

        private void Start()
        {
            ResolveCenterAnchorIfNeeded();
            ResolveSlimePbfIfNeeded();
        }

        private void FixedUpdate()
        {
            if (_held == null || CenterAnchor == null)
                return;

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
                _heldRigidbody.MovePosition(targetPos);
            }
            else
            {
                _held.transform.position = targetPos;
            }
        }

        private void OnControllerColliderHit(ControllerColliderHit hit)
        {
            if (_held != null)
                return;

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

            _heldRigidbody = carryable.Rigidbody;
            _heldWasKinematic = _heldRigidbody.isKinematic;
            _heldUsedGravity = _heldRigidbody.useGravity;

            if (!_heldWasKinematic)
            {
                _heldRigidbody.linearVelocity = Vector3.zero;
                _heldRigidbody.angularVelocity = Vector3.zero;
            }

            _heldRigidbody.isKinematic = true;
            _heldRigidbody.useGravity = false;

            _heldColliders = carryable.Colliders;
            _heldColliderEnabledStates = new bool[_heldColliders.Length];
            for (int i = 0; i < _heldColliders.Length; i++)
            {
                var col = _heldColliders[i];
                if (col == null)
                    continue;

                _heldColliderEnabledStates[i] = col.enabled;
                col.enabled = false;
            }

            CacheAndDisableHeldShadowState();

            Vector3 targetPos = GetHoldAnchorWorldPosition();
            float trans = Mathf.Max(0f, PickupTransitionSeconds);
            if (trans <= 0f)
            {
                _pickupInTransition = false;
                _heldRigidbody.MovePosition(targetPos);
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
                if (_dbgThrowFailLogFrame != Time.frameCount)
                {
                    _dbgThrowFailLogFrame = Time.frameCount;
                    Debug.LogWarning($"[SlimeCarrySlot] ThrowHeld failed. held={( _held != null ? _held.name : "null" )} heldRbNull={(_heldRigidbody == null)} postThrowIgnored={(_postThrowIgnoredColliders != null)} pickupInTransition={_pickupInTransition}", this);
                }

                if (_held != null)
                {
                    RestoreHeldShadowState();
                    if (_heldColliders != null && _heldColliderEnabledStates != null)
                    {
                        for (int i = 0; i < _heldColliders.Length; i++)
                        {
                            var col = _heldColliders[i];
                            if (col == null)
                                continue;
                            col.enabled = _heldColliderEnabledStates[i];
                        }
                    }

                    _held.IsHeld = false;
                }

                _held = null;
                _heldRigidbody = null;
                _heldColliders = null;
                _heldColliderEnabledStates = null;
                _heldRenderers = null;
                _heldRendererReceiveShadows = null;
                _heldRendererShadowCastingModes = null;
                _heldRendererMaterials = null;
                _heldMaterialReceiveShadowsValues = null;
                _heldMaterialReceiveShadowsKeywordOff = null;
                _pickupInTransition = false;

                return false;
            }

            var carryable = _held;
            var rb = _heldRigidbody;
            var objectColliders = _heldColliders;

            RestoreHeldShadowState();

            if (objectColliders != null)
            {
                bool wantIgnoreSlime = PostThrowIgnoreSlimeSimulation;
                _postThrowIndexIgnored = false;
                if (wantIgnoreSlime && SlimeWorldColliderIndex.TryGetInstance(out var index))
                {
                    index.UnregisterDynamicColliders(objectColliders);
                    _postThrowIndexIgnored = true;
                }
            }

            if (objectColliders != null && _heldColliderEnabledStates != null)
            {
                for (int i = 0; i < objectColliders.Length; i++)
                {
                    var col = objectColliders[i];
                    if (col == null)
                        continue;
                    col.enabled = _heldColliderEnabledStates[i];
                }
            }

            rb.isKinematic = false;
            rb.useGravity = _heldUsedGravity;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;

            Vector3 impulse = ComputeThrowImpulse(carryable, rb);
            if (impulse.sqrMagnitude <= 0.000001f && _dbgThrowFailLogFrame != Time.frameCount)
            {
                _dbgThrowFailLogFrame = Time.frameCount;
                Debug.LogWarning($"[SlimeCarrySlot] ThrowHeld impulse is near zero. held={carryable.name} angleDeg={FixedThrowAngleDegrees} maxRange={MaxThrowRange} gravityY={Physics.gravity.y}", this);
            }
            rb.AddForce(impulse, ForceMode.Impulse);

            if (objectColliders != null)
            {
                bool wantIgnorePlayer = PostThrowIgnorePlayerCollision;

                if (wantIgnorePlayer)
                {
                    int verifiedIgnored = 0;
                    int verifiedMismatch = 0;
                    SetIgnorePlayerCollision(objectColliders, true, out verifiedIgnored, out verifiedMismatch);
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
            _heldColliderEnabledStates = null;
            _heldRenderers = null;
            _heldRendererReceiveShadows = null;
            _heldRendererShadowCastingModes = null;
            _heldRendererMaterials = null;
            _heldMaterialReceiveShadowsValues = null;
            _heldMaterialReceiveShadowsKeywordOff = null;
            _pickupInTransition = false;

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

        private Vector3 ComputeThrowImpulse(SlimeCarryableObject carryable, Rigidbody rb)
        {
            Vector3 startWorldPos = rb != null ? rb.position : GetBaseAnchorWorldPosition();
            float mass = rb != null ? Mathf.Max(0.0001f, rb.mass) : 1f;

            Vector3 targetWorldPos;
            switch (carryable.DirectionMode)
            {
                case SlimeCarryableObject.ThrowDirectionMode.MouseXZ:
                    if (!TryGetMouseAimPointOnGround(out targetWorldPos))
                    {
                        targetWorldPos = GetForwardAimPointOnGround(startWorldPos);
                    }
                    break;
                case SlimeCarryableObject.ThrowDirectionMode.CharacterForward:
                default:
                    targetWorldPos = GetForwardAimPointOnGround(startWorldPos);
                    break;
            }

            ClampTargetToMaxRangeAndProjectToGround(startWorldPos, ref targetWorldPos, out Vector3 deltaXZ, out float distXZ);
            return ComputeBallisticImpulseAutoAngle(startWorldPos, targetWorldPos, deltaXZ, distXZ, mass);
        }

        private void ClampTargetToMaxRangeAndProjectToGround(
            Vector3 startWorldPos,
            ref Vector3 targetWorldPos,
            out Vector3 deltaXZ,
            out float distXZ
        )
        {
            Vector3 startXZ = new Vector3(startWorldPos.x, 0f, startWorldPos.z);
            Vector3 targetXZ = new Vector3(targetWorldPos.x, 0f, targetWorldPos.z);
            deltaXZ = targetXZ - startXZ;
            distXZ = deltaXZ.magnitude;

            float maxRange = Mathf.Max(0.01f, MaxThrowRange);
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

        private Vector3 GetForwardAimPointOnGround(Vector3 startWorldPos)
        {
            Vector3 fwd = transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f)
                fwd = Vector3.forward;
            fwd.Normalize();

            float range = Mathf.Max(0.01f, MaxThrowRange);
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

                    UnityEngine.Physics.IgnoreCollision(playerCol, objectCol, ignore);
                }
            }
        }

        private void SetIgnorePlayerCollision(Collider[] objectColliders, bool ignore, out int verifiedIgnored, out int verifiedMismatch)
        {
            verifiedIgnored = 0;
            verifiedMismatch = 0;

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

                    UnityEngine.Physics.IgnoreCollision(playerCol, objectCol, ignore);
                    bool state = UnityEngine.Physics.GetIgnoreCollision(playerCol, objectCol);
                    if (state == ignore)
                        verifiedIgnored++;
                    else
                        verifiedMismatch++;
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

            int verifiedNotIgnored = 0;
            int stillIgnored = 0;
            SetIgnorePlayerCollision(objectColliders, false, out verifiedNotIgnored, out stillIgnored);
            if (_postThrowIndexIgnored && SlimeWorldColliderIndex.TryGetInstance(out var index))
            {
                index.RegisterDynamicColliders(objectColliders);
            }

            if (thrownObject != null)
            {
            }
            _postThrowIndexIgnored = false;
            _postThrowIgnoredColliders = null;
            _restoreCollisionCoroutine = null;
        }

        private void OnDisable()
        {
            RestoreHeldShadowState();

            if (_held != null && _heldColliders != null && _heldColliderEnabledStates != null)
            {
                for (int i = 0; i < _heldColliders.Length; i++)
                {
                    var col = _heldColliders[i];
                    if (col == null)
                        continue;
                    col.enabled = _heldColliderEnabledStates[i];
                }
            }

            if (_heldRigidbody != null)
            {
                _heldRigidbody.isKinematic = _heldWasKinematic;
                _heldRigidbody.useGravity = _heldUsedGravity;
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
            _heldColliderEnabledStates = null;
            _heldRenderers = null;
            _heldRendererReceiveShadows = null;
            _heldRendererShadowCastingModes = null;
            _heldRendererMaterials = null;
            _heldMaterialReceiveShadowsValues = null;
            _heldMaterialReceiveShadowsKeywordOff = null;
            _pickupInTransition = false;
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
