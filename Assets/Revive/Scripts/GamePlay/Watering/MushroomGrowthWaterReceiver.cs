using System.Collections;
using UnityEngine;
using Revive.Environment;
using Revive.Slime;

namespace Revive.Environment.Watering
{
    public class MushroomGrowthWaterReceiver : PbfWaterReceiver, IPbfWaterTarget
    {
        [ChineseHeader("蘑菇")]
        [ChineseLabel("目标Transform"), Tooltip("缩放作用的目标(通常是蘑菇可视模型)")]
        [SerializeField] private Transform targetTransform;

        [ChineseLabel("JumpPad挂载点"), Tooltip("动态添加 MushroomJumpPad3D 的对象(通常是有实体碰撞的底座/平台)，不填则使用自身")]
        [SerializeField] private Transform jumpPadHost;

        [ChineseLabel("等比缩放"), Tooltip("勾选则XYZ等比放大；否则仅放大Y")]
        [DefaultValue(true)]
        [SerializeField] private bool uniformScale = true;

        [ChineseHeader("浇水参数")]
        [ChineseLabel("当前蓄水量(运行时)")]
        [SerializeField] private float charge;

        [ChineseLabel("触发所需水量"), Tooltip("达到该水量后蘑菇变大并启用跳跳垫")]
        [DefaultValue(25f)]
        [SerializeField] private float chargeRequired = 25f;

        [ChineseHeader("缩放")]
        [ChineseLabel("放大系数(每次触发)"), Tooltip("触发后缩放倍率 = 1 + 该值")]
        [DefaultValue(0.2f)]
        [SerializeField] private float yScalePerStage = 0.2f;

        private Vector3 _baseScale;
        private bool _baseScaleInitialized;
        private bool _activated;
        private MushroomJumpPad3D _jumpPad;
        private bool _resetQueued;
        private bool _scaleResetQueued;

        private SlimeCarryableObject _carryable;
        private bool _carryablePickupEnabledPrev;
        private bool _carryablePickupPrevCaptured;

        public override bool WantsWater => !_activated;

        protected override void Awake()
        {
            base.Awake();
            if (targetTransform == null)
                targetTransform = transform;
            EnsureBaseScale();

            if (_carryable == null)
                _carryable = GetComponentInParent<SlimeCarryableObject>();

            if (_activated)
            {
                ApplyActivatedScale();
                SetCarryablePickupEnabled(false);
                EnsureJumpPad();
                if (_jumpPad != null)
                    _jumpPad.RefreshPlatformFeedbackBaseScale();
            }
        }

        public void ReceiveWater(WaterInput input)
        {
            if (_activated)
                return;

            if (targetTransform == null)
                targetTransform = transform;
            EnsureBaseScale();

            charge += input.Amount;
            if (chargeRequired > 0f && charge >= chargeRequired)
            {
                charge = 0f;
                _activated = true;
                ApplyActivatedScale();
                SetCarryablePickupEnabled(false);
                EnsureJumpPad();
                if (_jumpPad != null)
                    _jumpPad.RefreshPlatformFeedbackBaseScale();
            }
        }

        private void SetCarryablePickupEnabled(bool enabled)
        {
            if (_carryable == null)
                _carryable = GetComponentInParent<SlimeCarryableObject>();
            if (_carryable == null)
                return;

            if (!_carryablePickupPrevCaptured)
            {
                _carryablePickupPrevCaptured = true;
                _carryablePickupEnabledPrev = _carryable.PickupEnabled;
            }

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
                _jumpPad = host.gameObject.AddComponent<MushroomJumpPad3D>();
            }

            _jumpPad.OneShot = true;
            _jumpPad.Bounced -= OnJumpPadBounced;
            _jumpPad.Bounced += OnJumpPadBounced;
        }

        private void OnJumpPadBounced(MushroomJumpPad3D pad, MoreMountains.TopDownEngine.TopDownController3D controller)
        {
            if (pad == null || pad != _jumpPad)
                return;

            EnsureBaseScale();

            if (!_scaleResetQueued)
            {
                _scaleResetQueued = true;
                StartCoroutine(ResetScaleAfterBounceRoutine());
            }

            if (!_resetQueued)
            {
                _resetQueued = true;
                StartCoroutine(ResetAfterBounceRoutine(pad.OneShotDestroyDelay));
            }
        }

        private IEnumerator ResetScaleAfterBounceRoutine()
        {
            yield return new WaitForSeconds(0.5f);
            EnsureBaseScale();
            if (targetTransform != null)
                targetTransform.localScale = _baseScale;
            _scaleResetQueued = false;
        }

        private IEnumerator ResetAfterBounceRoutine(float delay)
        {
            if (delay > 0f)
                yield return new WaitForSeconds(delay);

            _activated = false;
            charge = 0f;
            _resetQueued = false;
            _scaleResetQueued = false;

            if (_carryablePickupPrevCaptured)
            {
                SetCarryablePickupEnabled(_carryablePickupEnabledPrev);
                _carryablePickupPrevCaptured = false;
            }

            if (_jumpPad != null)
            {
                _jumpPad.Bounced -= OnJumpPadBounced;
                _jumpPad = null;
            }
        }

        private void ApplyActivatedScale()
        {
            if (targetTransform == null)
                return;

            EnsureBaseScale();

            float mul = 1f + yScalePerStage;
            if (uniformScale)
            {
                targetTransform.localScale = _baseScale * mul;
            }
            else
            {
                targetTransform.localScale = new Vector3(_baseScale.x, _baseScale.y * mul, _baseScale.z);
            }
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
