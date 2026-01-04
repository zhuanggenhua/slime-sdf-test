using UnityEngine;
using Revive.GamePlay.Purification;
using Revive.Slime;

namespace Revive.Environment.Watering
{
    public abstract class PbfChargeWaterReceiver : PbfWaterReceiver, IPbfWaterTarget
    {
        [ChineseHeader("浇水参数")]
        [ChineseLabel("当前蓄水量(运行时)")]
        [SerializeField] private float charge;

        [ChineseLabel("触发所需水量")]
        [DefaultValue(25f)]
        [SerializeField] private float chargeRequired = 25f;

        [ChineseHeader("调试")]
        [ChineseLabel("最近一次收到水量(运行时)")]
        [SerializeField, MoreMountains.Tools.MMReadOnly] private float debugLastReceivedWaterAmount;

        [ChineseLabel("最近一次消耗粒子数(运行时)")]
        [SerializeField, MoreMountains.Tools.MMReadOnly] private int debugLastReceivedParticleCount;

        [ChineseLabel("最近一次收到水时间(运行时)")]
        [SerializeField, MoreMountains.Tools.MMReadOnly] private float debugLastReceivedTime;

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

        public override void OnPurificationRestored(PurificationRestoreTrigger trigger, Vector3 positionWorld)
        {
            if (Completed)
                return;

            ResetCharge();
            base.OnPurificationRestored(trigger, positionWorld);
        }

        public virtual void ReceiveWater(WaterInput input)
        {
            if (!WantsWater)
                return;

            debugLastReceivedWaterAmount = input.Amount;
            debugLastReceivedParticleCount = input.ParticleCount;
            debugLastReceivedTime = Time.time;

            TryPlayWaterTickFeedbacks(input.PositionWorld);

            float maxCharge = Mathf.Max(0f, chargeRequired);
            if (maxCharge > 0f)
            {
                charge = Mathf.Min(maxCharge, charge + input.Amount);
            }
            else
            {
                charge += input.Amount;
            }

            NotifyRestoreGateWaterAdded(input.Amount, input.PositionWorld);
            OnChargeUpdated(input);
        }

        protected virtual void OnChargeUpdated(WaterInput input)
        {
        }
    }
}
