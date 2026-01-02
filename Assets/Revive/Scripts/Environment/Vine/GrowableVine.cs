using MoreMountains.Tools;
using Revive.GamePlay.Purification;
using Revive.Slime;
using UnityEngine;

namespace Revive.Environment
{
    /// <summary>
    /// 可生长藤蔓 - 净化度监听者
    /// 当净化度达到阈值后生长，并激活为可攀爬表面
    /// </summary>
    [AddComponentMenu("Revive/Environment/Growable Vine")]
    public class GrowableVine : MonoBehaviour, IPurificationListener
    {
        [Header("藤蔓对象")]
        [Tooltip("藤蔓根对象（用于获取材质）")]
        public GameObject VineRoot;
        
        [Tooltip("是否自动查找子对象")]
        public bool AutoFindVineInChildren = true;
        
        [Header("生长设置")]
        [Tooltip("初始生长进度（枯萎状态下的最小进度，0=完全不可见）")]
        [Range(0f, 0.5f)]
        public float InitialGrowthProgress = 0.2f;
        
        [Tooltip("生长阈值（净化度达到此值时开始生长）")]
        [Range(0f, 1f)]
        public float GrowthThreshold = 0.6f;
        
        [Tooltip("枯萎阈值（净化度低于此值时枯萎）")]
        [Range(0f, 1f)]
        public float WitherThreshold = 0.4f;
        
        [Tooltip("基础生长速度")]
        public float BaseGrowthSpeed = 0.5f;
        
        [Tooltip("基础枯萎速度")]
        public float BaseWitherSpeed = 0.3f;
        
        [Tooltip("生长速度受净化度影响（勾选后净化度越高生长越快）")]
        public bool PurificationAffectsSpeed = true;
        
        [Tooltip("速度影响强度（净化度对速度的影响程度）")]
        [Range(0f, 2f)]
        public float SpeedInfluenceStrength = 1.0f;
        
        [Tooltip("生长曲线（控制生长的非线性变化）")]
        public AnimationCurve GrowthCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
        
        [Header("高级生长控制")]
        [Tooltip("分阶段生长（不同阶段不同速度）")]
        public bool UsePhaseGrowth = false;
        
        [Tooltip("早期生长阶段速度倍率 (0-30%)")]
        [Range(0.1f, 3f)]
        public float EarlyPhaseSpeedMultiplier = 0.5f;
        
        [Tooltip("中期生长阶段速度倍率 (30-70%)")]
        [Range(0.1f, 3f)]
        public float MidPhaseSpeedMultiplier = 1.5f;
        
        [Tooltip("后期生长阶段速度倍率 (70-100%)")]
        [Range(0.1f, 3f)]
        public float LatePhaseSpeedMultiplier = 0.8f;
        
        [Header("材质设置")]
        [Tooltip("材质属性名（Shader中的生长参数，默认_GrowthProgress）")]
        public string GrowthPropertyName = "_GrowthProgress";
        
        [Tooltip("藤蔓渲染器（用于获取材质）")]
        public Renderer VineRenderer;
        
        [Tooltip("是否使用共享材质（false则运行时创建实例）")]
        public bool UseSharedMaterial = false;
        
        [Header("碰撞体设置")]
        [Tooltip("攀爬碰撞体（生长后激活）")]
        public BoxCollider ClimbCollider;
        
        [Tooltip("是否自动查找BoxCollider")]
        public bool AutoFindCollider = true;
        
        [Tooltip("碰撞体表面摩擦力")]
        [Range(0f, 1f)]
        public float SurfaceFriction = 0.3f;
        
        [Header("监听者设置")]
        [Tooltip("监听者名称（用于调试）")]
        public string ListenerName = "Vine";
        
        [Tooltip("是否在Start时自动注册")]
        public bool AutoRegisterOnStart = true;
        
        [Header("运行时信息")]
        [SerializeField, MMReadOnly]
        private float _currentPurificationLevel = 0f;
        
        [SerializeField, MMReadOnly]
        private VineState _currentState = VineState.Withered;
        
        [SerializeField, MMReadOnly]
        private float _growthProgress = 0f;
        
        private bool _isRegistered = false;
        private Material _vineMaterial;
        private SlimeColliderInfo _colliderInfo;
        
