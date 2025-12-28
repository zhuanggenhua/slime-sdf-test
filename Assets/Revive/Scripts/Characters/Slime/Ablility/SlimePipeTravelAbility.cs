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
        private float _maxSpeed;
        private TravelRotationMode _rotationMode;
        private bool _isTravelling;
        private Vector3 _lastForward = Vector3.forward;

        // 保存的状态
        private CharacterStates.CharacterConditions _prevCondition;
        private bool _prevFreeMovement;
        private bool _prevGravityActive;
        private bool _prevInputAuthorized;

        public override void ProcessAbility()
        {
            base.ProcessAbility();

            if (!_isTravelling || _path == null)
            {
                return;
            }

            float dt = Time.deltaTime;
            if (dt <= 0f)
                return;

            var length = _path.GetLength();
            if (length <= 0f)
            {
                StopTravel();
                return;
            }

            float currentSpeed = _speed;
            if (_maxSpeed > 0f)
                currentSpeed = Mathf.Min(currentSpeed, _maxSpeed);

            float deltaT = (currentSpeed * dt) / Mathf.Max(length, 0.0001f);
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
                    UpdateMovement(dt); // 最终位置/朝向
                    StopTravel();
                    return;
                }
            }

            UpdateMovement(dt);
        }

        private void UpdateMovement(float dt)
        {
            Vector3 targetPos = _path.EvaluatePosition(_t);
            Vector3 delta = targetPos - transform.position;

            var cc = _controller3D != null ? _controller3D.GetComponent<CharacterController>() : null;
            if (cc != null)
            {
                cc.Move(delta);
            }
            else
            {
                transform.position = targetPos;
            }

            Vector3 velocity = dt > 0f ? delta / dt : Vector3.zero;
            if (_controller3D != null)
            {
                _controller3D.Velocity = velocity;
            }

            ApplyRotation();
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

        public void StartTravel(SlimePipePath path, float startT, bool reverse, float speed, float maxSpeed, TravelRotationMode rotationMode)
        {
            if (path == null || !path.TryGetSpline(out _))
                return;

            _path = path;
            _t = Mathf.Clamp01(startT);
            _reverse = reverse;
            _speed = Mathf.Max(0f, speed);
            _maxSpeed = maxSpeed;
            _rotationMode = rotationMode;
            if (_rotationMode == TravelRotationMode.YawOnly && path != null)
            {
                _rotationMode = path.RotationModeDefault;
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

            if (_controller3D != null)
            {
                _controller3D.FreeMovement = _prevFreeMovement;
                _controller3D.GravityActive = _prevGravityActive;
                _controller3D.Velocity = Vector3.zero;
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
