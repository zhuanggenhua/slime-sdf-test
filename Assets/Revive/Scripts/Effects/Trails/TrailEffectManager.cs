using UnityEngine;
using System.Collections.Generic;

namespace Revive.Effects
{
    /// <summary>
    /// 尾迹效果管理器，统一管理多个尾迹效果组件
    /// </summary>
    public class TrailEffectManager : MonoBehaviour
    {
        [Header("Trail Effects")]
        [Tooltip("所有尾迹效果组件")]
        public List<TrailEffectBase> TrailEffects = new List<TrailEffectBase>();
        
        [Header("Global Settings")]
        [Tooltip("是否启用尾迹效果")]
        public bool EnableTrails = true;
        
        [Tooltip("全局距离阈值乘数")]
        [Range(0.1f, 5f)]
        public float GlobalDistanceMultiplier = 1f;
        
        private void Awake()
        {
            // 自动收集子对象中的尾迹效果
            if (TrailEffects.Count == 0)
            {
                TrailEffects.AddRange(GetComponentsInChildren<TrailEffectBase>());
            }
        }
        
        private void Start()
        {
            UpdateTrailStates();
        }
        
        /// <summary>
        /// 启用所有尾迹效果
        /// </summary>
        public void EnableAllTrails()
        {
            EnableTrails = true;
            UpdateTrailStates();
        }
        
        /// <summary>
        /// 禁用所有尾迹效果
        /// </summary>
        public void DisableAllTrails()
        {
            EnableTrails = false;
            UpdateTrailStates();
        }
        
        /// <summary>
        /// 切换尾迹效果开关
        /// </summary>
        public void ToggleTrails()
        {
            EnableTrails = !EnableTrails;
            UpdateTrailStates();
        }
        
        /// <summary>
        /// 更新所有尾迹效果的启用状态
        /// </summary>
        private void UpdateTrailStates()
        {
            foreach (var trail in TrailEffects)
            {
                if (trail != null)
                {
                    trail.enabled = EnableTrails;
                }
            }
        }
        
        /// <summary>
        /// 获取指定类型的尾迹效果
        /// </summary>
        public T GetTrailEffect<T>() where T : TrailEffectBase
        {
            foreach (var trail in TrailEffects)
            {
                if (trail is T)
                {
                    return trail as T;
                }
            }
            return null;
        }
        
        /// <summary>
        /// 添加尾迹效果
        /// </summary>
        public void AddTrailEffect(TrailEffectBase effect)
        {
            if (effect != null && !TrailEffects.Contains(effect))
            {
                TrailEffects.Add(effect);
                effect.enabled = EnableTrails;
            }
        }
        
        /// <summary>
        /// 移除尾迹效果
        /// </summary>
        public void RemoveTrailEffect(TrailEffectBase effect)
        {
            if (effect != null && TrailEffects.Contains(effect))
            {
                TrailEffects.Remove(effect);
            }
        }
    }
}

