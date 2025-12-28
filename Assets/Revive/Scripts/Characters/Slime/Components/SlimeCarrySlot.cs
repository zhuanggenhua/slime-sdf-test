using System.Collections;
using UnityEngine;

namespace Revive.Slime
{
    [DisallowMultipleComponent]
    public class SlimeCarrySlot : MonoBehaviour
    {
        [Header("Bindings")]
        [Tooltip("The world-space anchor where held objects will be placed. Defaults to Slime_PBF.trans if available.")]
        public Transform CenterAnchor;

        public bool PreferSlimePbfCentroid = true;
        public Vector3 HeldAnchorOffset = new Vector3(0f, 0.5f, 0f);

        [Header("Holding")]
        [Tooltip("When true, the held object's colliders are disabled while held (simplest, avoids PBF sampling conflicts).")]
        public bool DisableHeldColliders = true;

        [Tooltip("Optionally disable gravity while held.")]
        public bool DisableGravityWhileHeld = false;

        [Header("Throw")]
        [Tooltip("After throwing, keep collisions between player and object ignored for a short grace period to avoid overlap jitter.")]
        [Min(0f)]
        public float PostThrowIgnorePlayerCollisionSeconds = 0.1f;

        public bool HasHeldObject => _held != null;

        private SlimeCarryableObject _held;
        private Rigidbody _heldRigidbody;
        private bool _heldWasKinematic;
        private bool _heldUsedGravity;
        private Collider[] _heldColliders;
        private bool[] _heldColliderEnabledStates;

        private Slime_PBF _slimePbf;

        private int _dbgAnchorLogLastFrame = -9999;
        private bool _dbgUsedCentroid;
        private Vector3 _dbgCenterAnchorPos;
        private Vector3 _dbgCentroidPos;
        private Vector3 _dbgBaseAnchorPos;

        private Collider[] _playerColliders;
        private Coroutine _restoreCollisionCoroutine;