        /// <summary>
        /// 藤蔓状态枚举
        /// </summary>
        public enum VineState
        {
            Withered,   // 枯萎/未生长
            Growing,    // 生长中
            Grown,      // 完全生长
            Withering   // 枯萎中
        }
        
        private void Awake()
        {
            // 自动查找藤蔓对象
            if (AutoFindVineInChildren && VineRoot == null)
            {
                if (transform.childCount > 0)
                {
                    VineRoot = transform.GetChild(0).gameObject;
                }
                else
                {
                    VineRoot = gameObject;
                }
            }
            
            if (VineRoot == null)
            {
                VineRoot = gameObject;
            }
            
            // 获取或查找Renderer
            if (VineRenderer == null)
            {
                VineRenderer = VineRoot.GetComponentInChildren<Renderer>();
            }
            
            // 准备材质
            if (VineRenderer != null)
            {
                if (UseSharedMaterial)
                {
                    _vineMaterial = VineRenderer.sharedMaterial;
                }
                else
                {
                    // 创建材质实例
                    _vineMaterial = VineRenderer.material;
                }
                
                // 初始化为未生长状态（显示根部）
                if (_vineMaterial.HasProperty(GrowthPropertyName))
                {
                    float initialProgress = GrowthCurve.Evaluate(InitialGrowthProgress);
                    _vineMaterial.SetFloat(GrowthPropertyName, initialProgress);
                }
            }
            else
            {
                Debug.LogWarning($"[GrowableVine] {ListenerName} 未找到Renderer组件", this);
            }
            
            // 自动查找碰撞体
            if (AutoFindCollider && ClimbCollider == null)
            {
                ClimbCollider = VineRoot.GetComponentInChildren<BoxCollider>();
            }
            
            // 初始化碰撞体状态
            if (ClimbCollider != null)
            {
                // 初始状态禁用碰撞体
                ClimbCollider.enabled = false;
                
                // 添加或获取SlimeColliderInfo组件
                _colliderInfo = ClimbCollider.GetComponent<SlimeColliderInfo>();
                if (_colliderInfo == null)
                {
                    _colliderInfo = ClimbCollider.gameObject.AddComponent<SlimeColliderInfo>();
                }
                
                // 设置为可攀爬类型
                _colliderInfo.colliderType = SlimeColliderInfo.ColliderType.Climbable;
                _colliderInfo.surfaceFriction = SurfaceFriction;
            }
            else
            {
                Debug.LogWarning($"[GrowableVine] {ListenerName} 未找到BoxCollider组件", this);
            }
            
            // 初始状态（从初始进度开始，显示根部）
            _growthProgress = InitialGrowthProgress;
            _currentState = VineState.Withered;
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
            
            // 清理材质实例
            if (!UseSharedMaterial && _vineMaterial != null)
            {
                Destroy(_vineMaterial);
            }
        }
        
        #region IPurificationListener 实现
        
