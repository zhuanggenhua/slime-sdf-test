using MoreMountains.TopDownEngine;
using UnityEngine;
using Revive.Environment;

namespace Revive.Slime
{
    /// <summary>
    /// 让史莱姆沿样条路径移动，并锁定玩家输入。
    /// </summary>
    [AddComponentMenu("TopDown Engine/Character/Abilities/Slime Pipe Travel")]
    public class SlimePipeTravelAbility : CharacterAbility
    {
        private SlimePipePath _path;
        private float _t;
        private bool _reverse;
        private float _speed;
        private TravelRotationMode _rotationMode;
        private bool _isTravelling;
        private bool _alignedToPath;
        private Vector3 _lastForward = Vector3.forward;
        private Vector3 _lastTravelVelocity;

        private float _travelElapsed;

        private const float SpeedRampSeconds = 1.0f;

        private float _cooldownUntilTime;
        private CharacterController _cachedCharacterController;
        private bool _prevDetectCollisions;
        private bool _prevEnableOverlapRecovery;

        private const float TravelReentryCooldownSeconds = 0.25f;

        // 保存的状态
        private CharacterStates.CharacterConditions _prevCondition;
        private bool _prevFreeMovement;
        private bool _prevGravityActive;
        private bool _prevInputAuthorized;

        public bool IsTravelling => _isTravelling;

        public bool CanStartTravel => !_isTravelling && Time.time >= _cooldownUntilTime;

        public override void ProcessAbility()
        {
            base.ProcessAbility();

            if (_isTravelling)
            {
                return;
            }
        }

        private void FixedUpdate()
        {
            if (!_isTravelling || _path == null)
            {
                return;
            }

            float dt = Time.fixedDeltaTime;
            if (dt <= 0f)
            {
                return;
            }

            _travelElapsed += dt;
            float ramp01 = SpeedRampSeconds > 1e-4f ? Mathf.Clamp01(_travelElapsed / SpeedRampSeconds) : 1f;
            ramp01 = ramp01 * ramp01 * (3f - 2f * ramp01);
            float effectiveSpeed = _speed * ramp01;

            if (!_isTravelling || _path == null)
            {
                return;
            }

            if (!_alignedToPath)
            {
                Vector3 startTargetPos = _path.EvaluatePosition(_t);
                Vector3 startDelta = startTargetPos - transform.position;
                if (startDelta.sqrMagnitude <= 0.0001f)
                {
                    _alignedToPath = true;
                }
                else
                {
                    if (UpdateMovement(dt, effectiveSpeed, out _))
                        _alignedToPath = true;
                    return;
                }
            }

            var length = _path.GetLength();
            if (length <= 0f)
            {
                StopTravel();
                return;
            }

            float deltaT = (effectiveSpeed * dt) / Mathf.Max(length, 0.0001f);
            _t += (_reverse ? -deltaT : deltaT);

            if (_path.ClosedLoop)
            {
                _t = Mathf.Repeat(_t, 1f);
            }
            else
            {
                if (_t >= 1f || _t <= 0f)
                {
                    _t = Mathf.Clamp01(_t);
                    bool reachedEnd = UpdateMovement(dt, effectiveSpeed, out _); // 最终位置/朝向
                    if (reachedEnd)
                    {
                        StopTravel();
                    }
                    return;
                }
            }

            UpdateMovement(dt, effectiveSpeed, out _);
        }

        private bool UpdateMovement(float dt, float speed, out Vector3 targetPos)
        {
            targetPos = _path.EvaluatePosition(_t);
            Vector3 deltaToTarget = targetPos - transform.position;

            float maxMove = Mathf.Max(0f, speed) * Mathf.Max(0f, dt);
            Vector3 move = maxMove > 0f ? Vector3.ClampMagnitude(deltaToTarget, maxMove) : Vector3.zero;

            if (_cachedCharacterController != null)
            {
                _cachedCharacterController.Move(move);
            }
            else
            {
                transform.position += move;
            }

            Vector3 velocity = dt > 0f ? move / dt : Vector3.zero;
            _lastTravelVelocity = velocity;
            if (_controller3D != null)
            {
                _controller3D.Velocity = velocity;
            }

            ApplyRotation();

            Vector3 remain = targetPos - transform.position;
            return remain.sqrMagnitude <= 0.0001f;
        }

