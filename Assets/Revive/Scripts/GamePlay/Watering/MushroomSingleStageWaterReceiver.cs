using UnityEngine;
using Revive.Environment;
using Revive.GamePlay.Purification;
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
        private MushroomJumpPad3D _jumpPad;

        protected override void Awake()
        {
            base.Awake();

            ResolveTargetTransform(ref targetTransform);
            EnsureBaseLocalScale(targetTransform, ref _baseScaleInitialized, ref _baseScale);

            if (Completed)
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
                DisableAllJumpPadsAtStartup();
                if (_jumpPad != null)
                {
                    _jumpPad.enabled = false;
                    RefreshJumpPadAfterScaleChange();
                }
            }
        }

        private void DisableAllJumpPadsAtStartup()
        {
            Transform host = jumpPadHost != null ? jumpPadHost : transform;
            if (host == null)
                return;

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
            EnsureJumpPad();

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
            if (_jumpPad == null)
            {
                _jumpPad = host.GetComponentInChildren<MushroomJumpPad3D>(includeInactive: true);
            }
        }

        private void ApplyInitialScale()
        {
            if (targetTransform == null)
                return;

            EnsureBaseLocalScale(targetTransform, ref _baseScaleInitialized, ref _baseScale);
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
            EnsureBaseLocalScale(targetTransform, ref _baseScaleInitialized, ref _baseScale);
            return _baseScale;
        }

        private void RefreshJumpPadAfterScaleChange()
        {
            if (_jumpPad == null)
                return;

            _jumpPad.RebuildTriggerZone();
            _jumpPad.RefreshPlatformFeedbackBaseScale();
        }

    }
}
