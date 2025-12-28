using UnityEngine;

namespace Revive.Slime
{
    /// <summary>
    /// 场景水珠源 - 预先放置在场景中的独立水珠
    /// 采用 Streaming/LOD 架构：只在玩家附近时激活
    /// </summary>
    public class DropWater : MonoBehaviour
    {
        public enum DropletSourceState
        {
            Dormant,    // 休眠：不占用粒子池
            Simulated,  // 激活：参与物理模拟
            Consumed    // 已消耗：被吸收完毕
        }

        [Header("【粒子配置】")]
        [ChineseLabel("粒子数量"), Tooltip("该水珠包含的粒子数")]
        [Range(10, 5000), DefaultValue(100)]
        public int particleCount = 100;

        [ChineseLabel("生成半径"), Tooltip("初始粒子分布的球形半径")]
        [Range(0.5f, 5f), DefaultValue(1f)]
        public float spawnRadius = 1f;

        [Header("【物理参数】")]
        [ChineseLabel("凝聚力强度"), Tooltip("粒子向中心凝聚的力度")]
        [Range(0f, 100f), DefaultValue(30f)]
        public float cohesionStrength = 30f;

        [ChineseLabel("凝聚半径"), Tooltip("凝聚力作用的最大半径")]
        [Range(1f, 20f), DefaultValue(10f)]
        public float cohesionRadius = 10f;

        [ChineseLabel("速度阻尼"), Tooltip("速度衰减系数，越大越慢")]
        [Range(0.9f, 1f), DefaultValue(0.99f)]
        public float velocityDamping = 0.99f;

        [ChineseLabel("垂直凝聚缩放"), Tooltip("向下凝聚力的缩放（<1削弱）")]
        [Range(0f, 2f), DefaultValue(0.5f)]
        public float verticalCohesionScale = 0.5f;

        [ChineseLabel("启用粘性"), Tooltip("启用粘性可维持形状")]
        public bool enableViscosity = true;

        [ChineseLabel("粘性强度"), Tooltip("粘性强度，越大形状越稳定（与主体一致=10）")]
        [Range(0f, 100f), DefaultValue(1f)]
        public float viscosityStrength = 1f;

        [Header("【激活配置（Streaming）】")]
        [ChineseLabel("激活半径"), Tooltip("玩家进入此范围时激活水珠")]
        [Range(10f, 100f), DefaultValue(50f)]
        public float activationRadius = 50f;

        [ChineseLabel("休眠半径"), Tooltip("玩家离开此范围时休眠水珠（应略大于激活半径避免抖动）")]
        [Range(15f, 120f), DefaultValue(60f)]
        public float deactivationRadius = 60f;
        
        // 注：吸收配置已移除，改为接触融合自动吸收

        [Header("【运行时状态（只读）】")]
        [ChineseLabel("当前状态")]
        [SerializeField]
        private DropletSourceState state = DropletSourceState.Dormant;

        [Header("【调试】")]
        [ChineseLabel("输出调试日志"), Tooltip("输出状态变化/分配日志（会产生日志）")]
        public bool dropletDebug;

        [ChineseLabel("剩余粒子数"), Tooltip("尚未被吸收的粒子数")]
        [SerializeField]
        private int remainingCount;

        [ChineseLabel("组ID"), Tooltip("水滴分组ID")]
        [SerializeField]
        private int groupId = -1;

        [ChineseLabel("自适应半径"), Tooltip("基于粒子分布计算的实际半径")]
        [SerializeField]
        private float adaptiveRadius = 1f;

        // 公共属性访问器
        public DropletSourceState State => state;
        public float AdaptiveRadius => adaptiveRadius;
        public int RemainingCount => remainingCount;
        public int GroupId => groupId;

        /// <summary>
        /// 设置水珠状态
        /// </summary>
        public void SetState(DropletSourceState newState)
        {
            if (state != newState)
            {
                state = newState;
            }
        }

        /// <summary>
        /// 分配粒子索引范围
        /// </summary>
        public void AssignParticles(int start, int count)
        {
            remainingCount = count;
        }

        /// <summary>
        /// 设置组ID
        /// </summary>
        public void SetGroupId(int id)
        {
            groupId = id;
        }

        /// <summary>
        /// 设置自适应半径（基于粒子实际分布）
        /// </summary>
        public void SetAdaptiveRadius(float radius)
        {
            adaptiveRadius = radius;
        }

