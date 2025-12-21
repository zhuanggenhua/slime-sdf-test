using UnityEngine;
using UnityEngine.UI;

namespace Slime
{
    /// <summary>
    /// 体积UI - 显示史莱姆当前体积状态和警告信息
    /// </summary>
    public class VolumeUI : MonoBehaviour
    {
        [Header("【引用组件】")]
        
        [Tooltip("体积滑动条组件")]
        [SerializeField] private Slider volumeSlider;
        
        [Tooltip("体积文本显示组件")]
        [SerializeField] private Text volumeText;
        
        [Tooltip("填充图像组件 - 用于颜色变化")]
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

        private void Start()
        {
            if (VolumeManager.Instance != null)
            {
                VolumeManager.Instance.OnVolumeChanged += OnVolumeChanged;
                UpdateUI(VolumeManager.Instance.VolumePercent);
            }
        }

        private void OnDestroy()
        {
            if (VolumeManager.Instance != null)
            {
                VolumeManager.Instance.OnVolumeChanged -= OnVolumeChanged;
            }
        }

        private void OnVolumeChanged(float percent)
        {
            UpdateUI(percent);
        }

        private void UpdateUI(float percent)
        {
            if (volumeSlider != null)
            {
                volumeSlider.value = percent;
            }
            
            if (volumeText != null)
            {
                volumeText.text = $"{percent * 100:F0}%";
            }
            
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

        private void Update()
        {
            if (VolumeManager.Instance != null)
            {
                UpdateUI(VolumeManager.Instance.VolumePercent);
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
