using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using MoreMountains.Tools;

namespace Revive.GamePlay.Purification
{
    /// <summary>
    /// 净化系统核心管理类
    /// 管理净化指示物、监听者注册、净化度计算等核心功能
    /// </summary>
    public class PurificationSystem : MMPersistentSingleton<PurificationSystem>
    {
        [Header("净化系统配置")]
        [Tooltip("识别半径（米），在此半径内的指示物会被计入净化度")]
        public float DetectionRadius = 10f;
        
        [Tooltip("目标净化值，达到此值时净化度为1.0")]
        public float TargetPurificationValue = 100f;
        
        [Header("调试设置")]
        [Tooltip("是否显示调试Gizmos")]
        public bool ShowDebugGizmos = true;
        
        [Tooltip("指示物Gizmos颜色")]
        public Color IndicatorGizmosColor = new(0.3f, 1f, 0.3f, 0.6f);
        
        [Tooltip("监听者Gizmos颜色")]
        public Color ListenerGizmosColor = new(1f, 0.8f, 0.2f, 0.6f);
        
        [Tooltip("范围圈Gizmos颜色")]
        public Color RangeGizmosColor = new(0.5f, 0.8f, 1f, 0.3f);
        
        // 私有字段
        private readonly List<PurificationIndicator> _indicators = new(16);
        
        private readonly List<IPurificationListener> _listeners = new(16);
        
        public event Action<PurificationIndicator> IndicatorAdded;
        public event Action<PurificationIndicator> IndicatorRemoved;
        
        // 统计信息（用于Inspector显示）
        [Header("运行时统计")]
        [SerializeField, MMReadOnly]
        private int _indicatorCount = 0;
        
        [SerializeField, MMReadOnly]
        private int _listenerCount = 0;
        
        protected override void Awake()
        {
            base.Awake();
        }
        
        private void Update()
        {
            // 更新统计信息
            _indicatorCount = _indicators.Count;
            _listenerCount = _listeners.Count;
        }
        
        #region 指示物管理
        
        /// <summary>
        /// 添加净化指示物
        /// </summary>
        /// <param name="pName">指示物名称</param>
        /// <param name="position">世界坐标位置</param>
        /// <param name="contributionValue">贡献值</param>
        /// <param name="indicatorType">类型标识</param>
        /// <param name="radiationRadius">辐射范围（默认8米）</param>
        /// <returns>创建的指示物实例</returns>
        public PurificationIndicator AddIndicator(string pName, Vector3 position, float contributionValue, string indicatorType, float radiationRadius = 8f)
        {
            PurificationIndicator indicator = new PurificationIndicator(pName, position, contributionValue, indicatorType, radiationRadius);
            _indicators.Add(indicator);
            
            Debug.Log($"[PurificationSystem] 添加指示物: {indicator}");
            IndicatorAdded?.Invoke(indicator);
            
            // 通知半径范围内的监听者
            NotifyListenersInRange(position);
            
            return indicator;
        }
        
        /// <summary>
        /// 移除净化指示物
        /// </summary>
        /// <param name="indicator">要移除的指示物</param>
        /// <returns>是否成功移除</returns>
        public bool RemoveIndicator(PurificationIndicator indicator)
        {
            if (_indicators.Remove(indicator))
            {
                Debug.Log($"[PurificationSystem] 移除指示物: {indicator}");
                IndicatorRemoved?.Invoke(indicator);
                
                // 通知可能受影响的监听者
                NotifyListenersInRange(indicator.Position);
                
                return true;
            }
            return false;
        }
        
        /// <summary>
        /// 根据名称移除指示物
        /// </summary>
        /// <param name="name">指示物名称</param>
        /// <returns>移除的指示物数量</returns>
        public int RemoveIndicatorsByName(string name)
        {
            List<PurificationIndicator> removedIndicators = _indicators.Where(i => i.Name == name).ToList();
            if (removedIndicators.Count > 0)
            {
                foreach (var indicator in removedIndicators)
                {
                    _indicators.Remove(indicator);
                    IndicatorRemoved?.Invoke(indicator);
                }
                
                Debug.Log($"[PurificationSystem] 根据名称移除了 {removedIndicators.Count} 个指示物: {name}");
                NotifyAllListeners();
            }
            return removedIndicators.Count;
        }
        
