using MoreMountains.Tools;
using MoreMountains.TopDownEngine;
using UnityEngine;

namespace Revive.Slime
{
    /// <summary>
    /// 史莱姆攀爬能力 - 检测可攀爬表面，修改移动速度实现攀爬
    /// 移植自 ControllerTest.HandleClimbing
    /// </summary>
    [AddComponentMenu("TopDown Engine/Character/Abilities/SlimeClimbAbility")]
    public class SlimeClimbAbility : CharacterAbility
    {
        [Header("Climb Settings")]
        [Tooltip("攀爬检测距离")]
        [Range(0.1f, 2f)]
        public float ClimbDetectDistance = 0.8f;
        
        [Tooltip("攀爬接触距离")]
        [Range(0f, 1f)]
        public float ClimbContactDistance = 0.3f;
        
        [Tooltip("攀爬脱离距离")]
        [Range(0f, 1f)]
        public float ClimbDetachDistance = 0.15f;
        
        [Tooltip("攀爬检测Layer")]
        public LayerMask ClimbableLayers = ~0;
        
        [Tooltip("攀爬速度倍率")]
        [Range(0.1f, 2f)]
        public float ClimbSpeedMultiplier = 1f;

        [Range(0f, 1f)]
        public float ClimbEnterIntoWallDot = 0.05f;

        public float ClimbEnterDirectionMemory = 0.3f;

        public float ClimbSlideSpeedMultiplier = 0.5f;

        public float ClimbVerticalAcceleration = 20f;

        protected Collider _selfCollider;
        protected bool _isClimbing;
        protected Vector3 _climbNormal;
        protected SlimeColliderInfo _climbInfo;
        protected Collider[] _climbOverlapHits = new Collider[32];

        protected float _storedGravity;
        protected bool _storedGravityActive;
        protected bool _hasStoredGravity;

        protected Vector3 _lastHorizontalEnterDir;
        protected float _lastHorizontalEnterDirTime;

        protected const string _climbingAnimationParameterName = "Climbing";
        protected int _climbingAnimationParameter;

        public bool IsClimbing => _isClimbing;
        public Vector3 ClimbNormal => _climbNormal;

        protected override void Initialization()
        {
            base.Initialization();
            _selfCollider = gameObject.GetComponentInChildren<Collider>();
        }

        public override void ProcessAbility()
        {
            base.ProcessAbility();

            if (_controller3D == null)
            {
                Debug.LogWarning("[SlimeClimb] _controller3D is null");
                return;
            }

            bool wasClimbing = _isClimbing;
            Vector2 primaryMovement = _inputManager != null ? _inputManager.PrimaryMovement : Vector2.zero;
            Vector3 moveDir = new Vector3(primaryMovement.x, 0f, primaryMovement.y);
            Vector3 velocityDir = _controller3D.Velocity;
            velocityDir.y = 0f;

            if (velocityDir.sqrMagnitude > 0.000001f)
            {
                _lastHorizontalEnterDir = velocityDir.normalized;
                _lastHorizontalEnterDirTime = Time.time;
            }
            else if (moveDir.sqrMagnitude > 0.0001f)
            {
                _lastHorizontalEnterDir = moveDir.normalized;
                _lastHorizontalEnterDirTime = Time.time;
            }

            Vector3 detectDir;
            if (velocityDir.sqrMagnitude > 0.000001f)
            {
                detectDir = velocityDir.normalized;
            }
            else if (Time.time - _lastHorizontalEnterDirTime <= Mathf.Max(0f, ClimbEnterDirectionMemory) && _lastHorizontalEnterDir.sqrMagnitude > 0.0001f)
            {
                detectDir = _lastHorizontalEnterDir;
            }
            else if (moveDir.sqrMagnitude > 0.0001f)
            {
                detectDir = moveDir.normalized;
            }
            else
            {
                detectDir = Vector3.zero;
            }

            Vector3 maintainDir = Vector3.zero;
            bool hasMaintainDir = false;
            if (moveDir.sqrMagnitude > 0.0001f)
            {
                maintainDir = moveDir.normalized;
                hasMaintainDir = true;
            }
            float verticalInput = primaryMovement.y;
            
            DetectClimbableSurface(detectDir, maintainDir, hasMaintainDir);

            if (_isClimbing && !wasClimbing)
            {
                OnClimbStart();
            }
            else if (!_isClimbing && wasClimbing)
            {
                OnClimbEnd();
            }

            if (_isClimbing)
            {
                ApplyClimbMovement(maintainDir, hasMaintainDir, verticalInput);
            }
        }

