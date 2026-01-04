using MoreMountains.Tools;
using UnityEngine;

namespace Revive.GamePlay.Purification
{
    /// <summary>
    /// 净化度鲜花绽放控制器
    /// 根据净化度控制鲜花的生长和绽放状态
    /// </summary>
    public class PurificationFlowerBloom : MonoBehaviour, IPurificationListener
    {
        [Header("鲜花对象")]
        [Tooltip("鲜花根对象")]
        public GameObject FlowerRoot;
        
        [Tooltip("是否自动查找子对象")]
        public bool AutoFindFlowerInChildren = true;
        
        [Header("绽放设置")]
        [Tooltip("绽放阈值（净化度达到此值时绽放）")]
        [Range(0f, 1f)]
        public float BloomThreshold = 0.5f;
        
        [Tooltip("凋谢阈值（净化度低于此值时凋谢）")]
        [Range(0f, 1f)]
        public float WitherThreshold = 0.3f;
        
        [Tooltip("生长/凋谢速度")]
        public float TransitionSpeed = 1f;
        
        [Header("缩放设置")]
        [Tooltip("完全凋谢时的缩放")]
        public float WitheredScale = 0.1f;
        
        [Tooltip("完全绽放时的缩放")]
        public float BloomedScale = 1f;
        
        [Header("蝴蝶特效设置")]
        [Tooltip("蝴蝶特效配置资源（推荐）。如果设置，会使用配置资源的设置")]
        public ButterflyEffectConfig ButterflyConfig;
        
        [Tooltip("是否使用本地覆盖配置（勾选后使用下方的本地设置，忽略配置资源）")]
        public bool UseLocalOverride = false;
        
        [Header("本地覆盖配置（仅在UseLocalOverride时生效）")]
        [Tooltip("蝴蝶预制体数组（随机选择一个生成）")]
        public GameObject[] ButterflyPrefabs;
        
        [Tooltip("开花时生成蝴蝶的概率（0-1）")]
        [Range(0f, 1f)]
        public float ButterflySpawnChance = 0.3f;
        
        [Tooltip("蝴蝶生成位置偏移（相对于花朵）")]
        public Vector3 ButterflySpawnOffset = new Vector3(0f, 1f, 0f);
        
        [Tooltip("蝴蝶生成位置随机半径")]
        public float ButterflySpawnRandomRadius = 0.5f;
        
        [Tooltip("蝴蝶自动销毁时间（秒，0表示不自动销毁）")]
        public float ButterflyLifetime = 0f;
        
        [Tooltip("花凋谢时是否移除蝴蝶")]
        public bool RemoveButterflyOnWither = true;

        [Header("净化指示物设置")]
        [Tooltip("完全绽放时是否生成净化指示物")]
        public bool CreatePurificationIndicatorOnBloom = true;

        [Tooltip("指示物类型")]
        public string PurificationIndicatorType = "Flower";

        [Tooltip("事件贡献值")]
        public float PurificationContributionValue = 10f;

        [Tooltip("辐射范围(米)")]
        public float PurificationRadiationRadius = 8f;
        
        [Header("监听者设置")]
        [Tooltip("监听者名称（用于调试）")]
        public string ListenerName = "Flower";
        
        [Tooltip("是否在Start时自动注册")]
        public bool AutoRegisterOnStart = true;
        
        [Header("运行时信息")]
        [SerializeField, MMReadOnly]
        private float _currentPurificationLevel = 0f;

        [SerializeField, MMReadOnly]
        private float _debugToBloomThreshold;

        [SerializeField, MMReadOnly]
        private float _debugToWitherThreshold;
        
        [SerializeField, MMReadOnly]
        private FlowerState _currentState = FlowerState.Withered;
        
        [SerializeField, MMReadOnly]
        private float _growthProgress = 0f;
        