        private void ApplyRotation()
        {
            Vector3 tangent = _path.EvaluateTangent(_t);
            Vector3 up = _path.EvaluateUp(_t);

            Quaternion targetRot;
            if (_rotationMode == TravelRotationMode.FollowFullTangent)
            {
                if (tangent.sqrMagnitude < 1e-6f)
                {
                    targetRot = transform.rotation;
                }
                else
                {
                    targetRot = Quaternion.LookRotation(tangent, up.sqrMagnitude > 0.01f ? up : Vector3.up);
                }
            }
            else
            {
                Vector3 forward = new Vector3(tangent.x, 0f, tangent.z);
                if (forward.sqrMagnitude < 1e-6f)
                {
                    forward = _lastForward;
                }
                else
                {
                    forward.Normalize();
                    _lastForward = forward;
                }
                targetRot = forward.sqrMagnitude > 0.001f
                    ? Quaternion.LookRotation(forward, Vector3.up)
                    : transform.rotation;
            }

            transform.rotation = targetRot;
        }

        public void StartTravel(SlimePipePath path, float startT, bool reverse, float speed, TravelRotationMode rotationMode)
        {
            if (path == null)
            {
                Debug.LogWarning($"[SlimePipeTravelAbility] StartTravel aborted: path=null character={_character?.name}", this);
                return;
            }

            if (!path.TryGetSpline(out _))
            {
                Debug.LogWarning($"[SlimePipeTravelAbility] StartTravel aborted: path.TryGetSpline=false character={_character?.name} path={path.name}", this);
                return;
            }

            if (_isTravelling)
                return;

            if (Time.time < _cooldownUntilTime)
                return;

            _path = path;
            _t = Mathf.Clamp01(startT);
            _reverse = reverse;
            _speed = Mathf.Max(0f, speed);
            _rotationMode = rotationMode;
            _alignedToPath = false;
            _travelElapsed = 0f;

            _cachedCharacterController = _controller3D != null ? _controller3D.GetComponent<CharacterController>() : null;
            if (_cachedCharacterController != null)
            {
                _prevDetectCollisions = _cachedCharacterController.detectCollisions;
                _prevEnableOverlapRecovery = _cachedCharacterController.enableOverlapRecovery;
                _cachedCharacterController.detectCollisions = false;
                _cachedCharacterController.enableOverlapRecovery = false;
            }

            // 保存原状态
            _prevCondition = _condition.CurrentState;
            _prevFreeMovement = _controller3D != null && _controller3D.FreeMovement;
            _prevGravityActive = _controller3D != null && _controller3D.GravityActive;
            _prevInputAuthorized = _characterMovement != null && _characterMovement.InputAuthorized;

            // 锁定控制
            _condition.ChangeState(CharacterStates.CharacterConditions.ControlledMovement);
            if (_characterMovement != null)
            {
                _characterMovement.InputAuthorized = false;
            }
            if (_controller3D != null)
            {
                _controller3D.FreeMovement = false;
            }

            _isTravelling = true;
        }

        private void StopTravel()
        {
            if (!_isTravelling)
                return;

            _isTravelling = false;
            _alignedToPath = false;
            _travelElapsed = 0f;
            _cooldownUntilTime = Time.time + TravelReentryCooldownSeconds;

            if (_cachedCharacterController != null)
            {
                _cachedCharacterController.detectCollisions = _prevDetectCollisions;
                _cachedCharacterController.enableOverlapRecovery = _prevEnableOverlapRecovery;
                _cachedCharacterController = null;
            }

            if (_controller3D != null)
            {
                _controller3D.FreeMovement = _prevFreeMovement;
                _controller3D.GravityActive = _prevGravityActive;
                _controller3D.Velocity = _prevFreeMovement ? _lastTravelVelocity : Vector3.zero;
            }

            if (_characterMovement != null)
            {
                _characterMovement.InputAuthorized = _prevInputAuthorized;
            }

            _condition.ChangeState(_prevCondition);
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            StopTravel();
        }
    }
}
