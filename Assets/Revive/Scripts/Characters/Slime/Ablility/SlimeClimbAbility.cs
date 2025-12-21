using MoreMountains.Tools;
using MoreMountains.TopDownEngine;
using Slime;
using UnityEngine;

namespace Revive
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

        protected TopDownController3D _controller3D;
        protected CharacterMovement _characterMovement;
        protected Collider _selfCollider;
        protected bool _isClimbing;
        protected Vector3 _climbNormal;
        protected SlimeColliderInfo _climbInfo;
        protected Collider[] _climbOverlapHits = new Collider[32];

        protected const string _climbingAnimationParameterName = "Climbing";
        protected int _climbingAnimationParameter;

        public bool IsClimbing => _isClimbing;
        public Vector3 ClimbNormal => _climbNormal;

        protected override void Initialization()
        {
            base.Initialization();
            _controller3D = _controller as TopDownController3D;
            _characterMovement = _character?.FindAbility<CharacterMovement>();
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
            Vector3 moveDir = _controller3D.CurrentDirection.normalized;
            float verticalInput = _inputManager != null ? _inputManager.PrimaryMovement.y : 0f;
            
            // DEBUG: 每秒输出一次输入状态
            if (Time.frameCount % 60 == 0)
            {
                Debug.Log($"[SlimeClimb] moveDir={moveDir}, verticalInput={verticalInput}, selfCollider={_selfCollider != null}");
            }
            
            DetectClimbableSurface(moveDir);

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
                ApplyClimbMovement(moveDir, verticalInput);
            }
        }

        protected virtual void DetectClimbableSurface(Vector3 moveDir)
        {
            float bestDist = float.MaxValue;
            float bestDot = float.MaxValue;
            Vector3 bestNormal = Vector3.zero;
            SlimeColliderInfo bestInfo = null;

            Vector3 queryCenter = _selfCollider != null ? _selfCollider.bounds.center : transform.position;
            float maxSurfaceDist = _isClimbing ? Mathf.Max(ClimbDetachDistance, ClimbContactDistance) : ClimbContactDistance;
            float searchRadius = Mathf.Max(ClimbDetectDistance, maxSurfaceDist);
            
            int count = Physics.OverlapSphereNonAlloc(queryCenter, searchRadius, _climbOverlapHits, ClimbableLayers);
            
            // DEBUG: 每秒输出一次检测结果
            if (Time.frameCount % 60 == 0)
            {
                Debug.Log($"[SlimeClimb] 检测: center={queryCenter}, radius={searchRadius}, hitCount={count}");
            }
            
            int climbableCount = 0;
            for (int i = 0; i < count; i++)
            {
                var col = _climbOverlapHits[i];
                if (col == null)
                    continue;

                var info = col.GetComponentInParent<SlimeColliderInfo>();
                if (info == null || info.colliderType != SlimeColliderInfo.ColliderType.Climbable)
                    continue;
                
                climbableCount++;

                Vector3 wallPoint = col.ClosestPoint(queryCenter);
                Vector3 selfPoint = queryCenter;
                if (_selfCollider != null)
                    selfPoint = _selfCollider.ClosestPoint(wallPoint);

                Vector3 delta = selfPoint - wallPoint;
                float dist = delta.magnitude;
                
                // DEBUG: 输出每个 Climbable 碰撞体的详细信息
                if (Time.frameCount % 60 == 0)
                {
                    Debug.Log($"[SlimeClimb] Climbable '{col.name}': dist={dist:F3}, maxSurfaceDist={maxSurfaceDist:F3}");
                }
                
                if (dist > maxSurfaceDist)
                    continue;

                Vector3 normal = dist < 0.0001f ? (queryCenter - wallPoint) : (delta / dist);
                normal.y = 0f;
                float normalSqr = normal.sqrMagnitude;
                if (normalSqr < 0.0001f)
                    continue;
                normal /= Mathf.Sqrt(normalSqr);
                
                float dot = Vector3.Dot(moveDir, normal);
                
                // DEBUG: 输出角度检查
                if (Time.frameCount % 60 == 0)
                {
                    Debug.Log($"[SlimeClimb] Climbable '{col.name}': normal={normal}, dot={dot:F3} (需要<=0.35)");
                }
                
                if (dot > 0.35f)
                    continue;

                if (dot < bestDot || (Mathf.Abs(dot - bestDot) < 0.0001f && dist < bestDist))
                {
                    bestDot = dot;
                    bestDist = dist;
                    bestNormal = normal;
                    bestInfo = info;
                }
            }

            // DEBUG: 输出可攀爬碰撞体数量
            if (Time.frameCount % 60 == 0 && climbableCount > 0)
            {
                Debug.Log($"[SlimeClimb] 找到 {climbableCount} 个 Climbable 碰撞体, bestInfo={bestInfo != null}");
            }

            if (bestInfo == null)
            {
                _isClimbing = false;
                return;
            }

            Debug.Log($"[SlimeClimb] 开始攀爬! normal={bestNormal}, dist={bestDist:F3}");
            _isClimbing = true;
            _climbNormal = bestNormal;
            _climbInfo = bestInfo;
        }

        protected virtual void ApplyClimbMovement(Vector3 moveDir, float verticalInput)
        {
            if (_controller3D == null || _climbInfo == null)
                return;

            // 禁用重力并强制非着地状态（否则垂直速度会被限制为<=0）
            _controller3D.GravityActive = false;
            _controller3D.Grounded = false;

            // 计算攀爬速度（与原始 ControllerTest 逻辑一致）
            float pushIntoWall = Mathf.Clamp01(-Vector3.Dot(moveDir, _climbNormal));
            float climbInput = Mathf.Max(0f, verticalInput);
            float climbStrength = Mathf.Max(pushIntoWall, climbInput);
            
            // 计算攀爬速度（使用正常移动速度）
            if (climbStrength > 0.1f)
            {
                float baseSpeed = _characterMovement != null ? _characterMovement.WalkSpeed : 4f;
                float targetY = baseSpeed * climbStrength * (1f - Mathf.Clamp01(_climbInfo.surfaceFriction)) * ClimbSpeedMultiplier;
                
                // 直接设置垂直速度，与正常移动速度一致
                _controller3D.AddForce(new Vector3(0, targetY, 0));
            }
        }

        protected virtual void OnClimbStart()
        {
            PlayAbilityStartFeedbacks();
        }

        protected virtual void OnClimbEnd()
        {
            if (_controller3D != null)
            {
                _controller3D.GravityActive = true;
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

        protected virtual void OnDisable()
        {
            if (_isClimbing)
            {
                OnClimbEnd();
                _isClimbing = false;
            }
        }
    }
}