        public void OnPurificationChanged(float purificationLevel, Vector3 position)
        {
            _currentPurificationLevel = purificationLevel;
            
            // 根据净化度更新状态
            if (purificationLevel >= GrowthThreshold)
            {
                if (_currentState == VineState.Withered || _currentState == VineState.Withering)
                {
                    _currentState = VineState.Growing;
                    Debug.Log($"[GrowableVine] {ListenerName} 开始生长！净化度: {purificationLevel:F2}");
                }
            }
            else if (purificationLevel < WitherThreshold)
            {
                if (_currentState == VineState.Grown || _currentState == VineState.Growing)
                {
                    _currentState = VineState.Withering;
                    Debug.Log($"[GrowableVine] {ListenerName} 开始枯萎！净化度: {purificationLevel:F2}");
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
            switch (_currentState)
            {
                case VineState.Growing:
                    {
                        // 计算本帧的生长速度
                        float growthSpeed = CalculateGrowthSpeed(_growthProgress, true);
                        _growthProgress += Time.deltaTime * growthSpeed;
                        
                        if (_growthProgress >= 1f)
                        {
                            _growthProgress = 1f;
                            _currentState = VineState.Grown;
                            OnFullyGrown();
                        }
                    }
                    break;
                    
                case VineState.Withering:
                    {
                        // 计算本帧的枯萎速度
                        float witherSpeed = CalculateGrowthSpeed(_growthProgress, false);
                        _growthProgress -= Time.deltaTime * witherSpeed;
                        
                        // 枯萎到初始进度（保留根部）
                        if (_growthProgress <= InitialGrowthProgress)
                        {
                            _growthProgress = InitialGrowthProgress;
                            _currentState = VineState.Withered;
                            OnFullyWithered();
                        }
                    }
                    break;
            }
            
            // 应用生长曲线
            float curvedProgress = GrowthCurve.Evaluate(_growthProgress);
            
            // 更新材质生长参数（使用曲线后的值）
            UpdateMaterialGrowth(curvedProgress);
            
            // 更新碰撞体状态（使用曲线后的值，生长超过50%时激活）
            UpdateColliderState(curvedProgress);
        }
        
        /// <summary>
        /// 计算当前的生长/枯萎速度
        /// </summary>
        /// <param name="currentProgress">当前生长进度</param>
        /// <param name="isGrowing">是生长还是枯萎</param>
        /// <returns>速度值</returns>
        private float CalculateGrowthSpeed(float currentProgress, bool isGrowing)
        {
            float baseSpeed = isGrowing ? BaseGrowthSpeed : BaseWitherSpeed;
            
            // 1. 净化度影响速度
            if (PurificationAffectsSpeed && isGrowing)
            {
                // 净化度越高，生长越快
                // 速度范围：baseSpeed * [0.5, 1.5]
                float purificationMultiplier = Mathf.Lerp(0.5f, 1.5f, _currentPurificationLevel);
                baseSpeed *= Mathf.Lerp(1.0f, purificationMultiplier, SpeedInfluenceStrength);
            }
            
            // 2. 分阶段生长速度
            if (UsePhaseGrowth && isGrowing)
            {
                float phaseMultiplier = 1.0f;
                
                if (currentProgress < 0.3f)
                {
                    // 早期阶段 (0-30%)
                    phaseMultiplier = EarlyPhaseSpeedMultiplier;
                }
                else if (currentProgress < 0.7f)
                {
                    // 中期阶段 (30-70%)
                    phaseMultiplier = MidPhaseSpeedMultiplier;
                }
                else
                {
                    // 后期阶段 (70-100%)
                    phaseMultiplier = LatePhaseSpeedMultiplier;
                }
                
                baseSpeed *= phaseMultiplier;
            }
            
            return baseSpeed;
        }
        
        /// <summary>
        /// 更新材质生长参数
        /// </summary>
        private void UpdateMaterialGrowth(float progress)
        {
            if (_vineMaterial != null && _vineMaterial.HasProperty(GrowthPropertyName))
            {
                _vineMaterial.SetFloat(GrowthPropertyName, progress);
            }
        }
        
        /// <summary>
        /// 更新碰撞体状态
        /// </summary>
        private void UpdateColliderState(float progress)
        {
            if (ClimbCollider != null)
            {
                // 生长超过50%时激活碰撞体，否则禁用
                bool shouldEnable = progress >= 0.5f;
                if (ClimbCollider.enabled != shouldEnable)
                {
                    ClimbCollider.enabled = shouldEnable;
                }
            }
        }
        
        /// <summary>
        /// 完全生长时调用
        /// </summary>
        private void OnFullyGrown()
        {
            Debug.Log($"[GrowableVine] {ListenerName} 完全生长！可攀爬表面已激活");
            
            // 确保碰撞体和攀爬组件都已激活
            if (ClimbCollider != null)
            {
                ClimbCollider.enabled = true;
            }
            
            // 这里可以添加生长完成的特效、音效等
        }
        
        /// <summary>
        /// 完全枯萎时调用（保留根部可见）
        /// </summary>
        private void OnFullyWithered()
        {
            Debug.Log($"[GrowableVine] {ListenerName} 枯萎至根部状态（进度: {InitialGrowthProgress:P0}）");
            
            // 确保碰撞体已禁用
            if (ClimbCollider != null)
            {
                ClimbCollider.enabled = false;
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
        public VineState GetCurrentState()
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
        /// 设置生长进度（手动控制，不受净化度影响）
        /// </summary>
        /// <param name="progress">进度值 0-1</param>
        /// <param name="applyCurve">是否应用生长曲线</param>
        public void SetGrowthProgress(float progress, bool applyCurve = true)
        {
            _growthProgress = Mathf.Clamp01(progress);
            float displayProgress = applyCurve ? GrowthCurve.Evaluate(_growthProgress) : _growthProgress;
            UpdateMaterialGrowth(displayProgress);
            UpdateColliderState(displayProgress);
        }
        
        /// <summary>
        /// 设置生长速度倍率（临时修改，用于特殊效果）
        /// </summary>
        public void SetGrowthSpeedMultiplier(float multiplier)
        {
            BaseGrowthSpeed *= multiplier;
            BaseWitherSpeed *= multiplier;
        }
        
        /// <summary>
        /// 模拟净化度变化（用于测试）
        /// </summary>
        public void SimulatePurification(float level)
        {
            OnPurificationChanged(level, transform.position);
        }
        
        /// <summary>
        /// 强制设置为完全生长状态
        /// </summary>
        public void ForceGrown()
        {
            _currentState = VineState.Grown;
            _growthProgress = 1f;
            UpdateMaterialGrowth(1f);
            if (ClimbCollider != null)
            {
                ClimbCollider.enabled = true;
            }
        }
        
        /// <summary>
        /// 强制设置为枯萎状态（保留根部）
        /// </summary>
        public void ForceWithered()
        {
            _currentState = VineState.Withered;
            _growthProgress = InitialGrowthProgress;
            float curvedProgress = GrowthCurve.Evaluate(InitialGrowthProgress);
            UpdateMaterialGrowth(curvedProgress);
            if (ClimbCollider != null)
            {
                ClimbCollider.enabled = false;
            }
        }
        
        #endregion
        
#if UNITY_EDITOR
        /// <summary>
        /// 在Scene视图中显示调试信息
        /// </summary>
        private void OnDrawGizmosSelected()
        {
            // 绘制监听范围（如果已注册）
            if (_isRegistered && PurificationSystem.HasInstance)
            {
                Gizmos.color = new Color(0.3f, 1f, 0.3f, 0.3f);
                DrawWireCircle(transform.position, PurificationSystem.Instance.DetectionRadius, 32);
            }
            
            // 显示生长信息
            if (Application.isPlaying)
            {
                float curvedProgress = GrowthCurve.Evaluate(_growthProgress);
                float currentSpeed = 0f;
                if (_currentState == VineState.Growing || _currentState == VineState.Withering)
                {
                    currentSpeed = CalculateGrowthSpeed(_growthProgress, _currentState == VineState.Growing);
                }
                
                string info = $"{ListenerName}\n" +
                             $"状态: {_currentState}\n" +
                             $"进度: {_growthProgress:P0} (曲线后: {curvedProgress:P0})\n" +
                             $"净化度: {_currentPurificationLevel:P0}\n" +
                             $"当前速度: {currentSpeed:F2}/s";
                
                UnityEditor.Handles.Label(transform.position + Vector3.up * 1.5f, info);
                
                // 绘制生长进度条
                Vector3 barStart = transform.position + Vector3.up * 0.5f + Vector3.left * 0.5f;
                Vector3 barEnd = barStart + Vector3.right * 1f;
                Vector3 barCurrent = Vector3.Lerp(barStart, barEnd, curvedProgress);
                
                UnityEditor.Handles.color = Color.gray;
                UnityEditor.Handles.DrawLine(barStart, barEnd, 3f);
                UnityEditor.Handles.color = Color.green;
                UnityEditor.Handles.DrawLine(barStart, barCurrent, 5f);
            }
            
            // 绘制碰撞体范围
            if (ClimbCollider != null)
            {
                Gizmos.color = ClimbCollider.enabled ? Color.green : Color.red;
                Gizmos.matrix = ClimbCollider.transform.localToWorldMatrix;
                Gizmos.DrawWireCube(ClimbCollider.center, ClimbCollider.size);
                Gizmos.matrix = Matrix4x4.identity;
            }
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
#endif
    }
}

