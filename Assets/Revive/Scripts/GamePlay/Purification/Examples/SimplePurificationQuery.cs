using MoreMountains.Tools;
using UnityEngine;

namespace Revive.GamePlay.Purification.Examples
{
    /// <summary>
    /// 简单净化度查询器示例
    /// 演示如何在任意位置查询净化度
    /// </summary>
    public class SimplePurificationQuery : MonoBehaviour
    {
        [Header("查询设置")]
        [Tooltip("查询间隔（秒）")]
        public float QueryInterval = 1f;
        
        [Tooltip("查询位置（留空则使用自身位置）")]
        public Transform QueryTarget;
        
        [Header("调试显示")]
        [Tooltip("是否在控制台输出")]
        public bool LogToConsole = true;
        
        [Tooltip("是否在Scene视图显示文本")]
        public bool ShowInScene = true;
        
        [SerializeField, MMReadOnly]
        private float _currentPurificationLevel = 0f;
        
        [SerializeField, MMReadOnly]
        private float _totalContribution = 0f;
        
        [SerializeField, MMReadOnly]
        private int _indicatorCount = 0;
        
        private float _queryTimer = 0f;
        
        private void Update()
        {
            if (!PurificationSystem.HasInstance) return;
            
            _queryTimer += Time.deltaTime;
            
            if (_queryTimer >= QueryInterval)
            {
                QueryPurification();
                _queryTimer = 0f;
            }
        }
        
        private void QueryPurification()
        {
            Vector3 queryPos = QueryTarget != null ? QueryTarget.position : transform.position;
            
            // 执行详细查询
            _currentPurificationLevel = PurificationSystem.Instance.GetPurificationLevelDetailed(
                queryPos,
                PurificationSystem.Instance.DetectionRadius,
                out _totalContribution,
                out _indicatorCount
            );
            
            if (LogToConsole)
            {
                Debug.Log($"[{gameObject.name}] 净化度: {_currentPurificationLevel:F2} ({_currentPurificationLevel * 100:F0}%), " +
                         $"指示物: {_indicatorCount}, 贡献: {_totalContribution:F1}");
            }
        }
        
        private void OnDrawGizmos()
        {
            if (!ShowInScene || !PurificationSystem.HasInstance) return;
            
            Vector3 pos = QueryTarget != null ? QueryTarget.position : transform.position;
            
            // 显示净化度百分比
#if UNITY_EDITOR
            UnityEditor.Handles.Label(
                pos + Vector3.up * 2f,
                $"净化度: {_currentPurificationLevel * 100:F0}%\n指示物: {_indicatorCount}"
            );
#endif
        }
    }
}

