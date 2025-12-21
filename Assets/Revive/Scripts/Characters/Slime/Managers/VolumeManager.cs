using System;
using Unity.Collections;
using UnityEngine;

namespace Slime
{
    /// <summary>
    /// 体积管理器 - 管理史莱姆的粒子数量限制和发射/吸收状态（单例）
    /// </summary>
    public class VolumeManager : MonoBehaviour
    {
        public static VolumeManager Instance { get; private set; }
        
        private void Awake()
        {
            if (Instance == null)
                Instance = this;
            else if (Instance != this)
                Destroy(gameObject);
        }
        
        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }
        
        [Header("【体积限制】")]
        [ChineseLabel("初始体积"), Tooltip("游戏开始时主体粒子数，与场景水珠数量无关")]
        [Range(100, 4096), DefaultValue(800)]
        public int initialMainVolume = 800;
        
        [ChineseLabel("最小体积"), Tooltip("低于此值时无法发射粒子")]
        [Range(0, 2048), DefaultValue(500)]
        public int minVolume = 500;

        [ChineseLabel("最大体积"), Tooltip("吸收场景水珠后可达到的最大值")]
        [Range(0, 4096), DefaultValue(2048)]
        public int maxVolume = 2048;

        [Header("【当前状态】（只读）")]
        [Tooltip("当前主体粒子数量 (BodyState=0)")]
        [SerializeField]
        private int currentVolume;
        
        [Tooltip("分离粒子数量 (BodyState=1)")]
        [SerializeField]
        private int separatedCount;

        [Tooltip("休眠粒子数量 (BodyState=2)")]
        [SerializeField]
        private int dormantCount;

        [Tooltip("分离粒子中 ID=0 的数量")]
        [SerializeField]
        private int separatedIdZeroCount;

        [Tooltip("分离粒子中 ID<0 的数量")]
        [SerializeField]
        private int separatedIdNegativeCount;

        [Tooltip("分离粒子中 ID>0 的数量")]
        [SerializeField]
        private int separatedIdPositiveCount;

        [Tooltip("分离粒子中来自场景水珠源的数量 (SourceId>=0)")]
        [SerializeField]
        private int separatedFromSourceCount;

        [Tooltip("分离粒子中自由粒子数量 (SourceId=-1 且 ID<=0)")]
        [SerializeField]
        private int separatedFreeCount;
        
        [Tooltip("总激活粒子数 (主体+分离)")]
        [SerializeField]
        private int totalActiveCount;

        [Tooltip("当前体积百分比 (0-1)")]
        [SerializeField]
        private float volumePercent;

        [Header("【操作限制】")]
        [ChineseLabel("最小体积阈值"), Tooltip("用于UI警告显示")]
        [Range(0f, 1f), DefaultValue(0.25f)]
        public float minVolumeThreshold = 0.25f;

        [ChineseLabel("最大体积阈值"), Tooltip("用于UI警告显示")]
        [Range(0f, 1f), DefaultValue(0.9f)]
        public float maxVolumeThreshold = 0.9f;

        [Header("【性能设置】")]
        [ChineseLabel("更新间隔帧数"), Tooltip("值越大性能越好但响应越慢")]
        [Range(1, 60), DefaultValue(10)]
        public int updateIntervalFrames = 10;

        [Header("【调试】")]
        [ChineseLabel("输出体积调试日志"), Tooltip("每60帧输出一次体积统计（会产生日志）")]
        public bool volumeDebug;

        public int CurrentVolume => currentVolume;
        public float VolumePercent => volumePercent;

        public event Action<float> OnVolumeChanged;
        public event Action OnVolumeMinReached;
        public event Action OnVolumeMaxReached;
        public event Action<bool> OnEmitStateChanged;
        public event Action<bool> OnAbsorbStateChanged;

        private int _lastUpdateFrame = int.MinValue;
        private int _lastVolume = -1;
        private bool _minReached;
        private bool _maxReached;
        private bool _lastCanEmit;
        private bool _lastCanAbsorb;

