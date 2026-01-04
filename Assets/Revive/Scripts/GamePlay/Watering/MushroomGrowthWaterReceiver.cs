using UnityEngine;
using Revive.Environment;
using Revive.GamePlay.Purification;
using Revive.Slime;

namespace Revive.Environment.Watering
{
    public class MushroomGrowthWaterReceiver : PbfChargeWaterReceiver
    {
        [ChineseHeader("蘑菇")]
        [ChineseLabel("目标Transform"), Tooltip("缩放作用的目标(通常是蘑菇可视模型)")]
        [SerializeField] private Transform targetTransform;

        [ChineseLabel("JumpPad挂载点"), Tooltip("动态添加 MushroomJumpPad3D 的对象(通常是有实体碰撞的底座/平台)，不填则使用自身")]
        [SerializeField] private Transform jumpPadHost;

        [ChineseLabel("等比缩放"), Tooltip("勾选则XYZ等比放大；否则仅放大Y")]
        [DefaultValue(true)]
        [SerializeField] private bool uniformScale = true;

        [ChineseHeader("缩放")]
        [ChineseLabel("放大系数(每次触发)"), Tooltip("触发后缩放倍率 = 1 + 该值")]
        [DefaultValue(0.2f)]
        [SerializeField] private float yScalePerStage = 0.2f;

        [ChineseHeader("缩放过渡")]
        [ChineseLabel("Q弹过渡")]
        [SerializeField] private LocalScaleTransition scaleTransition = new LocalScaleTransition();

        private Vector3 _baseScale;
        private bool _baseScaleInitialized;
        private MushroomJumpPad3D _jumpPad;

        private SlimeCarryableObject _carryable;

        private int _purificationIndicatorCounter;

        protected override void Awake()
        {
            base.Awake();
            ResolveTargetTransform(ref targetTransform);
            EnsureBaseLocalScale(targetTransform, ref _baseScaleInitialized, ref _baseScale);

            if (_carryable == null)
                _carryable = GetComponentInParent<SlimeCarryableObject>();

            if (!Completed)
            {
                DisableExistingJumpPadAtStartup();
            }

            if (Completed)
            {
                ApplyActivatedScale();
                SetCarryablePickupEnabled(false);
                EnsureJumpPad();
                if (_jumpPad != null)
                {
                    _jumpPad.enabled = true;
                    RefreshJumpPadAfterScaleChange();
                }
            }
        }

        private void DisableExistingJumpPadAtStartup()
        {
            Transform host = jumpPadHost != null ? jumpPadHost : transform;
            if (host == null)
            {
                return;
            }

            MushroomJumpPad3D[] pads = host.GetComponentsInChildren<MushroomJumpPad3D>(includeInactive: true);
            for (int i = 0; i < pads.Length; i++)
            {
                MushroomJumpPad3D pad = pads[i];
                if (pad == null)
                    continue;
                if (pad.enabled)
                    pad.enabled = false;
            }
        }

        protected override void OnChargeUpdated(WaterInput input)
        {
            ResolveTargetTransform(ref targetTransform);
            EnsureBaseLocalScale(targetTransform, ref _baseScaleInitialized, ref _baseScale);
        }

        protected override void OnRestoredByPurification(PurificationRestoreTrigger trigger, Vector3 positionWorld)
        {
            ResolveTargetTransform(ref targetTransform);
            EnsureBaseLocalScale(targetTransform, ref _baseScaleInitialized, ref _baseScale);

            SetCarryablePickupEnabled(false);
            EnsureJumpPad();
            if (_jumpPad != null)
            {
                _jumpPad.enabled = false;
            }

            Vector3 targetScale = GetActivatedLocalScale();
            TweenLocalScale(targetTransform, targetScale, scaleTransition, () =>
            {
                if (_jumpPad != null)
                {
                    _jumpPad.enabled = true;
                    RefreshJumpPadAfterScaleChange();
                }
            });
        }

        private void SetCarryablePickupEnabled(bool enabled)
        {
            if (_carryable == null)
                _carryable = GetComponentInParent<SlimeCarryableObject>();
            if (_carryable == null)
                return;
            _carryable.PickupEnabled = enabled;
        }

        private void EnsureJumpPad()
        {
            if (_jumpPad != null)
                return;

            Transform host = jumpPadHost != null ? jumpPadHost : transform;
            _jumpPad = host.GetComponent<MushroomJumpPad3D>();
            if (_jumpPad == null)
            {
                _jumpPad = host.GetComponentInChildren<MushroomJumpPad3D>(includeInactive: true);
            }
            if (_jumpPad == null)
            {
                _jumpPad = host.gameObject.AddComponent<MushroomJumpPad3D>();
            }

            _jumpPad.OneShot = false;
        }

        private void ApplyActivatedScale()
        {
            if (targetTransform == null)
                return;

            targetTransform.localScale = GetActivatedLocalScale();
        }

        private void RefreshJumpPadAfterScaleChange()
        {
            if (_jumpPad == null)
                return;

            _jumpPad.RebuildTriggerZone();
            _jumpPad.RefreshPlatformFeedbackBaseScale();
        }

        private Vector3 GetActivatedLocalScale()
        {
            EnsureBaseLocalScale(targetTransform, ref _baseScaleInitialized, ref _baseScale);

            float mul = 1f + yScalePerStage;
            if (uniformScale)
            {
                return _baseScale * mul;
            }

            return new Vector3(_baseScale.x, _baseScale.y * mul, _baseScale.z);
        }
    }
}
