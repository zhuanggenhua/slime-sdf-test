using MoreMountains.Feedbacks;
using Revive.GamePlay.Purification;
using UnityEngine;
using Revive.Environment;
using Revive.Slime;

namespace Revive.Environment.Watering
{
    public class MushroomSingleStageWaterReceiver : PbfChargeWaterReceiver
    {
        [ChineseHeader("蘑菇")]
        [ChineseLabel("目标Transform"), Tooltip("缩放作用的目标(通常是蘑菇可视模型)")]
        [SerializeField] private Transform targetTransform;

        [ChineseLabel("JumpPad挂载点"), Tooltip("启用 MushroomJumpPad3D 的对象(通常是有实体碰撞的底座/平台)，不填则使用自身")]
        [SerializeField] private Transform jumpPadHost;

        [ChineseHeader("初始缩放")]
        
        [ChineseLabel("初始缩放倍率(xyz)"), Tooltip("开始时会把目标缩放设置为 原始缩放 * 该倍率（xyz独立）")]
        [SerializeField] private Vector3 initialScaleMultiplier = new Vector3(0.5f, 0.5f, 0.5f);

        [ChineseHeader("缩放过渡")]
        [ChineseLabel("Q弹过渡")]
        [SerializeField] private LocalScaleTransition scaleTransition = new LocalScaleTransition();

        private int _purificationIndicatorCounter;

        private Vector3 _baseScale;
        private bool _baseScaleInitialized;
        private bool _activated;
        private MushroomJumpPad3D _jumpPad;

        public override bool WantsWater => !_activated;

        protected override void Awake()
        {
            base.Awake();

            if (targetTransform == null)
                targetTransform = transform;

            EnsureBaseScale();

            if (_activated)
            {
                ApplyActivatedScale();
                EnsureJumpPad();
                if (_jumpPad != null)
                {
                    _jumpPad.enabled = true;
                    RefreshJumpPadAfterScaleChange();
                }
            }
            else
            {
                ApplyInitialScale();
                EnsureJumpPad();
                if (_jumpPad != null)
                {
                    _jumpPad.enabled = false;
                    RefreshJumpPadAfterScaleChange();
                }
            }
        }

        protected override void OnChargeUpdated(WaterInput input)
        {
            if (targetTransform == null)
                targetTransform = transform;
            EnsureBaseScale();
        }

        protected override void OnChargeCompleted(WaterInput input)
        {
            if (_activated)
                return;

            if (targetTransform == null)
                targetTransform = transform;

            EnsureBaseScale();

            _activated = true;
            EnsureJumpPad();

            string indicatorName = $"{gameObject.name}_{PurificationIndicatorType}_{_purificationIndicatorCounter++}";
            PurificationSystem system = GetPurificationSystemChecked();
            system.AddIndicator(indicatorName, transform.position, PurificationContributionValue, PurificationIndicatorType, PurificationRadiationRadius);

            Vector3 targetScale = GetActivatedLocalScale();
            TweenLocalScale(targetTransform, targetScale, scaleTransition, () =>
            {
                if (_jumpPad != null)
                {
                    _jumpPad.OneShot = false;
                    _jumpPad.enabled = true;
                    RefreshJumpPadAfterScaleChange();
                }
            });
        }

        private void EnsureJumpPad()
        {
            if (_jumpPad != null)
                return;

            Transform host = jumpPadHost != null ? jumpPadHost : transform;
            _jumpPad = host.GetComponent<MushroomJumpPad3D>();
        }

        private void ApplyInitialScale()
        {
            if (targetTransform == null)
                return;

            EnsureBaseScale();
            targetTransform.localScale = new Vector3(
                _baseScale.x * initialScaleMultiplier.x,
                _baseScale.y * initialScaleMultiplier.y,
                _baseScale.z * initialScaleMultiplier.z);
        }

        private void ApplyActivatedScale()
        {
            if (targetTransform == null)
                return;
            targetTransform.localScale = GetActivatedLocalScale();
        }

        private Vector3 GetActivatedLocalScale()
        {
            EnsureBaseScale();
            return _baseScale;
        }

        private void RefreshJumpPadAfterScaleChange()
        {
            if (_jumpPad == null)
                return;

            _jumpPad.RebuildTriggerZone();
            _jumpPad.RefreshPlatformFeedbackBaseScale();
        }

        private void EnsureBaseScale()
        {
            if (_baseScaleInitialized)
                return;
            if (targetTransform == null)
                return;

            _baseScaleInitialized = true;
            _baseScale = targetTransform.localScale;
        }
    }
}