        /// <summary>
        /// 吸收粒子
        /// </summary>
        /// <returns>实际吸收的粒子数</returns>
        public int AbsorbParticles(int requestedCount)
        {
            int absorbed = Mathf.Min(requestedCount, remainingCount);
            remainingCount -= absorbed;
            
            if (remainingCount == 0)
            {
                SetState(DropletSourceState.Consumed);
            }
            
            return absorbed;
        }

        /// <summary>
        /// 重置到初始状态
        /// </summary>
        public void Reset()
        {
            state = DropletSourceState.Dormant;
            remainingCount = particleCount;
            groupId = -1;
        }

        // Gizmos 可视化
        void OnDrawGizmos()
        {
            // 激活半径（黄色）
            Gizmos.color = new Color(1f, 1f, 0f, 0.2f);
            Gizmos.DrawWireSphere(transform.position, activationRadius);

            // 休眠半径（橙色虚线）
            Gizmos.color = new Color(1f, 0.5f, 0f, 0.15f);
            DrawDashedWireSphere(transform.position, deactivationRadius, 32);

            // 生成范围（蓝色）
            Gizmos.color = new Color(0f, 0.5f, 1f, 0.8f);
            Gizmos.DrawWireSphere(transform.position, spawnRadius);

            // 自适应半径（绿色，仅在激活时显示，基于实际粒子范围）
            if (state == DropletSourceState.Simulated && adaptiveRadius > 0)
            {
                Gizmos.color = new Color(0f, 1f, 0f, 0.9f);
                Gizmos.DrawWireSphere(transform.position, adaptiveRadius);
            }

            // 显示状态信息和半径数值
            string statusText = $"{name}\n{state}";
            statusText += $"\n蓝(生成)={spawnRadius:F1}";
            if (state == DropletSourceState.Simulated)
            {
                statusText += $"\n绿(自适应)={adaptiveRadius:F1}";
                statusText += $"\n剩余: {remainingCount}/{particleCount}";
                if (groupId >= 0)
                    statusText += $"\n组: {groupId}";
            }

#if UNITY_EDITOR
            var style = new GUIStyle();
            style.normal.textColor = GetStateColor();
            style.fontSize = 12;
            style.alignment = TextAnchor.MiddleCenter;
            
            UnityEditor.Handles.Label(
                transform.position + Vector3.up * (spawnRadius + 1f), 
                statusText, 
                style
            );
#endif
        }

        void OnDrawGizmosSelected()
        {
            // 选中时显示更详细的信息
            Gizmos.color = Color.white;
            
            // 绘制到激活半径的标尺
            Vector3 right = transform.right * activationRadius;
            Gizmos.DrawLine(transform.position, transform.position + right);
            
#if UNITY_EDITOR
            var style = new GUIStyle();
            style.normal.textColor = Color.yellow;
            style.fontSize = 10;
            
            UnityEditor.Handles.Label(
                transform.position + right * 0.5f, 
                $"激活: {activationRadius:F1}m", 
                style
            );
#endif
        }

        // 辅助方法：绘制虚线球体
        private void DrawDashedWireSphere(Vector3 center, float radius, int segments)
        {
            float angleStep = 360f / segments;
            
            // 绘制水平圆
            for (int i = 0; i < segments; i += 2)
            {
                float angle1 = i * angleStep * Mathf.Deg2Rad;
                float angle2 = (i + 1) * angleStep * Mathf.Deg2Rad;
                
                Vector3 p1 = center + new Vector3(Mathf.Cos(angle1), 0, Mathf.Sin(angle1)) * radius;
                Vector3 p2 = center + new Vector3(Mathf.Cos(angle2), 0, Mathf.Sin(angle2)) * radius;
                
                Gizmos.DrawLine(p1, p2);
            }
        }

        // 获取状态对应的颜色
        private Color GetStateColor()
        {
            switch (state)
            {
                case DropletSourceState.Dormant:
                    return Color.gray;
                case DropletSourceState.Simulated:
                    return Color.cyan;
                case DropletSourceState.Consumed:
                    return Color.red;
                default:
                    return Color.white;
            }
        }

        /// <summary>
        /// 重置参数为默认值
        /// </summary>
        [ContextMenu("重置参数为默认值")]
        public void ResetToDefaults()
        {
            ConfigResetHelper.ResetToDefaults(this);
        }
    }
}
