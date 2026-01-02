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
        
        [Header("监听者设置")]
        [Tooltip("监听者名称（用于调试）")]
        public string ListenerName = "Flower";
        
        [Tooltip("是否在Start时自动注册")]
        public bool AutoRegisterOnStart = true;
        
        [Header("运行时信息")]
        [SerializeField, MMReadOnly]
        private float _currentPurificationLevel = 0f;
        
        [SerializeField, MMReadOnly]
        private FlowerState _currentState = FlowerState.Withered;
        
        [SerializeField, MMReadOnly]
        private float _growthProgress = 0f;
        
        private bool _isRegistered = false;
        private Vector3 _initialScale;
        
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
        }
        
        #region IPurificationListener 实现
        
        public void OnPurificationChanged(float purificationLevel, Vector3 position)
        {
            _currentPurificationLevel = purificationLevel;
            
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
            return transform.position;
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
            
            // 这里可以添加粒子效果、音效等
            // 例如：播放绽放动画、发射花瓣粒子等
        }
        
        /// <summary>
        /// 完全凋谢时调用
        /// </summary>
        private void OnFullyWithered()
        {
            Debug.Log($"[{ListenerName}] 完全凋谢。");
            
            // 这里可以添加凋谢效果
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
        }
        
        #endregion
    }
}

