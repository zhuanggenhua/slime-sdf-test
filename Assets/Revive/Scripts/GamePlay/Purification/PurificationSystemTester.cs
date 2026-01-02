using UnityEngine;
using System.Collections;

namespace Revive.GamePlay.Purification
{
    /// <summary>
    /// 净化系统测试脚本
    /// 用于测试和验证净化系统的各项功能
    /// </summary>
    public class PurificationSystemTester : MonoBehaviour
    {
        [Header("测试配置")]
        [Tooltip("是否在Start时自动运行测试")]
        public bool AutoRunTestOnStart = false;
        
        [Tooltip("测试间隔时间（秒）")]
        public float TestInterval = 2f;
        
        [Header("指示物测试")]
        [Tooltip("添加指示物数量")]
        public int IndicatorCount = 5;
        
        [Tooltip("指示物生成半径")]
        public float SpawnRadius = 20f;
        
        [Tooltip("指示物贡献值范围")]
        public Vector2 ContributionRange = new Vector2(5f, 15f);
        
        [Header("查询测试")]
        [Tooltip("查询位置")]
        public Transform QueryPosition;
        
        [Header("存档测试")]
        [Tooltip("测试存档文件名")]
        public string TestSaveFileName = "test_purification.json";
        
        private void Start()
        {
            if (QueryPosition == null)
            {
                QueryPosition = transform;
            }
            
            if (AutoRunTestOnStart)
            {
                StartCoroutine(RunAllTests());
            }
        }
        
        private void Update()
        {
            // 按键快捷测试
            if (Input.GetKeyDown(KeyCode.P))
            {
                TestAddIndicators();
            }
            
            if (Input.GetKeyDown(KeyCode.O))
            {
                TestQueryPurificationLevel();
            }
            
            if (Input.GetKeyDown(KeyCode.I))
            {
                TestSaveLoad();
            }
            
            if (Input.GetKeyDown(KeyCode.U))
            {
                TestClearIndicators();
            }
        }
        
        #region 测试方法
        
        /// <summary>
        /// 运行所有测试
        /// </summary>
        public IEnumerator RunAllTests()
        {
            Debug.Log("========== 净化系统测试开始 ==========");
            
            yield return new WaitForSeconds(TestInterval);
            TestSystemInstance();
            
            yield return new WaitForSeconds(TestInterval);
            TestAddIndicators();
            
            yield return new WaitForSeconds(TestInterval);
            TestQueryPurificationLevel();
            
            yield return new WaitForSeconds(TestInterval);
            TestGetIndicatorsInRange();
            
            yield return new WaitForSeconds(TestInterval);
            TestSaveLoad();
            
            yield return new WaitForSeconds(TestInterval);
            TestRemoveIndicators();
            
            Debug.Log("========== 净化系统测试完成 ==========");
        }
        
        /// <summary>
        /// 测试：系统实例化
        /// </summary>
        [ContextMenu("1. Test System Instance")]
        public void TestSystemInstance()
        {
            Debug.Log("--- 测试：系统实例化 ---");
            
            if (PurificationSystem.HasInstance)
            {
                Debug.Log("✓ 系统实例存在");
                Debug.Log($"  检测半径: {PurificationSystem.Instance.DetectionRadius}m");
                Debug.Log($"  目标净化值: {PurificationSystem.Instance.TargetPurificationValue}");
            }
            else
            {
                Debug.LogError("✗ 系统实例不存在！");
            }
        }
        
        /// <summary>
        /// 测试：添加指示物
        /// </summary>
        [ContextMenu("2. Test Add Indicators")]
        public void TestAddIndicators()
        {
            Debug.Log("--- 测试：添加指示物 ---");
            
            if (!PurificationSystem.HasInstance)
            {
                Debug.LogError("系统实例不存在！");
                return;
            }
            
            Vector3 center = QueryPosition.position;
            
            for (int i = 0; i < IndicatorCount; i++)
            {
                // 随机生成位置
                Vector2 randomCircle = Random.insideUnitCircle * SpawnRadius;
                Vector3 position = center + new Vector3(randomCircle.x, 0, randomCircle.y);
                
                // 随机贡献值
                float contribution = Random.Range(ContributionRange.x, ContributionRange.y);
                
                // 随机类型
                string[] types = { "Idle", "Water", "Spore", "Plant" };
                string type = types[Random.Range(0, types.Length)];
                
                // 添加指示物
                string name = $"{type}_{i}";
                PurificationSystem.Instance.AddIndicator(name, position, contribution, type);
            }
            
            Debug.Log($"✓ 添加了 {IndicatorCount} 个指示物");
            Debug.Log($"  总指示物数量: {PurificationSystem.Instance.GetAllIndicators().Count}");
        }
        
        /// <summary>
        /// 测试：查询净化度
        /// </summary>
        [ContextMenu("3. Test Query Purification Level")]
        public void TestQueryPurificationLevel()
        {
            Debug.Log("--- 测试：查询净化度 ---");
            
            if (!PurificationSystem.HasInstance)
            {
                Debug.LogError("系统实例不存在！");
                return;
            }
            
            Vector3 position = QueryPosition.position;
            
            // 简单查询
            float level = PurificationSystem.Instance.GetPurificationLevel(position);
            Debug.Log($"✓ 位置 {position} 的净化度: {level:F2} ({level * 100:F0}%)");
            
            // 详细查询
            float totalContribution;
            int indicatorCount;
            float levelDetailed = PurificationSystem.Instance.GetPurificationLevelDetailed(
                position, 
                PurificationSystem.Instance.DetectionRadius, 
                out totalContribution, 
                out indicatorCount
            );
            
            Debug.Log($"  详细信息:");
            Debug.Log($"    净化度: {levelDetailed:F2}");
            Debug.Log($"    范围内指示物数量: {indicatorCount}");
            Debug.Log($"    贡献值总和: {totalContribution:F1}");
            Debug.Log($"    目标值: {PurificationSystem.Instance.TargetPurificationValue:F1}");
        }
        
