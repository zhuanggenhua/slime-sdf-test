using MoreMountains.Feedbacks;
using UnityEngine;
using UnityEngine.Serialization;

namespace Revive.Environment.Watering
{
    /// <summary>
    /// 单脚本版本：挂在目标物体上即可（不依赖 Trigger/Collider）。
    /// 体积使用基类 PbfWaterReceiver 的 Bounds 配置。
    /// 这个脚本同时扮演：
    /// - Receiver：提供接收体积与消耗策略
    /// - Target：接收 WaterInput 并驱动树的 charge/stage 与缩放
    ///
    /// 适用场景：
    /// - 你只想做“树浇水长大”且希望 Prefab/场景配置尽量少。
    ///
    /// 如果后续同一个接收体需要驱动多个目标/效果，或你想复用到不同玩法对象，
    /// 则更推荐用 PbfWaterReceiver + 自己的 IPbfWaterTarget（两脚本解耦）。
    /// </summary>
    public class TreeGrowthWaterReceiver : PbfWaterReceiver, IPbfWaterTarget
    {
        [Header("Growth")]
        [SerializeField] private Transform targetTransform;

        [SerializeField] private float charge;
        [SerializeField] private float chargePerStage = 100f;
        [SerializeField] private int stage;
        [SerializeField] private int maxStage = 5;

        [FormerlySerializedAs("xzScalePerStage")]
        [SerializeField] private float yScalePerStage = 0.2f;

        [ChineseHeader("反馈")]
        [ChineseLabel("浇水命中反馈")]
        [SerializeField] private MMFeedbacks waterTickFeedbacks;

        [ChineseLabel("浇水命中节流(秒)")]
        [SerializeField, Min(0f), DefaultValue(0.12f)]
        private float waterTickCooldownSeconds = 0.12f;

        [ChineseLabel("阶段提升反馈")]
        [SerializeField] private MMFeedbacks stageUpFeedbacks;

        private Vector3 _baseScale;
        private bool _baseScaleInitialized;

        private float _nextAllowedWaterTickTime;

        public override bool WantsWater => !(maxStage > 0 && stage >= maxStage);

        protected override void Awake()
        {
            base.Awake();
            if (maxStage > 0 && stage >= maxStage)
            {
                ApplyStageScale();
            }
        }

        public void ReceiveWater(WaterInput input)
        {
            if (targetTransform == null)
                targetTransform = transform;

            if (Time.time >= _nextAllowedWaterTickTime)
            {
                waterTickFeedbacks?.PlayFeedbacks(input.PositionWorld);
                _nextAllowedWaterTickTime = Time.time + Mathf.Max(0f, waterTickCooldownSeconds);
            }

            if (!_baseScaleInitialized && targetTransform != null)
            {
                _baseScaleInitialized = true;
                _baseScale = targetTransform.localScale;
            }

            if (maxStage > 0 && stage >= maxStage)
                return;

            charge += input.Amount;

            while (chargePerStage > 0f && charge >= chargePerStage && (maxStage <= 0 || stage < maxStage))
            {
                charge -= chargePerStage;
                stage++;
                ApplyStageScale();
                stageUpFeedbacks?.PlayFeedbacks(input.PositionWorld);
            }
        }

        private void ApplyStageScale()
        {
            if (targetTransform == null)
                return;

            if (!_baseScaleInitialized)
            {
                _baseScaleInitialized = true;
                _baseScale = targetTransform.localScale;
            }

            float yMul = 1f + stage * yScalePerStage;
            targetTransform.localScale = new Vector3(_baseScale.x, _baseScale.y * yMul, _baseScale.z);
        }
    }
}