        private void Awake()
        {
            _playerColliders = GetComponentsInChildren<Collider>(true);
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
            if (_heldRigidbody != null)
            {
                _heldRigidbody.MovePosition(targetPos);
            }
            else
            {
                _held.transform.position = targetPos;
            }

            if (Time.frameCount - _dbgAnchorLogLastFrame >= 30)
            {
                _dbgAnchorLogLastFrame = Time.frameCount;
                string centroidStr = _slimePbf != null ? _dbgCentroidPos.ToString("F3") : "null";
                Debug.Log(
                    $"[SlimeCarrySlot] frame={Time.frameCount} held={_held.name} usedCentroid={_dbgUsedCentroid} centerAnchor={_dbgCenterAnchorPos.ToString("F3")} centroid={centroidStr} baseAnchor={_dbgBaseAnchorPos.ToString("F3")} offset={HeldAnchorOffset.ToString("F3")} target={targetPos.ToString("F3")}",
                    this);
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

            // Ensure collisions are not left in an ignored state from a previous throw.
            SetIgnorePlayerCollision(carryable.Colliders, false);

            _held = carryable;
            _held.IsHeld = true;

            _heldRigidbody = carryable.Rigidbody;
            _heldWasKinematic = _heldRigidbody.isKinematic;
            _heldUsedGravity = _heldRigidbody.useGravity;

            _heldRigidbody.isKinematic = true;
            if (DisableGravityWhileHeld)
                _heldRigidbody.useGravity = false;
            _heldRigidbody.linearVelocity = Vector3.zero;
            _heldRigidbody.angularVelocity = Vector3.zero;

            _heldColliders = carryable.Colliders;
            _heldColliderEnabledStates = new bool[_heldColliders.Length];
            if (DisableHeldColliders)
            {
                for (int i = 0; i < _heldColliders.Length; i++)
                {
                    var col = _heldColliders[i];
                    if (col == null)
                        continue;

                    _heldColliderEnabledStates[i] = col.enabled;
                    col.enabled = false;
                }
            }

            Vector3 targetPos = GetHoldAnchorWorldPosition();
            _heldRigidbody.MovePosition(targetPos);
            return true;
        }

        public bool ThrowHeld()
        {
            if (_held == null || _heldRigidbody == null)
                return false;

            var carryable = _held;
            var rb = _heldRigidbody;
            var objectColliders = _heldColliders;

            if (DisableHeldColliders && objectColliders != null && _heldColliderEnabledStates != null)
            {
                for (int i = 0; i < objectColliders.Length; i++)
                {
                    var col = objectColliders[i];
                    if (col == null)
                        continue;
                    col.enabled = _heldColliderEnabledStates[i];
                }
            }

            rb.isKinematic = _heldWasKinematic;
            rb.useGravity = _heldUsedGravity;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;

            Vector3 impulse = ComputeThrowImpulse(carryable);
            rb.AddForce(impulse, ForceMode.Impulse);

            if (PostThrowIgnorePlayerCollisionSeconds > 0f && objectColliders != null)
            {
                SetIgnorePlayerCollision(objectColliders, true);
                _restoreCollisionCoroutine = StartCoroutine(RestoreCollisionAfterDelay(objectColliders, PostThrowIgnorePlayerCollisionSeconds));
            }

            carryable.IsHeld = false;
            _held = null;
            _heldRigidbody = null;
            _heldColliders = null;
            _heldColliderEnabledStates = null;

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

            _dbgCenterAnchorPos = anchor;
            _dbgUsedCentroid = false;
            _dbgCentroidPos = default;
            if (PreferSlimePbfCentroid)
            {
                ResolveSlimePbfIfNeeded();
                if (_slimePbf != null)
                {
                    Vector3 centroid = _slimePbf.MainBodyCentroidWorld;
                    _dbgCentroidPos = centroid;
                    if ((centroid - anchor).sqrMagnitude < 2500f)
                    {
                        anchor = centroid;
                        _dbgUsedCentroid = true;
                    }
                }
            }

            _dbgBaseAnchorPos = anchor;

            return anchor;
        }

        private Vector3 GetHoldAnchorWorldPosition()
        {
            return GetBaseAnchorWorldPosition() + HeldAnchorOffset;
        }

        private Vector3 ComputeThrowImpulse(SlimeCarryableObject carryable)
        {
            Vector3 dir = Vector3.forward;
            switch (carryable.DirectionMode)
            {
                case SlimeCarryableObject.ThrowDirectionMode.CharacterForward:
                    dir = transform.forward;
                    break;
                case SlimeCarryableObject.ThrowDirectionMode.MouseXZ:
                default:
                    dir = GetMouseAimDirectionXZ();
                    break;
            }

            if (dir.sqrMagnitude < 0.0001f)
                dir = Vector3.forward;
            dir.Normalize();

            Vector3 impulse = dir * Mathf.Max(0f, carryable.ThrowImpulse);
            float up = Mathf.Max(0f, carryable.UpwardImpulse);
            if (up > 0f)
                impulse += Vector3.up * up;

            return impulse;
        }

        private Vector3 GetMouseAimDirectionXZ()
        {
            var mainCam = Camera.main;
            if (mainCam == null)
            {
                Vector3 fallback = transform.forward;
                fallback.y = 0f;
                return fallback.sqrMagnitude > 0.0001f ? fallback.normalized : Vector3.forward;
            }

            Vector3 origin = GetBaseAnchorWorldPosition();
            Ray ray = mainCam.ScreenPointToRay(Input.mousePosition);
            Plane groundPlane = new Plane(Vector3.up, origin);

            if (groundPlane.Raycast(ray, out float distance) && distance > 0f)
            {
                Vector3 mouseWorldPos = ray.GetPoint(distance);
                Vector3 toMouse = mouseWorldPos - origin;
                toMouse.y = 0f;
                if (toMouse.sqrMagnitude > 0.0001f)
                    return toMouse.normalized;
            }

            Vector3 forward = transform.forward;
            forward.y = 0f;
            return forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;
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

        private IEnumerator RestoreCollisionAfterDelay(Collider[] objectColliders, float delaySeconds)
        {
            if (delaySeconds > 0f)
                yield return new WaitForSeconds(delaySeconds);

            SetIgnorePlayerCollision(objectColliders, false);
            _restoreCollisionCoroutine = null;
        }

        private void OnDisable()
        {
            if (_held != null && _heldColliders != null && DisableHeldColliders && _heldColliderEnabledStates != null)
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

            if (_held != null)
                _held.IsHeld = false;

            _held = null;
            _heldRigidbody = null;
            _heldColliders = null;
            _heldColliderEnabledStates = null;
        }
    }
}