        public void UpdateVolume(NativeArray<Particle> particles, bool forceUpdate = false)
        {
            int frame = Time.frameCount;
            if (!forceUpdate && frame - _lastUpdateFrame < updateIntervalFrames)
                return;

            _lastUpdateFrame = frame;

            int state0Count = 0;  // 主体粒子
            int state1Count = 0;  // 分离粒子
            int state2Count = 0;  // 休眠粒子

            int idZeroCount = 0;
            int idNegativeCount = 0;
            int idPositiveCount = 0;
            int fromSourceCount = 0;
            int freeSeparatedCount = 0;
            
            for (int i = 0; i < particles.Length; i++)
            {
                var p = particles[i];
                int state = p.BodyState;
                if (state == 0)
                    state0Count++;
                else if (state == 1)
                {
                    state1Count++;

                    if (p.SourceId >= 0)
                        fromSourceCount++;

                    if (p.ID == 0)
                        idZeroCount++;
                    else if (p.ID < 0)
                        idNegativeCount++;
                    else
                        idPositiveCount++;

                    if (p.SourceId == -1 && p.ID <= 0)
                        freeSeparatedCount++;
                }
                else if (state == 2)
                {
                    state2Count++;
                }
            }

            currentVolume = state0Count;
            separatedCount = state1Count;
            dormantCount = state2Count;
            totalActiveCount = state0Count + state1Count;

            separatedIdZeroCount = idZeroCount;
            separatedIdNegativeCount = idNegativeCount;
            separatedIdPositiveCount = idPositiveCount;
            separatedFromSourceCount = fromSourceCount;
            separatedFreeCount = freeSeparatedCount;
            
            // 每60帧打印一次详细状态
            if (volumeDebug && frame % 60 == 0)
            {
                Debug.Log($"[Volume] 总粒子={particles.Length}, 主体(0)={state0Count}, 分离(1)={state1Count}, 休眠(2)={state2Count}, 分离ID:0={idZeroCount},<0={idNegativeCount},>0={idPositiveCount}, 分离Source>={0}={fromSourceCount}, 分离自由={freeSeparatedCount}");
            }

            if (maxVolume > minVolume)
                volumePercent = Mathf.Clamp01((float)(currentVolume - minVolume) / (maxVolume - minVolume));
            else
                volumePercent = 0f;

            if (currentVolume != _lastVolume)
            {
                _lastVolume = currentVolume;
                OnVolumeChanged?.Invoke(volumePercent);
            }

            if (!_minReached && currentVolume <= minVolume)
            {
                _minReached = true;
                OnVolumeMinReached?.Invoke();
            }
            else if (_minReached && currentVolume > minVolume)
            {
                _minReached = false;
            }

            if (!_maxReached && currentVolume >= maxVolume)
            {
                _maxReached = true;
                OnVolumeMaxReached?.Invoke();
            }
            else if (_maxReached && currentVolume < maxVolume)
            {
                _maxReached = false;
            }

            bool canEmit = CanEmit(1);
            if (canEmit != _lastCanEmit)
            {
                _lastCanEmit = canEmit;
                OnEmitStateChanged?.Invoke(canEmit);
            }

            bool canAbsorb = CanAbsorb();
            if (canAbsorb != _lastCanAbsorb)
            {
                _lastCanAbsorb = canAbsorb;
                OnAbsorbStateChanged?.Invoke(canAbsorb);
            }
        }

        public bool CanEmit(int amount)
        {
            return currentVolume - amount >= minVolume;
        }

        public bool CanAbsorb()
        {
            return currentVolume < maxVolume;
        }

        public int GetMaxEmitAmount()
        {
            return Mathf.Max(0, currentVolume - minVolume);
        }

        public int GetMaxAbsorbAmount()
        {
            return Mathf.Max(0, maxVolume - currentVolume);
        }
        
        /// <summary>
        /// 重置所有参数为默认值
        /// </summary>
        [ContextMenu("重置参数为默认值")]
        public void ResetToDefaults()
        {
            int count = ConfigResetHelper.ResetToDefaults(this);
            Debug.Log($"[VolumeManager] 已重置 {count} 个参数为默认值");
        }
        
        /// <summary>
        /// 在控制台输出当前参数和默认值对比
        /// </summary>
        [ContextMenu("显示默认值信息")]
        public void ShowDefaultsInfo()
        {
            Debug.Log(ConfigResetHelper.GetDefaultsInfo(this));
        }
    }
}