        private bool _isRegistered = false;
        private Vector3 _initialScale;
        private Vector3 _initialPosition;
        private GameObject _currentButterfly = null;
        private bool _purificationIndicatorCreated = false;
        
        /// <summary>
        /// 鲜花状态枚举
        /// </summary>
        public enum FlowerState
        {
            Withered,   // 凋谢
            Growing,    // 生长中
            Bloomed,    // 绽放
            Withering   // 凋谢中
        }
        
        private void Awake()
        {
            // 自动查找鲜花对象
            if (AutoFindFlowerInChildren && FlowerRoot == null)
            {
                // 先尝试查找子对象
                if (transform.childCount > 0)
                {
                    FlowerRoot = transform.GetChild(0).gameObject;
                }
                else
                {
                    FlowerRoot = gameObject;
                }
            }
            
            if (FlowerRoot == null)
            {
                FlowerRoot = gameObject;
            }
            
            // 保存初始缩放
            _initialScale = FlowerRoot.transform.localScale;
            
            // static purification listener, record initial position
            _initialPosition = FlowerRoot.transform.position;
            
            // 初始状态设置为凋谢
            FlowerRoot.transform.localScale = _initialScale * WitheredScale;
            _growthProgress = 0f;
        }
        
        private void Start()
        {
            // 如果没有设置监听者名称，使用GameObject名称
            if (string.IsNullOrEmpty(ListenerName))
            {
                ListenerName = gameObject.name;
            }
            
            // 自动注册
            if (AutoRegisterOnStart && PurificationSystem.HasInstance)
            {
                PurificationSystem.Instance.RegisterListener(this);
                _isRegistered = true;
            }
        }
        
        private void Update()
        {
            UpdateGrowthState();
        }
        
        private void OnDestroy()
        {
            // 注销监听
            if (_isRegistered && PurificationSystem.HasInstance)
            {
                PurificationSystem.Instance.UnregisterListener(this);
            }
            
            // 清理蝴蝶
            RemoveButterfly();
        }
        
        #region IPurificationListener 实现
        
        public void OnPurificationChanged(float purificationLevel, Vector3 position)
        {
            _currentPurificationLevel = purificationLevel;
            _debugToBloomThreshold = BloomThreshold - purificationLevel;
            _debugToWitherThreshold = WitherThreshold - purificationLevel;
            
            // 根据净化度更新状态
            if (purificationLevel >= BloomThreshold)
            {
                if (_currentState == FlowerState.Withered || _currentState == FlowerState.Withering)
                {
                    _currentState = FlowerState.Growing;
                    Debug.Log($"[{ListenerName}] 开始生长！净化度: {purificationLevel:F2}");
                }
            }
            else if (purificationLevel < WitherThreshold)
            {
                if (_currentState == FlowerState.Bloomed || _currentState == FlowerState.Growing)
                {
                    _currentState = FlowerState.Withering;
                    Debug.Log($"[{ListenerName}] 开始凋谢！净化度: {purificationLevel:F2}");
                }
            }
        }
        
        public Vector3 GetListenerPosition()
        {
            return _initialPosition;
        }
        
        public string GetListenerName()
        {
            return ListenerName;
        }
        
        #endregion
        
        #region 生长逻辑
        
        /// <summary>
        /// 更新生长状态
        /// </summary>
        private void UpdateGrowthState()
        {
            if (FlowerRoot == null) return;
            
            switch (_currentState)
            {
                case FlowerState.Growing:
                    _growthProgress += Time.deltaTime * TransitionSpeed;
                    if (_growthProgress >= 1f)
                    {
                        _growthProgress = 1f;
                        _currentState = FlowerState.Bloomed;
                        OnFullyBloomed();
                    }
                    break;
                    
                case FlowerState.Withering:
                    _growthProgress -= Time.deltaTime * TransitionSpeed;
                    if (_growthProgress <= 0f)
                    {
                        _growthProgress = 0f;
                        _currentState = FlowerState.Withered;
                        OnFullyWithered();
                    }
                    break;
            }
            
            // 应用缩放
            Vector3 targetScale = _initialScale * Mathf.Lerp(WitheredScale, BloomedScale, _growthProgress);
            FlowerRoot.transform.localScale = targetScale;
        }
        