        /// <summary>
        /// 测试：获取范围内指示物
        /// </summary>
        [ContextMenu("4. Test Get Indicators In Range")]
        public void TestGetIndicatorsInRange()
        {
            Debug.Log("--- 测试：获取范围内指示物 ---");
            
            if (!PurificationSystem.HasInstance)
            {
                Debug.LogError("系统实例不存在！");
                return;
            }
            
            Vector3 position = QueryPosition.position;
            float radius = PurificationSystem.Instance.DetectionRadius;
            
            var indicators = PurificationSystem.Instance.GetIndicatorsInRange(position, radius);
            
            Debug.Log($"✓ 在半径 {radius}m 内找到 {indicators.Count} 个指示物:");
            foreach (var indicator in indicators)
            {
                float distance = Vector3.Distance(position, indicator.Position);
                Debug.Log($"  - {indicator.Name}: 距离={distance:F1}m, 贡献={indicator.ContributionValue:F1}, 类型={indicator.IndicatorType}");
            }
        }
        
        /// <summary>
        /// 测试：保存和加载
        /// </summary>
        [ContextMenu("5. Test Save and Load")]
        public void TestSaveLoad()
        {
            Debug.Log("--- 测试：保存和加载 ---");
            
            if (!PurificationSystem.HasInstance)
            {
                Debug.LogError("系统实例不存在！");
                return;
            }
            
            int beforeCount = PurificationSystem.Instance.GetAllIndicators().Count;
            
            // 保存
            PurificationSystem.Instance.SaveToFile(TestSaveFileName);
            Debug.Log($"✓ 已保存 {beforeCount} 个指示物");
            
            // 清空
            PurificationSystem.Instance.ClearAllIndicators();
            Debug.Log($"  清空后指示物数量: {PurificationSystem.Instance.GetAllIndicators().Count}");
            
            // 加载
            PurificationSystem.Instance.LoadFromFile(TestSaveFileName);
            int afterCount = PurificationSystem.Instance.GetAllIndicators().Count;
            Debug.Log($"✓ 已加载 {afterCount} 个指示物");
            
            if (beforeCount == afterCount)
            {
                Debug.Log("✓ 保存/加载测试通过！");
            }
            else
            {
                Debug.LogError($"✗ 保存/加载测试失败！保存前={beforeCount}, 加载后={afterCount}");
            }
        }
        
        /// <summary>
        /// 测试：移除指示物
        /// </summary>
        [ContextMenu("6. Test Remove Indicators")]
        public void TestRemoveIndicators()
        {
            Debug.Log("--- 测试：移除指示物 ---");
            
            if (!PurificationSystem.HasInstance)
            {
                Debug.LogError("系统实例不存在！");
                return;
            }
            
            int beforeCount = PurificationSystem.Instance.GetAllIndicators().Count;
            
            // 按类型移除
            int removed = PurificationSystem.Instance.RemoveIndicatorsByType("Water");
            Debug.Log($"✓ 按类型移除了 {removed} 个 'Water' 指示物");
            
            int afterCount = PurificationSystem.Instance.GetAllIndicators().Count;
            Debug.Log($"  移除前: {beforeCount}, 移除后: {afterCount}");
        }
        
        /// <summary>
        /// 测试：清空所有指示物
        /// </summary>
        [ContextMenu("7. Test Clear All Indicators")]
        public void TestClearIndicators()
        {
            Debug.Log("--- 测试：清空所有指示物 ---");
            
            if (!PurificationSystem.HasInstance)
            {
                Debug.LogError("系统实例不存在！");
                return;
            }
            
            PurificationSystem.Instance.ClearAllIndicators();
            Debug.Log($"✓ 已清空所有指示物");
            Debug.Log($"  当前指示物数量: {PurificationSystem.Instance.GetAllIndicators().Count}");
        }
        
        #endregion
        
        #region Gizmos绘制
        
        private void OnDrawGizmos()
        {
            if (QueryPosition == null) return;
            
            // 绘制查询位置
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(QueryPosition.position, 0.5f);
            
            // 绘制生成范围
            Gizmos.color = new Color(1f, 1f, 0f, 0.3f);
            DrawWireCircle(QueryPosition.position, SpawnRadius, 32);
        }
        
        private void DrawWireCircle(Vector3 center, float radius, int segments)
        {
            float angleStep = 360f / segments;
            Vector3 prevPoint = center + new Vector3(radius, 0, 0);
            
            for (int i = 1; i <= segments; i++)
            {
                float angle = angleStep * i * Mathf.Deg2Rad;
                Vector3 newPoint = center + new Vector3(Mathf.Cos(angle) * radius, 0, Mathf.Sin(angle) * radius);
                Gizmos.DrawLine(prevPoint, newPoint);
                prevPoint = newPoint;
            }
        }
        
        #endregion
    }
}