        /// <summary>
        /// 根据类型移除指示物
        /// </summary>
        /// <param name="indicatorType">指示物类型</param>
        /// <returns>移除的指示物数量</returns>
        public int RemoveIndicatorsByType(string indicatorType)
        {
            List<PurificationIndicator> removedIndicators = _indicators.Where(i => i.IndicatorType == indicatorType).ToList();
            if (removedIndicators.Count > 0)
            {
                foreach (var indicator in removedIndicators)
                {
                    _indicators.Remove(indicator);
                    IndicatorRemoved?.Invoke(indicator);
                }
                
                Debug.Log($"[PurificationSystem] 根据类型移除了 {removedIndicators.Count} 个指示物: {indicatorType}");
                NotifyAllListeners();
            }
            return removedIndicators.Count;
        }
        
        /// <summary>
        /// 清空所有指示物
        /// </summary>
        public void ClearAllIndicators()
        {
            List<PurificationIndicator> removedIndicators = _indicators.ToList();
            int count = removedIndicators.Count;
            _indicators.Clear();
            foreach (var indicator in removedIndicators)
            {
                IndicatorRemoved?.Invoke(indicator);
            }
            Debug.Log($"[PurificationSystem] 清空了 {count} 个指示物");
            NotifyAllListeners();
        }
        
        /// <summary>
        /// 获取所有指示物（只读）
        /// </summary>
        public IReadOnlyList<PurificationIndicator> GetAllIndicators()
        {
            return _indicators.AsReadOnly();
        }
        
        /// <summary>
        /// 获取指定范围内的指示物
        /// </summary>
        public List<PurificationIndicator> GetIndicatorsInRange(Vector3 position, float radius)
        {
            return _indicators.Where(i => i.IsInRange(position, radius)).ToList();
        }
        
        #endregion
        
        #region 净化度查询
        
        /// <summary>
        /// 获取指定位置的净化度
        /// </summary>
        /// <param name="position">查询位置</param>
        /// <param name="radius">查询半径（如不指定则使用默认DetectionRadius）</param>
        /// <returns>净化度 (0-1)</returns>
        public float GetPurificationLevel(Vector3 position, float radius = -1f)
        {
            if (radius < 0)
            {
                radius = DetectionRadius;
            }
            
            // 计算范围内所有指示物的加权贡献值
            float totalContribution = 0f;
            foreach (var indicator in _indicators)
            {
                // 使用球体相交计算权重
                float weight = indicator.CalculateIntersectionWeight(position, radius);
                if (weight > 0f)
                {
                    totalContribution += indicator.ContributionValue * weight;
                }
            }
            
            // 计算净化度 (0-1)
            float purificationLevel = Mathf.Min(1f, totalContribution / TargetPurificationValue);
            
            return purificationLevel;
        }
        
        /// <summary>
        /// 获取指定位置的净化度详细信息
        /// </summary>
        /// <param name="position">查询位置</param>
        /// <param name="radius">查询半径</param>
        /// <param name="totalContribution">输出：加权贡献值总和</param>
        /// <param name="indicatorCount">输出：范围内有效指示物数量（权重>0）</param>
        /// <returns>净化度 (0-1)</returns>
        public float GetPurificationLevelDetailed(Vector3 position, float radius, out float totalContribution, out int indicatorCount)
        {
            if (radius < 0)
            {
                radius = DetectionRadius;
            }
            
            totalContribution = 0f;
            indicatorCount = 0;
            
            foreach (var indicator in _indicators)
            {
                // 使用球体相交计算权重
                float weight = indicator.CalculateIntersectionWeight(position, radius);
                if (weight > 0f)
                {
                    totalContribution += indicator.ContributionValue * weight;
                    indicatorCount++;
                }
            }
            
            float purificationLevel = Mathf.Min(1f, totalContribution / TargetPurificationValue);
            
            return purificationLevel;
        }
        
        #endregion
        
        #region 监听者管理
        
