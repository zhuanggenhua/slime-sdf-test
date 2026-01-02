using MoreMountains.TopDownEngine;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Revive.Slime
{
    /// <summary>
    /// 史莱姆跳跃能力 - 使用瞬时冲量跳跃
    /// 模拟原版 velocity.y += JumpImpulse 的效果
    /// </summary>
    [AddComponentMenu("TopDown Engine/Character/Abilities/Slime Jump")]
    public class SlimeJumpAbility : CharacterJump3D
    {
        [Header("Slime Jump Settings")]
        [Tooltip("瞬时跳跃速度（类似原版 velocity.y += 4）")]
        public float JumpImpulse = 4f;

        [SerializeField] private bool _debugSceneLabel = true;
        [SerializeField] private bool _debugOnlyWhenPlaying = true;
        [SerializeField] private Vector3 _debugWorldOffset = new Vector3(0f, 2f, 0f);

        [SerializeField, Min(1)] private int _debugLogIntervalFrames = 15;
        private int _dbgLogFrame = -999999;
        private int _dbgFailLogFrame = -999999;

        private void DebugLogThrottled(string message)
        {
            if (!Debug.isDebugBuild)
            {
                return;
            }

            if (Time.frameCount - _dbgLogFrame < _debugLogIntervalFrames)
            {
                return;
            }

            _dbgLogFrame = Time.frameCount;
            Debug.Log(message, this);
        }

        private void DebugLogFailThrottled(string message)
        {
            if (!Debug.isDebugBuild)
            {
                return;
            }

            if (Time.frameCount - _dbgFailLogFrame < _debugLogIntervalFrames)
            {
                return;
            }

            _dbgFailLogFrame = Time.frameCount;
            Debug.Log(message, this);
        }

        private void DebugLogState(string phase)
        {
            if (!Debug.isDebugBuild)
            {
                return;
            }

            bool grounded = _controller3D != null && _controller3D.Grounded;
            bool justGrounded = _controller3D != null && _controller3D.JustGotGrounded;
            bool tooSteep = _controller3D != null && _controller3D.TooSteep();
            bool exitedTooSteep = _controller3D != null && _controller3D.ExitedTooSteepSlopeThisFrame;
            float vy = _controller3D != null ? _controller3D.Velocity.y : 0f;
            float lastVy = _controller3D != null ? _controller3D.VelocityLastFrame.y : 0f;
            var mode = _controller3D != null ? _controller3D.UpdateMode : default;
            bool grav = _controller3D != null && _controller3D.GravityActive;
            var cond = _condition != null ? _condition.CurrentState : default;
            var move = _movement != null ? _movement.CurrentState : default;
            DebugLogThrottled(
                $"[SlimeJumpDbg] phase={phase} frame={Time.frameCount} grounded={grounded} justGrounded={justGrounded} tooSteep={tooSteep} exitedTooSteep={exitedTooSteep} " +
                $"jumpsLeft={NumberOfJumpsLeft}/{NumberOfJumps} vy={vy:F2} lastVy={lastVy:F2} grav={grav} mode={mode} cond={cond} move={move}");
        }

        protected override void HandleInput()
        {
            if (_controller3D != null
                && _controller3D.Grounded
                && (ResetJumpsOnTooSteepSlopes || !_controller3D.TooSteep())
                && NumberOfJumpsLeft < NumberOfJumps)
            {
                ResetNumberOfJumps();
            }

            base.HandleInput();
        }
        
        /// <summary>
        /// 重写跳跃开始 - 使用瞬时冲量而非持续施力
        /// </summary>
        public override void JumpStart()
        {
            if (!EvaluateJumpConditions())
            {
                DebugLogJumpFail();
                return;
            }

            if (NumberOfJumpsLeft != NumberOfJumps)
            {
                _doubleJumping = true;
            }
            
            NumberOfJumpsLeft = NumberOfJumpsLeft - 1;

            _movement.ChangeState(CharacterStates.MovementStates.Jumping);
            MMCharacterEvent.Trigger(_character, MMCharacterEventTypes.Jump);
            JumpStartFeedback?.PlayFeedbacks(this.transform.position);
            _jumpOrigin = this.transform.position;
            _jumpStopped = false;
            _jumpStartedAt = Time.time;
            _controller.Grounded = false;
            _buttonReleased = false;

            if (_controller3D != null)
            {
                _controller3D.DetachFromGround();
                _controller3D.Grounded = false;
            }

            _controller.GravityActive = true;
            _controller.AddedForce = new Vector3(0f, JumpImpulse, 0f);

            PlayAbilityStartSfx();
            PlayAbilityUsedSfx();
            PlayAbilityStartFeedbacks();
        }

        protected override bool EvaluateJumpConditions()
        {
            if (!AbilityAuthorized)
            {
                return false;
            }
            if (_characterButtonActivation != null)
            {
                if (_characterButtonActivation.AbilityAuthorized
                    && _characterButtonActivation.InButtonActivatedZone
                    && _characterButtonActivation.PreventJumpInButtonActivatedZone)
                {
                    return false;
                }
            }

            if (!CanJumpOnTooSteepSlopes)
            {
                if (_controller3D != null && _controller3D.TooSteep())
                {
                    return false;
                }
            }

            if (_characterCrouch != null)
            {
                if (_characterCrouch.InATunnel)
                {
                    return false;
                }
            }

            if (CeilingTest())
            {
                return false;
            }

            if (NumberOfJumpsLeft <= 0)
            {
                return false;
            }

            if (_movement.CurrentState == CharacterStates.MovementStates.Dashing)
            {
                return false;
            }
            return true;
        }

        private void DebugLogJumpFail()
        {
            if (!Debug.isDebugBuild)
            {
                return;
            }

            string reason = "unknown";

            if (!AbilityAuthorized)
            {
                reason = "AbilityAuthorized=false";
            }
            else if (_condition != null && _condition.CurrentState != CharacterStates.CharacterConditions.Normal)
            {
                reason = $"condition={_condition.CurrentState}";
            }
            else if (_characterButtonActivation != null
                     && _characterButtonActivation.AbilityAuthorized
                     && _characterButtonActivation.InButtonActivatedZone
                     && _characterButtonActivation.PreventJumpInButtonActivatedZone)
            {
                reason = "PreventJumpInButtonActivatedZone";
            }
            else if (!CanJumpOnTooSteepSlopes && _controller3D != null && _controller3D.TooSteep())
            {
                reason = "TooSteep";
            }
            else if (_characterCrouch != null && _characterCrouch.InATunnel)
            {
                reason = "InATunnel";
            }
            else if (CeilingTest())
            {
                reason = "CeilingTest";
            }
            else if (NumberOfJumpsLeft <= 0)
            {
                reason = $"NoJumpsLeft({NumberOfJumpsLeft}/{NumberOfJumps})";
            }
            else if (_movement != null && _movement.CurrentState == CharacterStates.MovementStates.Dashing)
            {
                reason = "Dashing";
            }

            bool grounded = _controller3D != null && _controller3D.Grounded;
            bool justGrounded = _controller3D != null && _controller3D.JustGotGrounded;
            bool tooSteep = _controller3D != null && _controller3D.TooSteep();
            bool exitedTooSteep = _controller3D != null && _controller3D.ExitedTooSteepSlopeThisFrame;
            float vy = _controller3D != null ? _controller3D.Velocity.y : 0f;
            float lastVy = _controller3D != null ? _controller3D.VelocityLastFrame.y : 0f;
            var mode = _controller3D != null ? _controller3D.UpdateMode : default;
            bool grav = _controller3D != null && _controller3D.GravityActive;

            DebugLogFailThrottled(
                $"[SlimeJumpFail] frame={Time.frameCount} reason={reason} grounded={grounded} justGrounded={justGrounded} tooSteep={tooSteep} exitedTooSteep={exitedTooSteep} " +
                $"jumpsLeft={NumberOfJumpsLeft}/{NumberOfJumps} vy={vy:F2} lastVy={lastVy:F2} grav={grav} mode={mode}");
        }

        /// <summary>
        /// 重写处理 - 不再持续施力，只检测状态
        /// </summary>
        public override void ProcessAbility()
        {
            DebugLogState("tick");

            if (_controller.JustGotGrounded)
            {
                ResetNumberOfJumps();
            }

            if (_controller3D != null
                && !ResetJumpsOnTooSteepSlopes
                && _controller3D.ExitedTooSteepSlopeThisFrame
                && _controller3D.Grounded)
            {
                ResetNumberOfJumps();
            }

            if (_controller3D != null
                && _controller3D.Grounded
                && (ResetJumpsOnTooSteepSlopes || !_controller3D.TooSteep())
                && _movement.CurrentState != CharacterStates.MovementStates.Jumping
                && NumberOfJumpsLeft < NumberOfJumps)
            {
                ResetNumberOfJumps();
            }

            if (!AbilityAuthorized
                || (_condition.CurrentState != CharacterStates.CharacterConditions.Normal))
            {
                return;
            }

            // 检测是否应该结束跳跃状态
            if (_movement.CurrentState == CharacterStates.MovementStates.Jumping)
            {
                // 开始下落时结束跳跃状态
                if (_controller.Velocity.y <= 0 && !_jumpStopped)
                {
                    JumpStop();
                    _movement.ChangeState(CharacterStates.MovementStates.Falling);
                }
                
                // 按比例跳跃：提前松开按钮
                if (_buttonReleased 
                    && !_jumpStopped
                    && JumpProportionalToPress 
                    && (Time.time - _jumpStartedAt > MinimumPressTime))
                {
                    // 削减上升速度
                    if (_controller.Velocity.y > 0)
                    {
                        _controller.Velocity = new Vector3(
                            _controller.Velocity.x,
                            _controller.Velocity.y * 0.5f,
                            _controller.Velocity.z
                        );
                    }
                    JumpStop();
                }
                
                // 撞到天花板
                if (_controller3D != null && _controller3D.CollidingAbove())
                {
                    JumpStop();
                }
            }
            
            // 落地后重置
            if (!_jumpStopped
                && ((_movement.CurrentState == CharacterStates.MovementStates.Idle)
                    || (_movement.CurrentState == CharacterStates.MovementStates.Walking)
                    || (_movement.CurrentState == CharacterStates.MovementStates.Running)
                    || (_movement.CurrentState == CharacterStates.MovementStates.Falling)))
            {
                JumpStop();
            }
        }

        /// <summary>
        /// 重写跳跃停止 - 不清零速度，让重力自然处理
        /// </summary>
        public override void JumpStop()
        {
            _jumpStopped = true;
            _buttonReleased = false;
            PlayAbilityStopSfx();
            StopAbilityUsedSfx();
            StopStartFeedbacks();
            PlayAbilityStopFeedbacks();
            JumpStopFeedback?.PlayFeedbacks(this.transform.position);
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (!_debugSceneLabel)
            {
                return;
            }

            if (_debugOnlyWhenPlaying && !Application.isPlaying)
            {
                return;
            }

            TopDownController3D controller = null;
            if (Application.isPlaying)
            {
                controller = _controller3D;
            }

            if (controller == null)
            {
                controller = GetComponent<TopDownController3D>();
                if (controller == null)
                {
                    controller = GetComponentInParent<TopDownController3D>();
                }
            }

            if (controller == null)
            {
                return;
            }

            Vector3 labelPos = transform.position + _debugWorldOffset;
            float speed = controller.Velocity.magnitude;

            var speedStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                richText = true,
                fontSize = 14,
            };
            speedStyle.normal.textColor = new Color(1f, 0.85f, 0.1f);

            var textStyle = new GUIStyle(EditorStyles.label)
            {
                richText = true,
                fontSize = 11,
            };
            textStyle.normal.textColor = Color.white;

            Handles.color = Color.white;
            Handles.Label(labelPos, $"<b>Speed={speed:F2}</b>", speedStyle);

            string text =
                $"JumpImpulse={JumpImpulse:F2}\n" +
                $"vY={controller.Velocity.y:F2}  lastY={controller.VelocityLastFrame.y:F2}\n" +
                $"vXZ={new Vector2(controller.Velocity.x, controller.Velocity.z).magnitude:F2}\n" +
                $"grounded={controller.Grounded}  justGrounded={controller.JustGotGrounded}\n" +
                $"gravityActive={controller.GravityActive}  mode={controller.UpdateMode}";

            Handles.Label(labelPos + new Vector3(0f, -0.18f, 0f), text, textStyle);
        }
#endif
    }
}
