using UnityEngine;
using Revive.Slime;
using Revive.GamePlay.Purification;

namespace Revive.Environment.Watering
{
    public class SporeMushroomCropWaterReceiver : PbfChargeWaterReceiver
    {
        [ChineseHeader("孢子作物")]
        [ChineseLabel("目标Transform"), Tooltip("缩放作用的目标(通常是作物可视模型)")]
        [SerializeField] private Transform targetTransform;

        [ChineseHeader("初始缩放")]
        [ChineseLabel("初始缩放倍率(xyz)")]
        [SerializeField] private Vector3 initialScaleMultiplier = new Vector3(0.5f, 0.5f, 0.5f);

        [ChineseHeader("缩放过渡")]
        [ChineseLabel("成熟过渡")]
        [SerializeField] private LocalScaleTransition matureScaleTransition = new LocalScaleTransition();

        private bool _baseScaleInitialized;
        private Vector3 _baseScale;

        protected override void Awake()
        {
            base.Awake();

            ResolveTargetTransform(ref targetTransform);
            EnsureBaseLocalScale(targetTransform, ref _baseScaleInitialized, ref _baseScale);
            if (Completed)
            {
                targetTransform.localScale = _baseScale;
            }
            else
            {
                ApplyScaleByCharge01(0f);
            }

            SetPurificationConfig("Spore", PurificationContributionValue);
        }

        protected override void OnChargeUpdated(WaterInput input)
        {
            if (Completed)
                return;
        }

        protected override void OnRestoredByPurification(PurificationRestoreTrigger trigger, Vector3 positionWorld)
        {
            ResolveTargetTransform(ref targetTransform);
            EnsureBaseLocalScale(targetTransform, ref _baseScaleInitialized, ref _baseScale);

            TweenLocalScale(targetTransform, _baseScale, matureScaleTransition);
        }

        private void ApplyScaleByCharge01(float charge01)
        {
            if (targetTransform == null)
                return;

            Vector3 from = new Vector3(
                _baseScale.x * initialScaleMultiplier.x,
                _baseScale.y * initialScaleMultiplier.y,
                _baseScale.z * initialScaleMultiplier.z);

            Vector3 to = _baseScale;
            targetTransform.localScale = Vector3.LerpUnclamped(from, to, Mathf.Clamp01(charge01));
        }
    }
}