        /// <summary>
        /// 注册监听者
        /// </summary>
        /// <param name="listener">监听者实例</param>
        public void RegisterListener(IPurificationListener listener)
        {
            if (listener == null)
            {
                Debug.LogWarning("[PurificationSystem] 尝试注册空监听者");
                return;
            }
            
            if (!_listeners.Contains(listener))
            {
                _listeners.Add(listener);
                Debug.Log($"[PurificationSystem] 注册监听者: {listener.GetListenerName()}");
                
                // 立即通知监听者当前的净化度
                Vector3 position = listener.GetListenerPosition();
                float level = GetPurificationLevel(position);
                listener.OnPurificationChanged(level, position);
            }
        }
        
        /// <summary>
        /// 注销监听者
        /// </summary>
        /// <param name="listener">监听者实例</param>
        public void UnregisterListener(IPurificationListener listener)
        {
            if (_listeners.Remove(listener))
            {
                Debug.Log($"[PurificationSystem] 注销监听者: {listener?.GetListenerName() ?? "Unknown"}");
            }
        }
        
        /// <summary>
        /// 清空所有监听者
        /// </summary>
        public void ClearAllListeners()
        {
            int count = _listeners.Count;
            _listeners.Clear();
            Debug.Log($"[PurificationSystem] 清空了 {count} 个监听者");
        }
        
        /// <summary>
        /// 通知指定位置半径范围内的监听者
        /// </summary>
        /// <param name="position">中心位置</param>
        private void NotifyListenersInRange(Vector3 position)
        {
            foreach (var listener in _listeners.ToList()) // ToList避免迭代期间修改
            {
                if (listener == null) continue;
                
                Vector3 listenerPos = listener.GetListenerPosition();
                float distance = Vector3.Distance(position, listenerPos);
                
                // 如果监听者在影响范围内，通知它
                if (distance <= DetectionRadius * 2f) // 使用2倍半径确保覆盖边界情况
                {
                    float level = GetPurificationLevel(listenerPos);
                    listener.OnPurificationChanged(level, listenerPos);
                }
            }
        }
        
        /// <summary>
        /// 通知所有监听者
        /// </summary>
        public void NotifyAllListeners()
        {
            foreach (var listener in _listeners.ToList())
            {
                if (listener == null) continue;
                
                Vector3 position = listener.GetListenerPosition();
                float level = GetPurificationLevel(position);
                listener.OnPurificationChanged(level, position);
            }
        }
        
        /// <summary>
        /// 监听者主动请求更新其位置的净化度
        /// （当监听者移动后调用）
        /// </summary>
        /// <param name="listener">监听者实例</param>
        public void RequestUpdate(IPurificationListener listener)
        {
            if (listener == null || !_listeners.Contains(listener)) return;
            
            Vector3 position = listener.GetListenerPosition();
            float level = GetPurificationLevel(position);
            listener.OnPurificationChanged(level, position);
        }
        
        #endregion
        
        #region 数据持久化
        
        /// <summary>
        /// 存档文件名
        /// </summary>
        private const string SaveFileName = "purification_data.json";
        
        /// <summary>
        /// 存档文件夹名
        /// </summary>
        private const string SaveFolderName = "Purification";
        
        /// <summary>
        /// 保存净化数据到文件
        /// </summary>
        public void SaveToFile()
        {
            SaveToFile(SaveFileName);
        }
        
        /// <summary>
        /// 保存净化数据到指定文件
        /// </summary>
        /// <param name="filename">文件名</param>
        public void SaveToFile(string filename)
        {
            PurificationSaveData saveData = PurificationSaveData.FromIndicators(_indicators);
            
            try
            {
                MMSaveLoadManager.SaveLoadMethod = new MMSaveLoadManagerMethodJson();
                MMSaveLoadManager.Save(saveData, filename, SaveFolderName);
                Debug.Log($"[PurificationSystem] 保存成功: {filename}, 指示物数量: {saveData.Indicators.Count}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[PurificationSystem] 保存失败: {e.Message}");
            }
        }
        
        /// <summary>
        /// 从文件加载净化数据
        /// </summary>
        public void LoadFromFile()
        {
            LoadFromFile(SaveFileName);
        }
        