        /// <summary>
        /// 完全绽放时调用
        /// </summary>
        private void OnFullyBloomed()
        {
            Debug.Log($"[{ListenerName}] 完全绽放！");
            
            // 尝试生成蝴蝶特效
            TrySpawnButterfly();

            TryCreatePurificationIndicator();
            
            // 这里可以添加其他粒子效果、音效等
            // 例如：播放绽放动画、发射花瓣粒子等
        }
        
        /// <summary>
        /// 完全凋谢时调用
        /// </summary>
        private void OnFullyWithered()
        {
            Debug.Log($"[{ListenerName}] 完全凋谢。");
            
            // 移除蝴蝶（如果配置了）
            bool shouldRemove = ShouldRemoveButterflyOnWither();
            if (shouldRemove)
            {
                RemoveButterfly();
            }
            
            // 这里可以添加凋谢效果
        }
        
        /// <summary>
        /// 尝试生成蝴蝶特效
        /// </summary>
        private void TrySpawnButterfly()
        {
            // 检查是否已经有蝴蝶存在
            if (_currentButterfly != null)
            {
                Debug.Log($"[{ListenerName}] 蝴蝶已存在，不重复生成");
                return;
            }
            
            // 获取有效配置
            bool useConfig = !UseLocalOverride && ButterflyConfig != null && ButterflyConfig.IsValid();
            
            if (!useConfig)
            {
                // 使用本地配置
                if (ButterflyPrefabs == null || ButterflyPrefabs.Length == 0)
                {
                    return;
                }
            }
            
            // 获取配置值
            float spawnChance = useConfig ? ButterflyConfig.SpawnChance : ButterflySpawnChance;
            GameObject[] prefabs = useConfig ? ButterflyConfig.ButterflyPrefabs : ButterflyPrefabs;
            Vector3 spawnOffset = useConfig ? ButterflyConfig.SpawnOffset : ButterflySpawnOffset;
            float randomRadius = useConfig ? ButterflyConfig.SpawnRandomRadius : ButterflySpawnRandomRadius;
            float lifetime = useConfig ? ButterflyConfig.Lifetime : ButterflyLifetime;
            
            // 概率判定
            float randomValue = Random.Range(0f, 1f);
            if (randomValue > spawnChance)
            {
                Debug.Log($"[{ListenerName}] 蝴蝶生成概率判定失败: {randomValue:F2} > {spawnChance:F2}");
                return;
            }
            
            // 随机选择一个蝴蝶预制体
            GameObject prefab = prefabs[Random.Range(0, prefabs.Length)];
            if (prefab == null)
            {
                Debug.LogWarning($"[{ListenerName}] 选中的蝴蝶预制体为null");
                return;
            }
            
            // 计算生成位置（花朵位置 + 偏移 + 随机）
            Vector3 spawnPosition = FlowerRoot.transform.position + spawnOffset;
            if (randomRadius > 0f)
            {
                Vector2 randomCircle = Random.insideUnitCircle * randomRadius;
                spawnPosition += new Vector3(randomCircle.x, 0f, randomCircle.y);
            }
            
            // 生成蝴蝶
            _currentButterfly = Instantiate(prefab, spawnPosition, Quaternion.identity);
            _currentButterfly.name = $"{prefab.name}_From_{ListenerName}";
            
            string configSource = useConfig ? $"配置资源: {ButterflyConfig.name}" : "本地配置";
            Debug.Log($"[{ListenerName}] 成功生成蝴蝶特效: {_currentButterfly.name} 在位置 {spawnPosition} (来源: {configSource})");
            
            // 如果设置了生命周期，自动销毁
            if (lifetime > 0f)
            {
                Destroy(_currentButterfly, lifetime);
            }
        }
        
