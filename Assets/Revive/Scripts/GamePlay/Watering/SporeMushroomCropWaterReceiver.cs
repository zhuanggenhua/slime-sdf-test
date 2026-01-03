using Revive.GamePlay.Purification;
using UnityEngine;
using Revive.Slime;

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

        private bool _matured;
        private PurificationIndicator _sporeIndicator;

        public override bool WantsWater => !_matured;

        protected override void Awake()
        {
            base.Awake();

            if (targetTransform == null)
                targetTransform = transform;

            EnsureBaseScale();
            ApplyScaleByCharge01(GetCharge01());

            SetPurificationConfig("Spore", PurificationContributionValue);
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            RemovePurificationIndicator(ref _sporeIndicator);
        }

        protected override void OnChargeUpdated(WaterInput input)
        {
            if (_matured)
                return;

            if (targetTransform == null)
                targetTransform = transform;

            EnsureBaseScale();
            ApplyScaleByCharge01(GetCharge01());
        }

        protected override void OnChargeCompleted(WaterInput input)
        {
            if (_matured)
                return;

            if (targetTransform == null)
                targetTransform = transform;

            EnsureBaseScale();

            _matured = true;

            string sporeName = $"Spore_{gameObject.GetInstanceID()}";
            EnsurePurificationIndicator(ref _sporeIndicator, sporeName, transform.position, PurificationContributionValue, PurificationIndicatorType, PurificationRadiationRadius);

            TweenLocalScale(targetTransform, _baseScale, matureScaleTransition);
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
