using MoreMountains.Feedbacks;
using MoreMountains.TopDownEngine;
using UnityEngine;

namespace Revive.Slime
{
    /// <summary>
    /// 史莱姆移动能力 - 继承 CharacterMovement，自动绑定 Slime_PBF
    /// 替换 CharacterMovement 使用
    /// </summary>
    [AddComponentMenu("TopDown Engine/Character/Abilities/Slime Movement")]
    public class SlimeMovementAbility : CharacterMovement
    {
        [Header("Slime Bindings")]
        [Tooltip("Slime_PBF 组件，用于自动绑定 trans")]
        public Slime_PBF SlimePBF;

        public enum StepTriggerModes
        {
            Time,
            Distance,
        }

        [Header("Footstep")]
        public MMFeedbacks FootstepFeedbacks;

        public StepTriggerModes StepTriggerMode = StepTriggerModes.Distance;

        public Vector2 StepIntervalSeconds = new Vector2(0.35f, 0.50f);
        public Vector2 StepIntervalDistance = new Vector2(0.35f, 0.55f);

        private float _footstepTimer;
        private float _footstepDistance;
        private float _nextFootstepInterval;

        protected override void Initialization()
        {
            base.Initialization();

            if (SlimePBF == null)
            {
                SlimePBF = GetComponentInChildren<Slime_PBF>();
            }

            if (SlimePBF == null)
            {
                Debug.LogWarning("[SlimeMovementAbility] SlimePBF is null and no Slime_PBF component was found on the same GameObject.", this);
                return;
            }

            if (SlimePBF.trans == null)
            {
                SlimePBF.trans = transform;
            }

            ResetFootstepState();
        }

        public override void ProcessAbility()
        {
            base.ProcessAbility();

            if (!AbilityAuthorized
                || ((_condition.CurrentState != CharacterStates.CharacterConditions.Normal)
                    && (_condition.CurrentState != CharacterStates.CharacterConditions.ControlledMovement)))
            {
                return;
            }

            if (FootstepFeedbacks == null)
            {
                return;
            }

            if (_controller == null)
            {
                return;
            }

            if (!_controller.Grounded
                || (_movement == null)
                || (_movement.CurrentState != CharacterStates.MovementStates.Walking)
                || (_controller.CurrentMovement.magnitude <= IdleThreshold))
            {
                ResetFootstepState();
                return;
            }

            if (_nextFootstepInterval <= 0f)
            {
                _nextFootstepInterval = GetNextFootstepInterval();
            }

            if (StepTriggerMode == StepTriggerModes.Time)
            {
                _footstepTimer += Time.deltaTime;
                if (_footstepTimer >= _nextFootstepInterval)
                {
                    _footstepTimer = 0f;
                    _nextFootstepInterval = GetNextFootstepInterval();
                    FootstepFeedbacks.PlayFeedbacks(transform.position);
                }
            }
            else
            {
                _footstepDistance += _controller.CurrentMovement.magnitude * Time.deltaTime;
                if (_footstepDistance >= _nextFootstepInterval)
                {
                    _footstepDistance = 0f;
                    _nextFootstepInterval = GetNextFootstepInterval();
                    FootstepFeedbacks.PlayFeedbacks(transform.position);
                }
            }
        }

        private void ResetFootstepState()
        {
            _footstepTimer = 0f;
            _footstepDistance = 0f;
            _nextFootstepInterval = 0f;
        }

        private float GetNextFootstepInterval()
        {
            Vector2 range = StepTriggerMode == StepTriggerModes.Time ? StepIntervalSeconds : StepIntervalDistance;
            float min = Mathf.Min(range.x, range.y);
            float max = Mathf.Max(range.x, range.y);
            return Random.Range(min, max);
        }
    }
}