        /// <summary>
        /// 判断凋谢时是否应该移除蝴蝶
        /// </summary>
        private bool ShouldRemoveButterflyOnWither()
        {
            bool useConfig = !UseLocalOverride && ButterflyConfig != null;
            return useConfig ? ButterflyConfig.RemoveOnWither : RemoveButterflyOnWither;
        }
        
        /// <summary>
        /// 移除当前蝴蝶
        /// </summary>
        private void RemoveButterfly()
        {
            if (_currentButterfly != null)
            {
                Debug.Log($"[{ListenerName}] 移除蝴蝶: {_currentButterfly.name}");
                Destroy(_currentButterfly);
                _currentButterfly = null;
            }
        }
        
        #endregion
        
        #region 公共方法
        
        /// <summary>
        /// 手动注册监听
        /// </summary>
        public void RegisterListener()
        {
            if (!_isRegistered && PurificationSystem.HasInstance)
            {
                PurificationSystem.Instance.RegisterListener(this);
                _isRegistered = true;
            }
        }
        
        /// <summary>
        /// 手动注销监听
        /// </summary>
        public void UnregisterListener()
        {
            if (_isRegistered && PurificationSystem.HasInstance)
            {
                PurificationSystem.Instance.UnregisterListener(this);
                _isRegistered = false;
            }
        }
        
        /// <summary>
        /// 手动请求更新
        /// </summary>
        public void RequestUpdate()
        {
            if (_isRegistered && PurificationSystem.HasInstance)
            {
                PurificationSystem.Instance.RequestUpdate(this);
            }
        }
        
        /// <summary>
        /// 获取当前状态
        /// </summary>
        public FlowerState GetCurrentState()
        {
            return _currentState;
        }
        
        /// <summary>
        /// 获取生长进度 (0-1)
        /// </summary>
        public float GetGrowthProgress()
        {
            return _growthProgress;
        }
        
        /// <summary>
        /// 强制设置为绽放状态
        /// </summary>
        public void ForceBloomed()
        {
            _currentState = FlowerState.Bloomed;
            _growthProgress = 1f;
            if (FlowerRoot != null)
            {
                FlowerRoot.transform.localScale = _initialScale * BloomedScale;
            }
            
            // 尝试生成蝴蝶
            TrySpawnButterfly();

            TryCreatePurificationIndicator();
        }

        private void TryCreatePurificationIndicator()
        {
            if (!CreatePurificationIndicatorOnBloom)
                return;

            if (_purificationIndicatorCreated)
                return;

            if (!PurificationSystem.HasInstance)
                return;

            string indicatorName = $"FlowerBloom_{ListenerName}_{gameObject.GetInstanceID()}";

            var existingIndicators = PurificationSystem.Instance.GetAllIndicators();
            for (int i = 0; i < existingIndicators.Count; i++)
            {
                var indicator = existingIndicators[i];
                if (indicator != null && indicator.Name == indicatorName)
                {
                    _purificationIndicatorCreated = true;
                    return;
                }
            }

            PurificationSystem.Instance.AddIndicator(
                indicatorName,
                _initialPosition,
                PurificationContributionValue,
                PurificationIndicatorType,
                PurificationRadiationRadius);

            _purificationIndicatorCreated = true;
        }
        
        /// <summary>
        /// 强制设置为凋谢状态
        /// </summary>
        public void ForceWithered()
        {
            _currentState = FlowerState.Withered;
            _growthProgress = 0f;
            if (FlowerRoot != null)
            {
                FlowerRoot.transform.localScale = _initialScale * WitheredScale;
            }
            
            // 移除蝴蝶
            bool shouldRemove = ShouldRemoveButterflyOnWither();
            if (shouldRemove)
            {
                RemoveButterfly();
            }
        }
        
        #endregion
    }
}

