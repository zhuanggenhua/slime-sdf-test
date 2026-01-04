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
        private static readonly int PurificationFieldTexId = Shader.PropertyToID("_PurificationFieldTex");
        private static readonly int PurificationFieldParamsId = Shader.PropertyToID("_PurificationFieldParams");
        private static readonly int PurificationFieldResId = Shader.PropertyToID("_PurificationFieldRes");

        [ChineseHeader("净化系统配置")]
        [Tooltip("识别半径（米），在此半径内的指示物会被计入净化度")]
        public float DetectionRadius = 10f;
        
        [Tooltip("目标净化值，达到此值时净化度为1.0")]
        public float TargetPurificationValue = 100f;

        [ChineseHeader("净化空间场")]
        [Tooltip("优先从 Terrain 自动推导净化场范围")]
        public bool AutoFieldBoundsFromTerrain = true;

        [Tooltip("无法从 Terrain 推导时使用的净化场锚点（中心）")]
        public Transform FieldAnchor;

        [Tooltip("净化场尺寸（米），当未找到 Terrain 时使用")]
        public Vector2 FieldSizeWorld = new(250f, 250f);

        [Tooltip("净化场分辨率（单边格子数）")]
        [Min(8)]
        public int FieldResolution = 1024;

        [ChineseHeader("净化采样")]
        [Tooltip("查询净化度时是否使用空间场（O(1)）")]
        public bool UseSpatialField = true;
        
        [ChineseHeader("调试设置")]
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

        public event Action<Vector3, float, float, string> StampAdded;
        
        // 统计信息（用于Inspector显示）
        [ChineseHeader("运行时统计")]
        [SerializeField, MMReadOnly]
        private int _indicatorCount = 0;
        
        [SerializeField, MMReadOnly]
        private int _listenerCount = 0;

        [SerializeField, MMReadOnly]
        private bool _fieldInitialized = false;

        [SerializeField, MMReadOnly]
        private Vector3 _fieldOriginWorld;

        [SerializeField, MMReadOnly]
        private Vector2 _fieldSizeWorldRuntime;

        [SerializeField, MMReadOnly]
        private int _fieldResolutionRuntime;

        private float[] _contributionField;
        private float _cellSizeX;
        private float _cellSizeZ;

        private Texture2D _fieldTexture;
        private float[] _fieldTextureData;
        private bool _fieldTextureDirty;
        
        protected override void Awake()
        {
            base.Awake();
        }

        private void OnDestroy()
        {
            if (_fieldTexture != null)
            {
                Destroy(_fieldTexture);
                _fieldTexture = null;
            }
        }
        
        private void Update()
        {
            // 更新统计信息
            _indicatorCount = _indicators.Count;
            _listenerCount = _listeners.Count;

            if (UseSpatialField)
            {
                UploadFieldTextureIfDirty();
            }
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
            if (UseSpatialField)
            {
                EnsureContributionFieldInitialized();
                StampContribution(position, contributionValue, radiationRadius);
                StampAdded?.Invoke(position, contributionValue, radiationRadius, indicatorType);
            }

            PurificationIndicator indicator = new PurificationIndicator(pName, position, contributionValue, indicatorType, radiationRadius);
            _indicators.Add(indicator);
            
            Debug.Log($"[PurificationSystem] 添加指示物: {indicator}");
            IndicatorAdded?.Invoke(indicator);
            
            // 通知半径范围内的监听者
            NotifyListenersInRange(position);
            
            return indicator;
        }

        public void AddStamp(Vector3 position, float contributionValue, string indicatorType, float radiationRadius = 8f)
        {
            if (UseSpatialField)
            {
                EnsureContributionFieldInitialized();
                StampContribution(position, contributionValue, radiationRadius);
                StampAdded?.Invoke(position, contributionValue, radiationRadius, indicatorType);
            }

            NotifyListenersInRange(position);
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

            if (UseSpatialField)
            {
                EnsureContributionFieldInitialized();
                ClearContributionField();
            }

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

        public bool HasIndicatorInRange(Vector3 position, float radius, string indicatorType)
        {
            float r = Mathf.Max(0f, radius);
            if (r <= 0f)
                return false;

            float r2 = r * r;
            bool filterType = !string.IsNullOrEmpty(indicatorType);

            for (int i = 0; i < _indicators.Count; i++)
            {
                PurificationIndicator indicator = _indicators[i];
                if (indicator == null)
                    continue;

                if (filterType && !string.Equals(indicator.IndicatorType, indicatorType, StringComparison.Ordinal))
                    continue;

                Vector3 d = indicator.Position - position;
                if (d.sqrMagnitude <= r2)
                    return true;
            }

            return false;
        }
        
        #endregion

        #region 空间场

        private void EnsureContributionFieldInitialized()
        {
            int res = Mathf.Max(8, FieldResolution);
            bool needRecreate = !_fieldInitialized || _contributionField == null || _fieldResolutionRuntime != res;

            if (!needRecreate)
                return;

            if (!TryResolveFieldBounds(out Vector3 originWorld, out Vector2 sizeWorld))
            {
                Vector3 center = FieldAnchor != null ? FieldAnchor.position : transform.position;
                originWorld = new Vector3(center.x - FieldSizeWorld.x * 0.5f, 0f, center.z - FieldSizeWorld.y * 0.5f);
                sizeWorld = new Vector2(FieldSizeWorld.x, FieldSizeWorld.y);
            }

            sizeWorld.x = Mathf.Max(0.01f, sizeWorld.x);
            sizeWorld.y = Mathf.Max(0.01f, sizeWorld.y);

            _fieldOriginWorld = originWorld;
            _fieldSizeWorldRuntime = sizeWorld;
            _fieldResolutionRuntime = res;

            _cellSizeX = sizeWorld.x / (res - 1);
            _cellSizeZ = sizeWorld.y / (res - 1);

            _contributionField = new float[res * res];
            _fieldTextureData = new float[res * res];
            EnsureFieldTextureInitialized(res);
            _fieldTextureDirty = true;
            _fieldInitialized = true;
        }

        private void EnsureFieldTextureInitialized(int res)
        {
            if (_fieldTexture != null && _fieldTexture.width == res && _fieldTexture.height == res)
                return;

            if (_fieldTexture != null)
            {
                Destroy(_fieldTexture);
                _fieldTexture = null;
            }

            _fieldTexture = new Texture2D(res, res, TextureFormat.RFloat, false, true)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "PurificationFieldTex"
            };

            Shader.SetGlobalTexture(PurificationFieldTexId, _fieldTexture);
        }

        private bool TryResolveFieldBounds(out Vector3 originWorld, out Vector2 sizeWorld)
        {
            originWorld = default;
            sizeWorld = default;

            if (AutoFieldBoundsFromTerrain && TryGetTerrainBoundsWorld(out Bounds b))
            {
                originWorld = new Vector3(b.min.x, 0f, b.min.z);
                sizeWorld = new Vector2(b.size.x, b.size.z);
                return true;
            }

            if (FieldAnchor != null)
            {
                Vector3 center = FieldAnchor.position;
                originWorld = new Vector3(center.x - FieldSizeWorld.x * 0.5f, 0f, center.z - FieldSizeWorld.y * 0.5f);
                sizeWorld = new Vector2(FieldSizeWorld.x, FieldSizeWorld.y);
                return true;
            }

            return false;
        }

        private bool TryGetTerrainBoundsWorld(out Bounds bounds)
        {
            bounds = default;

            Terrain[] terrains = Terrain.activeTerrains;
            if (terrains == null || terrains.Length == 0)
            {
                terrains = FindObjectsByType<Terrain>(FindObjectsSortMode.None);
            }

            bool hasAny = false;
            for (int i = 0; i < terrains.Length; i++)
            {
                Terrain t = terrains[i];
                if (t == null || t.terrainData == null)
                    continue;

                Vector3 pos = t.GetPosition();
                Vector3 size = t.terrainData.size;
                Bounds tb = new Bounds(pos + size * 0.5f, size);
                if (!hasAny)
                {
                    bounds = tb;
                    hasAny = true;
                }
                else
                {
                    bounds.Encapsulate(tb.min);
                    bounds.Encapsulate(tb.max);
                }
            }

            return hasAny;
        }

        private void ClearContributionField()
        {
            if (_contributionField == null)
                return;
            Array.Clear(_contributionField, 0, _contributionField.Length);

            _fieldTextureDirty = true;
        }

        private void RebuildContributionFieldFromIndicators()
        {
            ClearContributionField();
            for (int i = 0; i < _indicators.Count; i++)
            {
                PurificationIndicator indicator = _indicators[i];
                if (indicator == null)
                    continue;
                StampContribution(indicator.Position, indicator.ContributionValue, indicator.RadiationRadius);
            }

            _fieldTextureDirty = true;
        }

        private void StampContribution(Vector3 centerWorld, float contributionValue, float radiusWorld)
        {
            if (_contributionField == null)
                return;
            if (radiusWorld <= 0f || contributionValue <= 0f)
                return;

            int res = _fieldResolutionRuntime;
            float sizeX = _fieldSizeWorldRuntime.x;
            float sizeZ = _fieldSizeWorldRuntime.y;
            if (sizeX <= 0f || sizeZ <= 0f)
                return;

            float relX = centerWorld.x - _fieldOriginWorld.x;
            float relZ = centerWorld.z - _fieldOriginWorld.z;
            if (relX < -radiusWorld || relZ < -radiusWorld || relX > sizeX + radiusWorld || relZ > sizeZ + radiusWorld)
                return;

            float invCellX = _cellSizeX > 0f ? 1f / _cellSizeX : 0f;
            float invCellZ = _cellSizeZ > 0f ? 1f / _cellSizeZ : 0f;
            int minX = Mathf.Clamp(Mathf.FloorToInt((relX - radiusWorld) * invCellX), 0, res - 1);
            int maxX = Mathf.Clamp(Mathf.CeilToInt((relX + radiusWorld) * invCellX), 0, res - 1);
            int minZ = Mathf.Clamp(Mathf.FloorToInt((relZ - radiusWorld) * invCellZ), 0, res - 1);
            int maxZ = Mathf.Clamp(Mathf.CeilToInt((relZ + radiusWorld) * invCellZ), 0, res - 1);

            float targetMax = Mathf.Max(0f, TargetPurificationValue);

            for (int z = minZ; z <= maxZ; z++)
            {
                float cz = _fieldOriginWorld.z + z * _cellSizeZ;
                float dz = cz - centerWorld.z;
                for (int x = minX; x <= maxX; x++)
                {
                    float cx = _fieldOriginWorld.x + x * _cellSizeX;
                    float dx = cx - centerWorld.x;
                    float d = Mathf.Sqrt(dx * dx + dz * dz);
                    if (d >= radiusWorld)
                        continue;

                    float w = 1f - (d / radiusWorld);
                    if (w <= 0f)
                        continue;

                    int idx = z * res + x;
                    float next = _contributionField[idx] + contributionValue * w;
                    _contributionField[idx] = targetMax > 0f ? Mathf.Min(targetMax, next) : next;
                }
            }

            _fieldTextureDirty = true;
        }

        private float SampleContribution(Vector3 positionWorld)
        {
            if (_contributionField == null)
                return 0f;

            float sizeX = _fieldSizeWorldRuntime.x;
            float sizeZ = _fieldSizeWorldRuntime.y;
            if (sizeX <= 0f || sizeZ <= 0f)
                return 0f;

            float relX = positionWorld.x - _fieldOriginWorld.x;
            float relZ = positionWorld.z - _fieldOriginWorld.z;
            if (relX < 0f || relZ < 0f || relX > sizeX || relZ > sizeZ)
                return 0f;

            int res = _fieldResolutionRuntime;
            float u = relX / sizeX;
            float v = relZ / sizeZ;

            float fx = u * (res - 1);
            float fz = v * (res - 1);

            int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, res - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt(fz), 0, res - 1);
            int x1 = Mathf.Clamp(x0 + 1, 0, res - 1);
            int z1 = Mathf.Clamp(z0 + 1, 0, res - 1);

            float tx = fx - x0;
            float tz = fz - z0;

            float c00 = _contributionField[z0 * res + x0];
            float c10 = _contributionField[z0 * res + x1];
            float c01 = _contributionField[z1 * res + x0];
            float c11 = _contributionField[z1 * res + x1];

            float cx0 = Mathf.Lerp(c00, c10, tx);
            float cx1 = Mathf.Lerp(c01, c11, tx);
            return Mathf.Lerp(cx0, cx1, tz);
        }

        private void UploadFieldTextureIfDirty()
        {
            if (!_fieldInitialized || _contributionField == null)
                return;
            if (!_fieldTextureDirty)
                return;

            int res = _fieldResolutionRuntime;
            EnsureFieldTextureInitialized(res);

            float invTarget = TargetPurificationValue > 0f ? 1f / TargetPurificationValue : 0f;

            for (int i = 0; i < _contributionField.Length; i++)
            {
                _fieldTextureData[i] = _contributionField[i] * invTarget;
            }

            _fieldTexture.SetPixelData(_fieldTextureData, 0);
            _fieldTexture.Apply(false, false);

            Shader.SetGlobalVector(
                PurificationFieldParamsId,
                new Vector4(_fieldOriginWorld.x, _fieldOriginWorld.z, _fieldSizeWorldRuntime.x, _fieldSizeWorldRuntime.y));
            Shader.SetGlobalFloat(PurificationFieldResId, _fieldResolutionRuntime);

            _fieldTextureDirty = false;
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
            if (UseSpatialField)
            {
                EnsureContributionFieldInitialized();
                float totalContribution = SampleContribution(position);
                return Mathf.Min(1f, totalContribution / TargetPurificationValue);
            }

            if (radius < 0)
            {
                radius = DetectionRadius;
            }

            float totalContributionLegacy = 0f;
            foreach (var indicator in _indicators)
            {
                float weight = indicator.CalculateIntersectionWeight(position, radius);
                if (weight > 0f)
                {
                    totalContributionLegacy += indicator.ContributionValue * weight;
                }
            }

            float purificationLevelLegacy = Mathf.Min(1f, totalContributionLegacy / TargetPurificationValue);
            return purificationLevelLegacy;
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
            if (UseSpatialField)
            {
                EnsureContributionFieldInitialized();
                totalContribution = SampleContribution(position);
                indicatorCount = 0;
                return Mathf.Min(1f, totalContribution / TargetPurificationValue);
            }

            if (radius < 0)
            {
                radius = DetectionRadius;
            }

            totalContribution = 0f;
            indicatorCount = 0;

            foreach (var indicator in _indicators)
            {
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

                    if (UseSpatialField)
                    {
                        EnsureContributionFieldInitialized();
                        RebuildContributionFieldFromIndicators();
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
                
                // 显示指示物名称、类型、强度和辐射范围
                string label = $"{indicator.Name}\n类型: {indicator.IndicatorType}\n强度: {indicator.ContributionValue:F1}\n范围: {indicator.RadiationRadius:F1}m";
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

