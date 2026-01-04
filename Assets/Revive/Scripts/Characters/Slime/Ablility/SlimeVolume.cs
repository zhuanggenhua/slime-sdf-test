using MoreMountains.Tools;
using Unity.Collections;
using UnityEngine;

namespace Revive.Slime
{
    /// <summary>
    /// 史莱姆体积变化事件（通过 MMEventManager 广播）
    /// </summary>
    public struct SlimeVolumeChangeEvent
    {
        public SlimeVolume AffectedVolume;
        public int CurrentVolume;
        public int MinVolume;
        public int MaxVolume;
        public float VolumePercent;

        static SlimeVolumeChangeEvent e;
        public static void Trigger(SlimeVolume volume, int current, int min, int max, float percent)
        {
            e.AffectedVolume = volume;
            e.CurrentVolume = current;
            e.MinVolume = min;
            e.MaxVolume = max;
            e.VolumePercent = percent;
            MMEventManager.TriggerEvent(e);
        }
    }

    /// <summary>
    /// 史莱姆体积组件 - 管理粒子数量限制和发射/吸收状态
    /// </summary>
    [AddComponentMenu("Revive/Slime/Slime Volume")]
    public class SlimeVolume : MonoBehaviour
    {
        [Header("【体积限制】")]
        [ChineseLabel("初始体积"), Tooltip("游戏开始时主体粒子数")]
        [Range(100, 4096), DefaultValue(800)]
        public int initialMainVolume = 800;
        
        [ChineseLabel("最小体积"), Tooltip("低于此值时无法发射粒子")]
        [Range(0, 2048), DefaultValue(500)]
        public int minVolume = 500;

        [ChineseLabel("最大体积"), Tooltip("吸收上限")]
        [Range(0, 4096), DefaultValue(4096)]
        public int maxVolume = 4096;

        [Header("【性能设置】")]
        [ChineseLabel("更新间隔帧数"), Tooltip("值越大越省性能")]
        [Range(1, 60), DefaultValue(10)]
        public int updateIntervalFrames = 10;

        [Header("【当前状态】（只读）")]
        [SerializeField] private int currentVolume;
        [SerializeField] private float volumePercent;
        [SerializeField] private int storedVolume;

        public int CurrentVolume => currentVolume;
        public float VolumePercent => volumePercent;
        public int MinVolume => minVolume;
        public int MaxVolume => maxVolume;

        public void AddStoredVolume(int amount)
        {
            if (amount <= 0)
                return;
            storedVolume = Mathf.Clamp(storedVolume + amount, 0, maxVolume);
        }

        private int _lastUpdateFrame = int.MinValue;
        private int _lastVolume = -1;

        /// <summary>
        /// 从粒子数组更新体积状态（由 Slime_PBF 调用）
        /// </summary>
        public void UpdateFromParticles(NativeArray<Particle> particles, bool forceUpdate = false)
        {
            int frame = Time.frameCount;
            if (!forceUpdate && frame - _lastUpdateFrame < updateIntervalFrames)
                return;

            _lastUpdateFrame = frame;

            int mainCount = 0;
            for (int i = 0; i < particles.Length; i++)
            {
                if (particles[i].Type == ParticleType.MainBody)
                    mainCount++;
            }

            currentVolume = Mathf.Clamp(mainCount + storedVolume, 0, maxVolume);

            if (maxVolume > minVolume)
                volumePercent = Mathf.Clamp01((float)(currentVolume - minVolume) / (maxVolume - minVolume));
            else
                volumePercent = 0f;

            if (currentVolume != _lastVolume)
            {
                _lastVolume = currentVolume;
                SlimeVolumeChangeEvent.Trigger(this, currentVolume, minVolume, maxVolume, volumePercent);
            }
        }

        public bool CanEmit(int amount) => currentVolume - amount >= minVolume;
        public bool CanAbsorb() => currentVolume < maxVolume;
        public int GetMaxEmitAmount() => Mathf.Max(0, currentVolume - minVolume);
        public int GetMaxAbsorbAmount() => Mathf.Max(0, maxVolume - currentVolume);

        public void ForceBroadcast()
        {
            currentVolume = Mathf.Clamp(currentVolume, 0, maxVolume);

            if (maxVolume > minVolume)
                volumePercent = Mathf.Clamp01((float)(currentVolume - minVolume) / (maxVolume - minVolume));
            else
                volumePercent = 0f;

            _lastVolume = currentVolume;
            SlimeVolumeChangeEvent.Trigger(this, currentVolume, minVolume, maxVolume, volumePercent);
        }
    }
}
