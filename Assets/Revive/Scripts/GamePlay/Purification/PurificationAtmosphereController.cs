using MoreMountains.Tools;
using MoreMountains.TopDownEngine;
using UnityEngine;
using UnityEngine.Rendering;

namespace Revive.GamePlay.Purification
{
    /// <summary>
    /// 净化度氛围效果控制器
    /// 根据净化度动态调整，包括但不限于：
    /// - 后处理效果（如灰暗程度）
    /// - 光强变化（净化度为0时最弱，为1时最强）
    /// - 音乐变化（净化度增加时播放更安静的音乐）
    /// - 其他氛围元素（如环境光变化）
    /// </summary>
    public class PurificationAtmosphereController : MonoBehaviour, IPurificationListener
    {
        [Header("后处理效果设置")]
        [Tooltip("Volume组件引用")]
        public Volume PostProcessingVolume;
        
        [Tooltip("最大灰暗强度（净化度为0时）")]
        [Range(0f, 1f)]
        public float MaxDarknessIntensity = 0.8f;
        
        [Tooltip("最小灰暗强度（净化度为1时）")]
        [Range(0f, 1f)]
        public float MinDarknessIntensity = 0f;
        
        [Header("主光源设置")]
        
        [Tooltip("主光源组件引用")]
        public Light MainLight;
        
        [Tooltip("最大主光源强度（净化度为0时）")]
        [Range(0f, 1f)]
        public float MaxLightIntensity = 1f;
        
        [Tooltip("最小主光源强度（净化度为1时）")]
        [Range(0f, 1f)]
        public float MinLightIntensity = 0.5f;
        
        [Header("通用")]
        [Tooltip("平滑过渡速度")]
        public float TransitionSpeed = 2f;
        
        [Header("监听者设置")]
        [Tooltip("监听者名称（用于调试）")]
        public string ListenerName = "PostProcessingController";
        
        [Tooltip("是否在Start时自动注册")]
        public bool AutoRegisterOnStart = true;
        
        [Header("运行时信息")]
        [SerializeField, MMReadOnly]
        private float _currentPurificationLevel = 0f;
        
        [SerializeField, MMReadOnly]
        [Tooltip("强度越高越灰暗")]
        private float _targetIntensity = 0f;
        
        [SerializeField, MMReadOnly]
        private float _currentIntensity = 0f;
        
        private bool _isRegistered = false;
        
        private void Start()
        {
            if (PostProcessingVolume == null)
            {
                var volumes = FindObjectsByType<Volume>(FindObjectsSortMode.None);
                for (int i = 0; i < volumes.Length; i++)
                {
                    var v = volumes[i];
                    if (v == null)
                        continue;
                    if (v.profile == null)
                        continue;
                    if (!v.gameObject.activeSelf)
                        continue;
                    if (v.name == null || !v.name.Contains("Dark"))
                        continue;
                    PostProcessingVolume = v;
                    break;
                }
            }
            
            if (PostProcessingVolume == null)
            {
                Debug.LogWarning($"[{ListenerName}] 未找到Volume组件");
            }

            if (MainLight == null)
            {
                var lights = FindObjectsByType<Light>(FindObjectsSortMode.None);
                for (int i = 0; i < lights.Length; i++)
                {
                    var l = lights[i];
                    if (l == null)
                        continue;
                    if (l.type != LightType.Directional)
                        continue;
                    if (!l.gameObject.activeSelf)
                        continue;
                    MainLight = l;
                    break;
                }
            }

            if (MainLight == null)
            {
                Debug.LogWarning($"[{ListenerName}] 未找到主光源组件");
            }
            
            // 自动注册监听净化系统
            if (AutoRegisterOnStart && PurificationSystem.HasInstance)
            {
                PurificationSystem.Instance.RegisterListener(this);
                _isRegistered = true;
            }
        }
        
        private void Update()
        {
            // 平滑过渡强度
            if (Mathf.Abs(_currentIntensity - _targetIntensity) > 0.001f)
            {
                _currentIntensity = Mathf.Lerp(_currentIntensity, _targetIntensity, Time.deltaTime * TransitionSpeed);
                ApplyIntensity(_currentIntensity);
            }
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
            
            // 计算目标强度：净化度越高，灰暗越低
            _targetIntensity = Mathf.Lerp(MaxDarknessIntensity, MinDarknessIntensity, purificationLevel);
        }
        
        public Vector3 GetListenerPosition()
        {
            if (LevelManager.TryGetInstance() is not {} levelManager
                || levelManager.Players == null 
                || levelManager.Players.Count == 0)
            {
                return transform.position;
            }
            
            return levelManager.Players[0].transform.position;
        }
        
        public string GetListenerName()
        {
            return ListenerName;
        }
        
        #endregion
        
        #region 应用
        
        /// <summary>
        /// 应用强度到氛围
        /// </summary>
        /// <param name="intensity">强度值 (0-1)</param>
        private void ApplyIntensity(float intensity)
        {
            // 处理后处理效果
            if (PostProcessingVolume != null)
            {
                PostProcessingVolume.weight = intensity;
                
                // 如果使用特定的后处理效果，可以这样访问：
                // if (PostProcessingVolume.profile.TryGet<ColorAdjustments>(out var colorAdjustments))
                // {
                //     colorAdjustments.postExposure.value = -intensity * 2f; // 降低曝光
                //     colorAdjustments.saturation.value = -intensity * 50f; // 降低饱和度
                // }
            }
            
            // Main Light 强度调整
            if (MainLight != null)
            {
                MainLight.intensity = Mathf.Lerp(MaxLightIntensity, MinLightIntensity, intensity);
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
        
        #endregion
    }
}

