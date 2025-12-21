using MoreMountains.Tools;
using MoreMountains.TopDownEngine;
using Slime;
using UnityEngine;

namespace Revive
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
            Debug.Log($"[SlimeInteraction] Init: InputManager={_inputManager != null}, SlimePBF={SlimePBF != null}");
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
            
            if (_inputManager.ShootButton.State.CurrentState == MMInput.ButtonStates.ButtonPressed)
            {
                if (Time.time - _lastEmitTime >= SlimePBF.EmitCooldown)
                {
                    _lastEmitTime = Time.time;
                    SlimePBF.EmitParticles();
                }
            }
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
