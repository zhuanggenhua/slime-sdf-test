using MoreMountains.Tools;
using UnityEngine;
using UnityEngine.UI;

namespace Revive.Slime
{
    /// <summary>
    /// 体积UI - 通过 MMEventManager 监听 SlimeVolumeChangeEvent 更新进度条
    /// </summary>
    public class VolumeUI : MonoBehaviour, MMEventListener<SlimeVolumeChangeEvent>
    {
        [Header("【引用组件】")]
        
        [Tooltip("MMProgressBar 进度条（推荐，复用 TopDown UI）")]
        [SerializeField] private MMProgressBar progressBar;
        
        [Tooltip("体积文本显示组件（可选）")]
        [SerializeField] private Text volumeText;
        
        [Tooltip("填充图像组件 - 用于颜色变化（可选）")]
        [SerializeField] private Image fillImage;
        
        [Header("【颜色配置】")]
        [ChineseLabel("正常颜色"), Tooltip("体积充足时显示")]
        [SerializeField, DefaultValue(0.2f, 0.8f, 0.2f, 1f)] 
        private Color normalColor = new Color(0.2f, 0.8f, 0.2f);
        
        [ChineseLabel("警告颜色"), Tooltip("体积较低时显示")]
        [SerializeField, DefaultValue(1f, 0.8f, 0f, 1f)] 
        private Color warningColor = new Color(1f, 0.8f, 0f);
        
        [ChineseLabel("危险颜色"), Tooltip("体积过低时显示")]
        [SerializeField, DefaultValue(1f, 0.2f, 0.2f, 1f)] 
        private Color dangerColor = new Color(1f, 0.2f, 0.2f);
        
        [Header("【阈值配置】")]
        [ChineseLabel("危险阈值"), Tooltip("体积低于此值时显示危险颜色")]
        [SerializeField, Range(0f, 0.5f), DefaultValue(0.15f)] 
        private float dangerThreshold = 0.15f;
        
        [ChineseLabel("警告阈值"), Tooltip("体积低于此值时显示警告颜色")]
        [SerializeField, Range(0f, 0.5f), DefaultValue(0.3f)] 
        private float warningThreshold = 0.3f;

        private void OnEnable()
        {
            // 注册全局事件监听
            this.MMEventStartListening<SlimeVolumeChangeEvent>();
        }

        private void OnDisable()
        {
            this.MMEventStopListening<SlimeVolumeChangeEvent>();
        }

        /// <summary>
        /// MMEventManager 事件回调
        /// </summary>
        public void OnMMEvent(SlimeVolumeChangeEvent volumeEvent)
        {
            UpdateUI(volumeEvent.CurrentVolume, volumeEvent.MinVolume, volumeEvent.MaxVolume, volumeEvent.VolumePercent);
        }

        private void UpdateUI(int current, int min, int max, float percent)
        {
            // 更新 MMProgressBar
            if (progressBar != null)
            {
                progressBar.UpdateBar(current, min, max);
            }
            
            // 更新文本
            if (volumeText != null)
            {
                volumeText.text = $"{percent * 100:F0}%";
            }
            
            // 更新颜色
            if (fillImage != null)
            {
                if (percent <= dangerThreshold)
                {
                    fillImage.color = dangerColor;
                }
                else if (percent <= warningThreshold)
                {
                    fillImage.color = warningColor;
                }
                else
                {
                    fillImage.color = normalColor;
                }
            }
        }
        
        /// <summary>
        /// 重置所有参数为默认值
        /// </summary>
        [ContextMenu("重置参数为默认值")]
        public void ResetToDefaults()
        {
            int count = ConfigResetHelper.ResetToDefaults(this);
            Debug.Log($"[VolumeUI] 已重置 {count} 个参数为默认值");
        }
    }
}