        protected virtual void DetectClimbableSurface(Vector3 enterDir, Vector3 maintainDir, bool hasMaintainDir)
        {
            Vector3 dirForDot = _isClimbing ? maintainDir : enterDir;
            bool applyDotFilter = !_isClimbing || hasMaintainDir;

            if (!_isClimbing && (enterDir.sqrMagnitude < 0.0001f))
            {
                applyDotFilter = false;
                dirForDot = Vector3.zero;
            }

            float bestDist = float.MaxValue;
            float bestDot = float.MaxValue;
            Vector3 bestNormal = Vector3.zero;
            SlimeColliderInfo bestInfo = null;

            Vector3 queryCenter = _selfCollider != null ? _selfCollider.bounds.center : transform.position;
            float maxSurfaceDist = _isClimbing ? Mathf.Max(ClimbDetachDistance, ClimbContactDistance) : ClimbContactDistance;
            float searchRadius = Mathf.Max(ClimbDetectDistance, maxSurfaceDist);
            
            int count = UnityEngine.Physics.OverlapSphereNonAlloc(queryCenter, searchRadius, _climbOverlapHits, ClimbableLayers);
            for (int i = 0; i < count; i++)
            {
                var col = _climbOverlapHits[i];
                if (col == null)
                    continue;

                var info = col.GetComponentInParent<SlimeColliderInfo>();
                if (info == null || info.colliderType != SlimeColliderInfo.ColliderType.Climbable)
                    continue;

                Vector3 wallPoint = col.ClosestPoint(queryCenter);
                Vector3 selfPoint = queryCenter;
                if (_selfCollider != null)
                    selfPoint = _selfCollider.ClosestPoint(wallPoint);

                Vector3 delta = selfPoint - wallPoint;
                float dist = delta.magnitude;
                if (dist > maxSurfaceDist)
                {
                    continue;
                }

                Vector3 normal = dist < 0.0001f ? (queryCenter - wallPoint) : (delta / dist);
                normal.y = 0f;
                float normalSqr = normal.sqrMagnitude;
                if (normalSqr < 0.0001f)
                {
                    continue;
                }
                normal /= Mathf.Sqrt(normalSqr);
                
                float dot = Vector3.Dot(dirForDot, normal);
                if (applyDotFilter)
                {
                    if (!_isClimbing)
                    {
                        if (dot > -Mathf.Clamp01(ClimbEnterIntoWallDot))
                        {
                            continue;
                        }
                    }
                    else
                    {
                        if (dot > 0.35f)
                        {
                            continue;
                        }
                    }
                }

                if (dot < bestDot || (Mathf.Abs(dot - bestDot) < 0.0001f && dist < bestDist))
                {
                    bestDot = dot;
                    bestDist = dist;
                    bestNormal = normal;
                    bestInfo = info;
                }
            }

            if (bestInfo == null)
            {
                _isClimbing = false;
                return;
            }

            _isClimbing = true;
            _climbNormal = bestNormal;
            _climbInfo = bestInfo;
        }

        protected virtual void ApplyClimbMovement(Vector3 moveDir, bool hasMoveDir, float verticalInput)
        {
            if (_controller3D == null || _climbInfo == null)
                return;

            // 禁用重力并强制非着地状态（否则垂直速度会被限制为<=0）
            if (!_hasStoredGravity)
            {
                _storedGravity = _controller3D.Gravity;
                _storedGravityActive = _controller3D.GravityActive;
                _hasStoredGravity = true;
            }

            _controller3D.GravityActive = true;
            _controller3D.Gravity = 0f;
            _controller3D.Grounded = false;

            float baseSpeed = _characterMovement != null ? _characterMovement.WalkSpeed : 4f;
            float frictionFactor = 1f - Mathf.Clamp01(_climbInfo.surfaceFriction);
            float upSpeed = baseSpeed * frictionFactor * ClimbSpeedMultiplier;
            float downSpeed = baseSpeed * frictionFactor * ClimbSpeedMultiplier;
            float slideSpeed = downSpeed * ClimbSlideSpeedMultiplier;

            float pushIntoWall = 0f;
            if (hasMoveDir)
            {
                pushIntoWall = Mathf.Clamp01(-Vector3.Dot(moveDir, _climbNormal));
            }

            float targetVy;
            if (verticalInput > 0.1f)
            {
                targetVy = upSpeed * Mathf.Clamp01(verticalInput);
            }
            else if (verticalInput < -0.1f)
            {
                targetVy = -downSpeed * Mathf.Clamp01(-verticalInput);
            }
            else if (pushIntoWall > 0.1f)
            {
                targetVy = upSpeed * pushIntoWall;
            }
            else
            {
                targetVy = -slideSpeed;
            }

            float currentVy = _controller3D.Velocity.y;
            float dt = _controller3D.UpdateMode == TopDownController3D.UpdateModes.FixedUpdate ? Time.fixedDeltaTime : Time.deltaTime;
            float newVy = Mathf.MoveTowards(currentVy, targetVy, ClimbVerticalAcceleration * dt);
            _controller3D.Velocity = new Vector3(_controller3D.Velocity.x, newVy, _controller3D.Velocity.z);
        }

        protected virtual void OnClimbStart()
        {
            PlayAbilityStartFeedbacks();
        }

        protected virtual void OnClimbEnd()
        {
            if (_controller3D != null)
            {
                if (_hasStoredGravity)
                {
                    _controller3D.Gravity = _storedGravity;
                    _controller3D.GravityActive = _storedGravityActive;
                    _hasStoredGravity = false;
                }
            }

            StopStartFeedbacks();
            PlayAbilityStopFeedbacks();
        }

        protected override void InitializeAnimatorParameters()
        {
            RegisterAnimatorParameter(_climbingAnimationParameterName, AnimatorControllerParameterType.Bool, out _climbingAnimationParameter);
        }

        public override void UpdateAnimator()
        {
            MMAnimatorExtensions.UpdateAnimatorBool(_animator, _climbingAnimationParameter, _isClimbing, _character._animatorParameters, _character.RunAnimatorSanityChecks);
        }

        protected override void OnDisable()
        {
            if (_isClimbing)
            {
                OnClimbEnd();
                _isClimbing = false;
            }
            base.OnDisable();
        }
    }
}
