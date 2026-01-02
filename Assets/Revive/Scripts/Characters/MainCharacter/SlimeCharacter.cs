using MoreMountains.Tools;
using MoreMountains.TopDownEngine;
using Revive.Effects;
using Revive.Slime;
using UnityEngine;

namespace Revive
{
    /// <summary>
    /// 史莱姆角色类
    /// </summary>
    [RequireComponent(typeof(SlimeWindFieldResistanceAbility))]
    public class SlimeCharacter : Character
    {
        [Header("Slime State Machine")]
        [Tooltip("史莱姆自定义状态机")]
        public MMStateMachine<SlimeStates> SlimeStateMachine;
        
        [Header("Trail Effects")]
        [Tooltip("尾迹效果管理器")]
        public TrailEffectManager TrailManager;
    
        protected override void Awake()
        {
            base.Awake();
            
            // 初始化自定义状态机
            SlimeStateMachine = new MMStateMachine<SlimeStates>(gameObject, true);
            
            // 初始化尾迹管理器
            if (TrailManager == null)
            {
                TrailManager = GetComponent<TrailEffectManager>();
            }
        }
        
        /// <summary>
        /// 启用尾迹效果
        /// </summary>
        public void EnableTrailEffects()
        {
            if (TrailManager != null)
            {
                TrailManager.EnableAllTrails();
            }
        }
        
        /// <summary>
        /// 禁用尾迹效果
        /// </summary>
        public void DisableTrailEffects()
        {
            if (TrailManager != null)
            {
                TrailManager.DisableAllTrails();
            }
        }
        
        /// <summary>
        /// 切换尾迹效果
        /// </summary>
        public void ToggleTrailEffects()
        {
            if (TrailManager != null)
            {
                TrailManager.ToggleTrails();
            }
        }
    }
}
