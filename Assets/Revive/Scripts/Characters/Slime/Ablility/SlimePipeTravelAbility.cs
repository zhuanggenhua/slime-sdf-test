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

        [Header("Debug")]
        [SerializeField, Min(1)]
        private int debugTravelLogIntervalFrames = 30;

        private int _dbgTravelLogFrame = -999999;

        private void DebugLogState(string phase, float dt, float effectiveSpeed, Vector3 targetPos)
        {
            if (!Debug.isDebugBuild)
                return;
            if (Time.frameCount - _dbgTravelLogFrame < debugTravelLogIntervalFrames)
                return;
            _dbgTravelLogFrame = Time.frameCount;

            float len = _path != null ? _path.GetLength() : 0f;
            Vector3 pos = transform.position;
            Debug.Log(
                $"[SlimePipeTravelDbg] phase={phase} frame={Time.frameCount} dt={dt:F3} effSpeed={effectiveSpeed:F2} " +
                $"t={_t:F3} reverse={_reverse} aligned={_alignedToPath} exitPush={_isExitPushing} exitEase={_isExitEasingOut} " +
                $"len={len:F2} pos=({pos.x:F2},{pos.y:F2},{pos.z:F2}) target=({targetPos.x:F2},{targetPos.y:F2},{targetPos.z:F2}) flags={_lastMoveFlags}",
                this);
        }

        private bool _isExitPushing;
        private bool _isExitEasingOut;
        private Vector3 _exitPushDir;
        private float _exitPushStepDistance;
        private int _exitPushStepsRemaining;
        private bool _pendingStopTravel;
        private float _exitPreAdvanceDistance;

        private float _exitPushSpeed;
        private float _exitEaseTimeRemaining;

        private CollisionFlags _lastMoveFlags;

        private float _travelElapsed;

        private float _speedRampSeconds = 1.0f;

        private float _cooldownUntilTime;
        private CharacterController _cachedCharacterController;
        private bool _prevDetectCollisions;
        private bool _prevEnableOverlapRecovery;
        private bool _prevCharacterControllerEnabled;

        private const float TravelReentryCooldownSeconds = 0.35f;
        private const float SamePathReentryCooldownSeconds = 1.0f;
        private const float ExitPushDistance = 0.5f;
        private const float ExitPushUp = 0.05f;
        private const float ExitEaseOutSeconds = 0.25f;

        private SlimePipePath _lastExitPath;
        private float _lastExitTime;

        // 保存的状态
        private CharacterStates.CharacterConditions _prevCondition;
        private bool _prevFreeMovement;
        private bool _prevGravityActive;
        private bool _prevInputAuthorized;

        public bool IsTravelling => _isTravelling;

        public Transform CurrentPathTransform => _path != null ? _path.CollisionIgnoreRoot : null;

        public bool CanStartTravel => !_isTravelling && Time.time >= _cooldownUntilTime;

        public bool CanStartTravelFromPath(SlimePipePath path)
        {
            if (!CanStartTravel)
                return false;

            if (path != null && ReferenceEquals(path, _lastExitPath) && Time.time < (_lastExitTime + SamePathReentryCooldownSeconds))
                return false;

            return true;
        }

        public override void ProcessAbility()
        {
            base.ProcessAbility();

            if (_pendingStopTravel)
            {
                _pendingStopTravel = false;
                StopTravel();
            }

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
            float ramp01 = _speedRampSeconds > 1e-4f ? Mathf.Clamp01(_travelElapsed / _speedRampSeconds) : 1f;
            ramp01 = ramp01 * ramp01 * (3f - 2f * ramp01);
            float effectiveSpeed = _speed * ramp01;

            if (_path != null)
            {
                Vector3 dbgTarget = _path.EvaluatePosition(_t);
                DebugLogState("tick_begin", dt, effectiveSpeed, dbgTarget);
            }

            if (_isExitPushing)
            {
                float pushSpeed = _exitPushSpeed;
                float step = _exitPushStepsRemaining > 0 ? _exitPushStepDistance : 0f;
                if (step > 0f && _exitPushStepsRemaining > 0)
                {
                    transform.position += _exitPushDir * step;
                    _exitPushStepsRemaining -= 1;
                }

                float actualSpeed = dt > 0f ? step / dt : 0f;
                _lastTravelVelocity = _exitPushDir * actualSpeed;
                if (_controller3D != null)
                {
                    _controller3D.Velocity = _lastTravelVelocity;
                }

                if (_exitPushStepsRemaining <= 0)
                {
                    _isExitPushing = false;
                    _isExitEasingOut = true;
                    _exitEaseTimeRemaining = ExitEaseOutSeconds;
                }

                DebugLogState("exit_push", dt, effectiveSpeed, transform.position);

                return;
            }

            if (_isExitEasingOut)
            {
                float dtStep = Mathf.Min(dt, _exitEaseTimeRemaining);
                float r0 = Mathf.Clamp01(_exitEaseTimeRemaining / ExitEaseOutSeconds);
                float r1 = Mathf.Clamp01((_exitEaseTimeRemaining - dtStep) / ExitEaseOutSeconds);
                float v0 = _exitPushSpeed * r0;
                float v1 = _exitPushSpeed * r1;
                float vAvg = 0.5f * (v0 + v1);

                float step = vAvg * dtStep;
                if (step > 0f)
                {
                    transform.position += _exitPushDir * step;
                }

                _lastTravelVelocity = _exitPushDir * v1;
                if (_controller3D != null)
                {
                    _controller3D.Velocity = _lastTravelVelocity;
                }

                _exitEaseTimeRemaining -= dtStep;
                if (_exitEaseTimeRemaining <= 0f)
                {
                    _isExitEasingOut = false;
                    _lastTravelVelocity = Vector3.zero;
                    if (_controller3D != null)
                    {
                        _controller3D.Velocity = Vector3.zero;
                    }
                    _pendingStopTravel = true;
                }

                DebugLogState("exit_ease", dt, effectiveSpeed, transform.position);
                return;
            }

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
                    UpdateMovement(dt, effectiveSpeed, out var alignTargetPos);
                    if ((alignTargetPos - transform.position).sqrMagnitude <= 0.0001f)
                        _alignedToPath = true;

                    DebugLogState("aligning", dt, effectiveSpeed, alignTargetPos);
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

                    Vector3 endTargetPos = _path.EvaluatePosition(_t);
                    Vector3 deltaToEnd = endTargetPos - transform.position;
                    float maxMove = Mathf.Max(0f, effectiveSpeed) * Mathf.Max(0f, dt);

                    bool reachedEnd;
                    if (deltaToEnd.magnitude <= maxMove && maxMove > 1e-6f)
                    {
                        Vector3 tangent = _path.EvaluateTangent(_t);
                        if (_reverse)
                        {
                            tangent = -tangent;
                        }
                        if (tangent.sqrMagnitude < 1e-6f)
                        {
                            tangent = _lastTravelVelocity.sqrMagnitude > 1e-6f ? _lastTravelVelocity : _lastForward;
                        }

                        Vector3 dir = tangent.normalized;

                        float deltaMag = deltaToEnd.magnitude;
                        _exitPreAdvanceDistance = Mathf.Max(0f, maxMove - deltaMag);

                        Vector3 move = dir * maxMove;
                        transform.position += move;
                        _lastMoveFlags = CollisionFlags.None;

                        Vector3 velocity = dt > 0f ? move / dt : Vector3.zero;
                        _lastTravelVelocity = velocity;
                        if (_controller3D != null)
                        {
                            _controller3D.Velocity = velocity;
                        }

                        ApplyRotation();
                        reachedEnd = true;
                    }
                    else
                    {
                        _exitPreAdvanceDistance = 0f;
                        reachedEnd = UpdateMovement(dt, effectiveSpeed, out endTargetPos); // 最终位置/朝向
                    }

                    if (reachedEnd)
                    {
                        StartExitPush();
                    }
                    return;
                }
            }

            UpdateMovement(dt, effectiveSpeed, out var travelTargetPos);

            DebugLogState("travelling", dt, effectiveSpeed, travelTargetPos);
        }

        private bool UpdateMovement(float dt, float speed, out Vector3 targetPos)
        {
            targetPos = _path.EvaluatePosition(_t);
            Vector3 deltaToTarget = targetPos - transform.position;

            float maxMove = Mathf.Max(0f, speed) * Mathf.Max(0f, dt);
            Vector3 move = maxMove > 0f ? Vector3.ClampMagnitude(deltaToTarget, maxMove) : Vector3.zero;

            if (_cachedCharacterController != null && _cachedCharacterController.enabled)
            {
                _lastMoveFlags = _cachedCharacterController.Move(move);
            }
            else
            {
                transform.position += move;
                _lastMoveFlags = CollisionFlags.None;
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

            if (_reverse)
            {
                tangent = -tangent;
            }

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

        public void StartTravel(SlimePipePath path, float startT, bool reverse, TravelRotationMode rotationMode)
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
            {
                if (Debug.isDebugBuild)
                    Debug.LogWarning($"[SlimePipeTravelAbility] StartTravel aborted: already travelling character={_character?.name} path={path.name}", this);
                return;
            }

            if (Time.time < _cooldownUntilTime)
            {
                if (Debug.isDebugBuild)
                    Debug.LogWarning($"[SlimePipeTravelAbility] StartTravel aborted: cooldown character={_character?.name} path={path.name} cooldownRemain={( _cooldownUntilTime - Time.time):F2}s", this);
                return;
            }

            _path = path;
            _t = Mathf.Clamp01(startT);
            _reverse = reverse;
            _speed = Mathf.Max(0f, path.DefaultSpeed);
            _speedRampSeconds = Mathf.Max(0f, path.SpeedRampSeconds);
            _rotationMode = rotationMode;
            _alignedToPath = false;
            _travelElapsed = 0f;
            _isExitPushing = false;
            _isExitEasingOut = false;
            _pendingStopTravel = false;
            _exitPushDir = Vector3.zero;
            _exitPushStepDistance = 0f;
            _exitPushStepsRemaining = 0;
            _exitPreAdvanceDistance = 0f;
            _exitPushSpeed = 0f;
            _exitEaseTimeRemaining = 0f;
            _lastMoveFlags = CollisionFlags.None;

            _cachedCharacterController = _controller3D != null ? _controller3D.GetComponent<CharacterController>() : null;
            if (_cachedCharacterController != null)
            {
                _prevCharacterControllerEnabled = _cachedCharacterController.enabled;
                _prevDetectCollisions = _cachedCharacterController.detectCollisions;
                _prevEnableOverlapRecovery = _cachedCharacterController.enableOverlapRecovery;
                _cachedCharacterController.detectCollisions = false;
                _cachedCharacterController.enableOverlapRecovery = false;
                _cachedCharacterController.enabled = false;
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

            if (Debug.isDebugBuild)
            {
                Debug.Log(
                    $"[SlimePipeTravel] Start frame={Time.frameCount} character={_character?.name} path={path.name} " +
                    $"t0={_t:F3} reverse={_reverse} speed={_speed:F2} ramp={_speedRampSeconds:F2} rot={_rotationMode} closed={path.ClosedLoop} len={path.GetLength():F2}",
                    this);
            }
        }

        private void StopTravel()
        {
            if (!_isTravelling)
                return;

            if (Debug.isDebugBuild && _path != null)
            {
                Debug.Log(
                    $"[SlimePipeTravel] Stop frame={Time.frameCount} character={_character?.name} path={_path.name} t={_t:F3} pos=({transform.position.x:F2},{transform.position.y:F2},{transform.position.z:F2})",
                    this);
            }

            _isTravelling = false;
            _alignedToPath = false;
            _travelElapsed = 0f;
            _cooldownUntilTime = Time.time + TravelReentryCooldownSeconds;

            _isExitPushing = false;
            _isExitEasingOut = false;
            _pendingStopTravel = false;

            if (_cachedCharacterController != null)
            {
                var cc = _cachedCharacterController;
                cc.enabled = _prevCharacterControllerEnabled;
                cc.detectCollisions = _prevDetectCollisions;
                cc.enableOverlapRecovery = _prevEnableOverlapRecovery;

                if (cc.enabled)
                {
                    cc.Move(Vector3.zero);
                }

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

            _lastExitPath = _path;
            _lastExitTime = Time.time;
            _path = null;
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            StopTravel();
        }

        private void StartExitPush()
        {
            if (_path == null)
            {
                StopTravel();
                return;
            }

            Vector3 tangent = _path.EvaluateTangent(_t);
            if (_reverse)
            {
                tangent = -tangent;
            }
            if (tangent.sqrMagnitude < 1e-6f)
            {
                tangent = _lastTravelVelocity.sqrMagnitude > 1e-6f ? _lastTravelVelocity : _lastForward;
            }

            Vector3 dir = tangent.normalized;

            Vector3 push = dir * ExitPushDistance + Vector3.up * ExitPushUp;
            if (_exitPreAdvanceDistance > 0f)
            {
                float dirDist = Mathf.Max(0f, ExitPushDistance - _exitPreAdvanceDistance);
                push = dir * dirDist + Vector3.up * ExitPushUp;
                _exitPreAdvanceDistance = 0f;
            }
            float pushDist = push.magnitude;
            if (pushDist <= 1e-4f)
            {
                StopTravel();
                return;
            }

            if (Debug.isDebugBuild)
            {
                Debug.Log(
                    $"[SlimePipeTravel] ExitPush frame={Time.frameCount} character={_character?.name} path={_path.name} t={_t:F3} pushDist={pushDist:F3} preAdvance={_exitPreAdvanceDistance:F3}",
                    this);
            }

            float baseSpeed = Mathf.Max(_speed, 0.5f);
            float easeOutDist = baseSpeed * ExitEaseOutSeconds * 0.5f;
            float constDist = Mathf.Max(0f, pushDist - easeOutDist);

            _isExitPushing = true;
            _isExitEasingOut = false;
            _pendingStopTravel = false;
            _exitPushDir = push / pushDist;
            _exitPushSpeed = baseSpeed;

            if (constDist <= 1e-4f)
            {
                _isExitPushing = false;
                _isExitEasingOut = true;
                _exitEaseTimeRemaining = ExitEaseOutSeconds;
                _exitPushStepDistance = 0f;
                _exitPushStepsRemaining = 0;
                return;
            }

            float stepDist = Mathf.Max(1e-4f, baseSpeed * Time.fixedDeltaTime);
            int steps = Mathf.Max(1, Mathf.CeilToInt(constDist / stepDist));
            _exitPushStepDistance = constDist / steps;
            _exitPushStepsRemaining = steps;
        }
    }
}
