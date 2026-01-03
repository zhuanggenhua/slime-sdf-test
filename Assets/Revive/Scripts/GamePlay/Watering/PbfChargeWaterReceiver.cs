using MoreMountains.Feedbacks;
using UnityEngine;
using Revive.Slime;

namespace Revive.Environment.Watering
{
    public abstract class PbfChargeWaterReceiver : PbfWaterReceiver, IPbfWaterTarget
    {
        [ChineseHeader("反馈")]
        [ChineseLabel("浇水命中反馈")]
        [SerializeField] private MMFeedbacks waterTickFeedbacks;

        [ChineseLabel("浇水命中节流(秒)")]
        [SerializeField, Min(0f), DefaultValue(0.12f)]
        private float waterTickCooldownSeconds = 0.12f;

        [ChineseLabel("完成反馈")]
        [SerializeField] private MMFeedbacks waterCompleteFeedbacks;

        [ChineseHeader("浇水参数")]
        [ChineseLabel("当前蓄水量(运行时)")]
        [SerializeField] private float charge;

        [ChineseLabel("触发所需水量")]
        [DefaultValue(25f)]
        [SerializeField] private float chargeRequired = 25f;

        private float _nextAllowedWaterTickTime;

        protected float Charge => charge;
        protected float ChargeRequired => chargeRequired;

        protected float GetCharge01()
        {
            if (chargeRequired <= 0f)
                return 0f;
            return Mathf.Clamp01(charge / chargeRequired);
        }

        protected void ResetCharge()
        {
            charge = 0f;
        }

        public virtual void ReceiveWater(WaterInput input)
        {
            if (!WantsWater)
                return;

            if (Time.time >= _nextAllowedWaterTickTime)
            {
                waterTickFeedbacks?.PlayFeedbacks(input.PositionWorld);
                _nextAllowedWaterTickTime = Time.time + Mathf.Max(0f, waterTickCooldownSeconds);
            }

            charge += input.Amount;
            OnChargeUpdated(input);

            if (chargeRequired > 0f && charge >= chargeRequired)
            {
                charge = 0f;
                OnChargeCompleted(input);
                waterCompleteFeedbacks?.PlayFeedbacks(input.PositionWorld);
            }
        }

        protected virtual void OnChargeUpdated(WaterInput input)
        {
        }

        protected abstract void OnChargeCompleted(WaterInput input);
    }
}
