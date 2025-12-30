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
        
        /// <summary>
        /// 重写跳跃开始 - 使用瞬时冲量而非持续施力
        /// </summary>
        public override void JumpStart()
        {
            if (!EvaluateJumpConditions())
            {
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

            // 【关键】瞬时冲量：直接设置y速度
            _controller.Velocity = new Vector3(
                _controller.Velocity.x,
                JumpImpulse,
                _controller.Velocity.z
            );

            PlayAbilityStartSfx();
            PlayAbilityUsedSfx();
            PlayAbilityStartFeedbacks();
        }

        /// <summary>
        /// 重写处理 - 不再持续施力，只检测状态
        /// </summary>
        public override void ProcessAbility()
        {
            if (_controller.JustGotGrounded)
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
