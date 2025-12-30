using MoreMountains.Tools;
using MoreMountains.TopDownEngine;
using UnityEngine;

namespace Revive.Slime
{
    /// <summary>
    /// 史莱姆交互能力 - 处理 Emit/Recall/SwitchInstance 输入
    /// 使用 TopDownEngine 的 InputManager 按钮映射
    /// </summary>
    [AddComponentMenu("TopDown Engine/Character/Abilities/Slime Interaction")]
    public class SlimeInteractionAbility : CharacterAbility
    {
        [Header("Slime Reference")]
        [Tooltip("Slime_PBF 组件引用")]
        public Slime_PBF SlimePBF;
        public SlimeCarrySlot CarrySlot;

        [Header("Button Mapping")]
        [Tooltip("发射粒子使用的按钮（默认 Shoot）")]
        public bool UseShootForEmit = true;
        
        [Tooltip("召回粒子使用的按钮（默认 SecondaryShoot）")]
        public bool UseSecondaryShootForRecall = true;
        
        [Tooltip("切换实例使用的按钮（默认 SwitchCharacter）")]
        public bool UseSwitchCharacterForSwitch = true;

        protected float _lastEmitTime;

        protected override void Initialization()
        {
            base.Initialization();

            if (SlimePBF == null)
            {
                SlimePBF = GetComponentInChildren<Slime_PBF>();
            }

            if (CarrySlot == null)
            {
                CarrySlot = GetComponentInChildren<SlimeCarrySlot>();
            }

            if (SlimePBF == null)
            {
                Debug.LogWarning("[SlimeInteractionAbility] SlimePBF is null and no Slime_PBF component was found on the same GameObject.", this);
            }
        }

        public override void ProcessAbility()
        {
            base.ProcessAbility();
            
            if (_inputManager == null || SlimePBF == null)
                return;
            
            HandleEmitInput();
            HandleRecallInput();
            HandleSwitchInput();
        }

        protected virtual void HandleEmitInput()
        {
            if (!UseShootForEmit)
                return;
            
            var shootState = _inputManager.ShootButton.State.CurrentState;
            bool wantEmitOnce = shootState == MMInput.ButtonStates.ButtonDown;
            bool wantEmitRepeat = shootState == MMInput.ButtonStates.ButtonPressed;

            if (!wantEmitOnce && !wantEmitRepeat)
                return;

            if (CarrySlot != null && CarrySlot.HasHeldObject)
            {
                if (CarrySlot.ThrowHeld())
                {
                    _lastEmitTime = Time.time;
                    return;
                }
            }

            if (Time.time - _lastEmitTime < SlimePBF.EmitCooldown)
                return;

            _lastEmitTime = Time.time;
            SlimePBF.EmitParticles();
        }

        protected virtual void HandleRecallInput()
        {
            if (!UseSecondaryShootForRecall)
                return;
            
            if (_inputManager.SecondaryShootButton.State.CurrentState == MMInput.ButtonStates.ButtonDown)
            {
                SlimePBF.StartRecall();
            }
        }

        protected virtual void HandleSwitchInput()
        {
            if (!UseSwitchCharacterForSwitch)
                return;
            
            if (_inputManager.SwitchCharacterButton.State.CurrentState == MMInput.ButtonStates.ButtonDown)
            {
                SlimePBF.SwitchInstance();
            }
        }
    }
}