        /// <summary>
        /// 从指定文件加载净化数据
        /// </summary>
        /// <param name="filename">文件名</param>
        public void LoadFromFile(string filename)
        {
            try
            {
                MMSaveLoadManager.SaveLoadMethod = new MMSaveLoadManagerMethodJson();
                PurificationSaveData saveData = (PurificationSaveData)MMSaveLoadManager.Load(
                    typeof(PurificationSaveData), 
                    filename, 
                    SaveFolderName
                );
                
                if (saveData != null && saveData.Indicators != null)
                {
                    List<PurificationIndicator> previousIndicators = _indicators.ToList();
                    saveData.ToIndicators(_indicators);
                    Debug.Log($"[PurificationSystem] 加载成功: {filename}, 指示物数量: {_indicators.Count}, 存档时间: {saveData.SaveTime}");
                    
                    foreach (var indicator in previousIndicators)
                    {
                        IndicatorRemoved?.Invoke(indicator);
                    }
                    
                    foreach (var indicator in _indicators)
                    {
                        IndicatorAdded?.Invoke(indicator);
                    }
                    
                    // 通知所有监听者
                    NotifyAllListeners();
                }
                else
                {
                    Debug.LogWarning($"[PurificationSystem] 存档文件为空或无效: {filename}");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[PurificationSystem] 加载失败（可能是首次运行）: {e.Message}");
            }
        }
        
        /// <summary>
        /// 删除存档文件
        /// </summary>
        public void DeleteSaveFile()
        {
            DeleteSaveFile(SaveFileName);
        }
        
        /// <summary>
        /// 删除指定存档文件
        /// </summary>
        /// <param name="filename">文件名</param>
        public void DeleteSaveFile(string filename)
        {
            try
            {
                MMSaveLoadManager.DeleteSave(filename, SaveFolderName);
                Debug.Log($"[PurificationSystem] 删除存档成功: {filename}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[PurificationSystem] 删除存档失败: {e.Message}");
            }
        }
        
        #endregion
        
        #region 调试可视化
        
        private void OnDrawGizmos()
        {
            if (!ShowDebugGizmos || !Application.isPlaying) return;
            
#if UNITY_EDITOR
            // 绘制所有指示物
            foreach (var indicator in _indicators)
            {
                // 绘制指示物辐射范围（半透明球体）
                Gizmos.color = new Color(IndicatorGizmosColor.r, IndicatorGizmosColor.g, IndicatorGizmosColor.b, 0.15f);
                Gizmos.DrawWireSphere(indicator.Position, indicator.RadiationRadius);
                
                // 绘制辐射范围圆圈（XZ平面）
                Gizmos.color = new Color(RangeGizmosColor.r, RangeGizmosColor.g, RangeGizmosColor.b, 0.4f);
                DrawWireCircle(indicator.Position, indicator.RadiationRadius, 32);
                
                // 绘制指示物中心点
                Gizmos.color = IndicatorGizmosColor;
                Gizmos.DrawSphere(indicator.Position, 0.3f);
                
                // 显示指示物名称、强度和辐射范围
                string label = $"{indicator.Name}\n强度: {indicator.ContributionValue:F1}\n范围: {indicator.RadiationRadius:F1}m";
                UnityEditor.Handles.Label(indicator.Position + Vector3.up * 0.5f, label);
            }
            
            // 绘制所有监听者
            Gizmos.color = ListenerGizmosColor;
            foreach (var listener in _listeners)
            {
                if (listener == null) continue;
                
                Vector3 position = listener.GetListenerPosition();
                Gizmos.DrawWireSphere(position, 0.5f);
                
                // 绘制监听者的查询范围
                Gizmos.color = new Color(ListenerGizmosColor.r, ListenerGizmosColor.g, ListenerGizmosColor.b, 0.2f);
                DrawWireCircle(position, DetectionRadius, 32);
                
                // 显示监听者名称
                string listenerLabel = listener.GetListenerName();
                UnityEditor.Handles.Label(position + Vector3.up * 1.0f, listenerLabel);
                
                Gizmos.color = ListenerGizmosColor;
            }
#endif
        }
        
        /// <summary>
        /// 在XZ平面绘制圆圈
        /// </summary>
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

