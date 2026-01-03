using MoreMountains.Tools;
using MoreMountains.TopDownEngine;
using Revive.GamePlay.Purification;
using UnityEngine;

namespace Revive.Slime
{
    /// <summary>
    /// 史莱姆净化能力 - 定时自动添加净化指示物
    /// 像一个buff一样持续产生净化效果
    /// </summary>
    [AddComponentMenu("TopDown Engine/Character/Abilities/Slime Purification")]
    public class SlimePurificationAbility : CharacterAbility
    {
        [Header("净化设置")]
        [Tooltip("净化间隔（秒）")]
        [ChineseLabel("净化间隔")]
        public float PurificationInterval = 3f;
        
        [Tooltip("每次净化的贡献值")]
        [ChineseLabel("贡献值")]
        public float ContributionValue = 10f;
        
        [Tooltip("指示物类型标识")]
        [ChineseLabel("类型标识")]
        public string IndicatorType = "SlimePuri";
        
        [Header("能力控制")]
        [Tooltip("是否启用自动净化")]
        [ChineseLabel("启用净化")]
        public bool EnablePurification = true;
        
        [Tooltip("是否在禁用时移除已创建的指示物")]
        [ChineseLabel("禁用时清理")]
        public bool RemoveIndicatorsOnDisable = false;
        
        [Header("运行时信息")]
        [SerializeField, MMReadOnly]
        private float _timeSinceLastPurification = 0f;
        
        [SerializeField, MMReadOnly]
        private int _indicatorCounter = 0;
        
        [SerializeField, MMReadOnly]
        private int _totalIndicatorsCreated = 0;
        
        private bool _isInitialized = false;
        
        protected override void Initialization()
        {
            base.Initialization();
            
            if (!PurificationSystem.HasInstance)
            {
                Debug.LogWarning("[SlimePurificationAbility] PurificationSystem 未找到，请确保场景中存在 PurificationSystem 实例", this);
            }
            
            _isInitialized = true;
            _timeSinceLastPurification = 0f;
            _indicatorCounter = 0;
            _totalIndicatorsCreated = 0;
            
            // 角色初始化时添加一个指示物，贡献值为100
            PurificationSystem.Instance.AddIndicator(
                "InitPuri",
                transform.position,
                100,
                IndicatorType
            );
        }
        
        public override void ProcessAbility()
        {
            base.ProcessAbility();
            
            if (!_isInitialized || !EnablePurification || !PurificationSystem.HasInstance)
                return;
            
            // 累积时间
            _timeSinceLastPurification += Time.deltaTime;
            
            // 达到间隔时间，添加净化指示物
            if (_timeSinceLastPurification >= PurificationInterval)
            {
                AddPurificationIndicator();
                _timeSinceLastPurification = 0f;
            }
        }
        
        /// <summary>
        /// 添加净化指示物
        /// </summary>
        protected virtual void AddPurificationIndicator()
        {
            if (!PurificationSystem.HasInstance)
                return;
            
            // 生成指示物名称
            string indicatorName = $"{gameObject.name}_{IndicatorType}_{_indicatorCounter++}";
            
            // 添加到净化系统
            PurificationSystem.Instance.AddIndicator(
                indicatorName,
                transform.position,
                ContributionValue,
                IndicatorType
            );
            
            _totalIndicatorsCreated++;
            
            // 可选：播放反馈效果
            OnPurificationIndicatorAdded();
        }
        
        /// <summary>
        /// 当添加净化指示物时调用（可在子类中重写添加反馈效果）
        /// </summary>
        protected virtual void OnPurificationIndicatorAdded()
        {
            // 子类可以在这里添加粒子效果、音效等
        }
        
        /// <summary>
        /// 启用净化能力
        /// </summary>
        public virtual void EnablePurificationAbility()
        {
            EnablePurification = true;
            _timeSinceLastPurification = 0f;
        }
        
        /// <summary>
        /// 禁用净化能力
        /// </summary>
        public virtual void DisablePurificationAbility()
        {
            EnablePurification = false;
        }
        
        /// <summary>
        /// 重置计数器
        /// </summary>
        public virtual void ResetCounters()
        {
            _indicatorCounter = 0;
            _totalIndicatorsCreated = 0;
            _timeSinceLastPurification = 0f;
        }
        
        /// <summary>
        /// 立即触发一次净化（忽略冷却时间）
        /// </summary>
        public virtual void TriggerPurificationNow()
        {
            if (PurificationSystem.HasInstance)
            {
                AddPurificationIndicator();
            }
        }
        
        protected override void OnDisable()
        {
            base.OnDisable();
            
            // 如果设置了禁用时清理，移除该Ability创建的所有指示物
            if (RemoveIndicatorsOnDisable && PurificationSystem.HasInstance)
            {
                // 注意：这会移除所有相同类型的指示物，不仅仅是这个Ability创建的
                // 如果需要更精确的控制，可以在Ability中维护一个创建的指示物列表
                int removed = PurificationSystem.Instance.RemoveIndicatorsByType(IndicatorType);
                Debug.Log($"[SlimePurificationAbility] 禁用时移除了 {removed} 个 {IndicatorType} 类型的指示物");
            }
        }
        
#if UNITY_EDITOR
        /// <summary>
        /// 在Scene视图中显示调试信息
        /// </summary>
        private void OnDrawGizmosSelected()
        {
            if (!Application.isPlaying || !EnablePurification)
                return;
            
            // 显示下次净化的倒计时
            float timeUntilNext = PurificationInterval - _timeSinceLastPurification;
            string info = $"净化倒计时: {timeUntilNext:F1}s\n" +
                         $"已创建: {_totalIndicatorsCreated} 个指示物";
            
            UnityEditor.Handles.Label(transform.position + Vector3.up * 2.5f, info);
            
            // 绘制一个进度环
            float progress = _timeSinceLastPurification / PurificationInterval;
            Gizmos.color = new Color(0.3f, 1f, 0.3f, 0.5f);
            DrawProgressCircle(transform.position + Vector3.up * 0.1f, 0.5f, progress);
        }
        
        private void DrawProgressCircle(Vector3 center, float radius, float progress)
        {
            int segments = 32;
            int completedSegments = Mathf.CeilToInt(segments * progress);
            
            for (int i = 0; i < completedSegments; i++)
            {
                float angle1 = (float)i / segments * 360f * Mathf.Deg2Rad;
                float angle2 = (float)(i + 1) / segments * 360f * Mathf.Deg2Rad;
                
                Vector3 point1 = center + new Vector3(Mathf.Cos(angle1) * radius, 0, Mathf.Sin(angle1) * radius);
                Vector3 point2 = center + new Vector3(Mathf.Cos(angle2) * radius, 0, Mathf.Sin(angle2) * radius);
                
                Gizmos.DrawLine(point1, point2);
            }
        }
#endif
    }
}

