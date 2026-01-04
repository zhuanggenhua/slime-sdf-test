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
        [Tooltip("净化采样间距（米）")]
        [ChineseLabel("净化间距")]
        public float FootprintSpacing = 1f;
        
        [Tooltip("每次净化的贡献值")]
        [ChineseLabel("贡献值")]
        public float ContributionValue = 10f;
        
        [Tooltip("辐射范围（米）")]
        [ChineseLabel("辐射范围")]
        public float RadiationRadius = 8f;

        [Tooltip("如果该范围内已存在同类型指示物，则跳过生成（米）。<=0 表示不启用")]
        [ChineseLabel("附近已存在则跳过")]
        [DefaultValue(0.9f)]
        public float SkipIfIndicatorExistsRadius = 0.9f;
        
        [Tooltip("指示物类型标识")]
        [ChineseLabel("类型标识")]
        public string IndicatorType = "SlimePuri";

        [Header("能力控制")]
        [Tooltip("是否启用自动净化")]
        [ChineseLabel("启用净化")]
        public bool EnablePurification = true;
        
        [Header("运行时信息")]
        [SerializeField, MMReadOnly]
        private float _distanceAccumulator = 0f;
        
        [SerializeField, MMReadOnly]
        private int _indicatorCounter = 0;
        
        [SerializeField, MMReadOnly]
        private int _totalIndicatorsCreated = 0;

        private bool _isInitialized = false;

        private bool _hasLastFramePosition;
        private Vector3 _lastFramePositionWorld;
        
        protected override void Initialization()
        {
            base.Initialization();
            
            if (!PurificationSystem.HasInstance)
            {
                Debug.LogWarning("[SlimePurificationAbility] PurificationSystem 未找到，请确保场景中存在 PurificationSystem 实例", this);
            }
            
            _isInitialized = true;
            _distanceAccumulator = 0f;
            _indicatorCounter = 0;
            _totalIndicatorsCreated = 0;
            _hasLastFramePosition = false;
            
            // 角色初始化时添加一个指示物，贡献值为100
            if (PurificationSystem.HasInstance)
            {
                int id = gameObject != null ? gameObject.GetInstanceID() : 0;
                string name = $"{IndicatorType}_{id}_Init";
                PurificationSystem.Instance.AddIndicator(name, transform.position, 100f, IndicatorType, RadiationRadius);
            }
        }
        
        public override void ProcessAbility()
        {
            base.ProcessAbility();
            
            if (!_isInitialized || !EnablePurification || !PurificationSystem.HasInstance)
                return;

            AccumulateDistanceAndStamp();
        }

        private void AccumulateDistanceAndStamp()
        {
            Vector3 pos = transform.position;
            if (!_hasLastFramePosition)
            {
                _hasLastFramePosition = true;
                _lastFramePositionWorld = pos;
                return;
            }

            float spacing = Mathf.Max(0.01f, FootprintSpacing);

            Vector3 a = _lastFramePositionWorld;
            Vector3 b = pos;
            Vector3 ab = b - a;
            float segLen = ab.magnitude;
            if (segLen <= 1e-6f)
                return;

            Vector3 dir = ab / segLen;

            float remaining = segLen;
            Vector3 cursor = a;
            while (remaining > 1e-6f)
            {
                float need = spacing - _distanceAccumulator;
                if (need <= 1e-6f)
                    need = spacing;

                if (remaining < need)
                {
                    _distanceAccumulator += remaining;
                    break;
                }

                cursor += dir * need;
                remaining -= need;
                _distanceAccumulator = 0f;

                AddPurificationIndicatorAt(cursor);
            }

            _lastFramePositionWorld = pos;
        }
        
        /// <summary>
        /// 添加净化指示物
        /// </summary>
        protected virtual void AddPurificationIndicatorAt(Vector3 position)
        {
            if (!PurificationSystem.HasInstance)
                return;

            float checkRadius = Mathf.Max(0f, SkipIfIndicatorExistsRadius);
            if (checkRadius > 0f && PurificationSystem.Instance.HasIndicatorInRange(position, checkRadius, IndicatorType))
                return;
            
            int id = gameObject != null ? gameObject.GetInstanceID() : 0;
            _indicatorCounter++;
            string name = $"{IndicatorType}_{id}_{_indicatorCounter}";
            PurificationSystem.Instance.AddIndicator(name, position, ContributionValue, IndicatorType, RadiationRadius);
            
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
            _distanceAccumulator = 0f;
            _hasLastFramePosition = false;
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
            _distanceAccumulator = 0f;
            _hasLastFramePosition = false;
        }
        
        /// <summary>
        /// 立即触发一次净化（忽略冷却时间）
        /// </summary>
        public virtual void TriggerPurificationNow()
        {
            if (PurificationSystem.HasInstance)
            {
                AddPurificationIndicatorAt(transform.position);
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
            
            string info = $"净化间距: {FootprintSpacing:F2}m\n" +
                         $"已创建: {_totalIndicatorsCreated} 个指示物";
            
            UnityEditor.Handles.Label(transform.position + Vector3.up * 2.5f, info);
            
            // 绘制一个进度环
            float progress = 1f;
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

