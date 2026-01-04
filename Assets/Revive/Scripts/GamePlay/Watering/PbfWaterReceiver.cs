using System;
using System.Collections;
using System.Collections.Generic;
using MoreMountains.Feedbacks;
using MoreMountains.Tools;
using Revive.GamePlay.Purification;
using Revive.Slime;
using UnityEngine;

namespace Revive.Environment.Watering
{
    /// <summary>
    /// PBF 浇水“接收体”Authoring 组件：
    /// - 用来定义一个“接收体积（Bounds）”以及其玩法参数，并把水输入转发给 Target。
    /// - 体积用本地 OBB（center/size + Transform）配置，系统运行时会换算成世界 AABB 参与查询与判定。
    ///
    /// 说明：PBF 粒子不走 Unity 逐粒子物理回调，我们通过 Slime_PBF 的空间哈希做批量查询。
    /// </summary>
    public class PbfWaterReceiver : MonoBehaviour, IPurificationRestoreTarget, IPurificationUnlockIndicatorProvider
    {
        private static readonly List<PbfWaterReceiver> _instances = new List<PbfWaterReceiver>(64);

        public static IReadOnlyList<PbfWaterReceiver> Instances => _instances;

        internal static void Register(PbfWaterReceiver receiver)
        {
            if (receiver == null)
                return;

            if (!_instances.Contains(receiver))
                _instances.Add(receiver);
        }

        internal static void Unregister(PbfWaterReceiver receiver)
        {
            if (receiver == null)
                return;

            _instances.Remove(receiver);
        }

        [ChineseHeader("浇水体积")]
        [ChineseLabel("触发器 Collider")]
        [SerializeField] private Collider triggerCollider;

        [ChineseHeader("浇水参数")]
        [ChineseLabel("每粒子水量")]
        [DefaultValue(1f)]
        [SerializeField] private float waterPerParticle = 1f;

        [ChineseLabel("每帧最大消耗(0=不限)")]
        [DefaultValue(0)]
        [SerializeField] private int maxConsumePerUpdate;

        [ChineseHeader("消耗开关")]
        [ChineseLabel("消耗喷出粒子")]
        [DefaultValue(true)]
        [SerializeField] private bool consumeEmitted = true;

        [ChineseLabel("消耗分离粒子")]
        [DefaultValue(true)]
        [SerializeField] private bool consumeSeparated = true;

        [ChineseLabel("消耗场景水珠")]
        [DefaultValue(true)]
        [SerializeField] private bool consumeDroplets = true;

        [ChineseHeader("反馈")]
        [ChineseLabel("浇水命中反馈")]
        [SerializeField] private MMFeedbacks waterTickFeedbacks;

        [ChineseLabel("浇水命中节流(秒)")]
        [SerializeField, Min(0f), DefaultValue(0.12f)]
        private float waterTickCooldownSeconds = 0.12f;

        [ChineseLabel("完成反馈")]
        [SerializeField] private MMFeedbacks waterCompleteFeedbacks;

        [ChineseHeader("净化")]
        [ChineseLabel("指示物类型")]
        [DefaultValue("Water")]
        [SerializeField] private string purificationIndicatorType = "Water";

        [ChineseHeader("完成")]
        [ChineseLabel("已完成(运行时)")]
        [SerializeField] private bool completed;

        [ChineseHeader("恢复")]
        [ChineseLabel("恢复闸门(可空)")]
        [SerializeField] private PurificationRestoreGate restoreGate;

        [ChineseLabel("自动获取闸门(同物体)")]
        [DefaultValue(true)]
        [SerializeField] private bool autoFindRestoreGateOnSelf = true;

        [ChineseLabel("事件贡献值")]
        [DefaultValue(10f)]
        [SerializeField] private float purificationContributionValue = 10f;

        [ChineseLabel("辐射范围(米)")]
        [DefaultValue(8f)]
        [SerializeField] private float purificationRadiationRadius = 8f;

        [ChineseHeader("调试")]
        [ChineseLabel("调试刷新间隔(秒)")]
        [SerializeField, Min(0f), DefaultValue(0.25f)]
        private float debugPollIntervalSeconds = 0.25f;

        [ChineseLabel("闸门已解锁(运行时)")]
        [SerializeField, MMReadOnly] private bool debugGateUnlocked;

        [ChineseLabel("闸门基础阈值(运行时)")]
        [SerializeField, MMReadOnly] private float debugGateBaseThreshold;

        [ChineseLabel("闸门有效阈值(运行时)")]
        [SerializeField, MMReadOnly] private float debugGateEffectiveThreshold;

        [ChineseLabel("闸门累计阈值降低(运行时)")]
        [SerializeField, MMReadOnly] private float debugGateThresholdDeltaAccumulated;

        [ChineseLabel("闸门浇水进度(运行时)")]
        [SerializeField, MMReadOnly] private float debugGateWaterProgress01;

        [ChineseLabel("闸门位置净化度(运行时)")]
        [SerializeField, MMReadOnly] private float debugGatePurificationLevel;

        [ChineseLabel("距离闸门阈值(运行时)")]
        [SerializeField, MMReadOnly] private float debugGateToThreshold;

        private bool _warnedMissingTriggerCollider;
        private bool _warnedTriggerColliderNotTrigger;
        private bool _warnedMissingTarget;

        private float _nextAllowedWaterTickTime;
        private float _nextDebugPollTime;

        private IPbfWaterTarget _target;

        public IPbfWaterTarget Target => _target;

        public Collider TriggerCollider => triggerCollider;

        public float WaterPerParticle => waterPerParticle;
        public int MaxConsumePerUpdate => maxConsumePerUpdate;

        public bool ConsumeEmitted => consumeEmitted;
        public bool ConsumeSeparated => consumeSeparated;
        public bool ConsumeDroplets => consumeDroplets;

        protected string PurificationIndicatorType => purificationIndicatorType;
        protected float PurificationContributionValue => purificationContributionValue;
        protected float PurificationRadiationRadius => purificationRadiationRadius;

        public bool Completed => completed;

        protected void SetCompleted(bool value)
        {
            completed = value;
        }

        protected void SetPurificationConfig(string indicatorType, float contributionValue)
        {
            purificationIndicatorType = indicatorType;
            purificationContributionValue = contributionValue;
        }

        public virtual bool WantsWater => !completed;

        private Coroutine _localScaleTransitionRoutine;

        protected void ResolveTargetTransform(ref Transform targetTransform)
        {
            if (targetTransform == null)
                targetTransform = transform;
        }

        protected void EnsureBaseLocalScale(Transform targetTransform, ref bool baseScaleInitialized, ref Vector3 baseLocalScale)
        {
            if (baseScaleInitialized)
                return;
            if (targetTransform == null)
                return;

            baseScaleInitialized = true;
            baseLocalScale = targetTransform.localScale;
        }

        protected void TryPlayWaterTickFeedbacks(Vector3 positionWorld)
        {
            if (waterTickFeedbacks == null)
                return;

            if (Time.time < _nextAllowedWaterTickTime)
                return;

            MMFeedbacksHelper.Play(waterTickFeedbacks, positionWorld);
            _nextAllowedWaterTickTime = Time.time + Mathf.Max(0f, waterTickCooldownSeconds);
        }

        protected void PlayWaterCompleteFeedbacks(Vector3 positionWorld)
        {
            MMFeedbacksHelper.Play(waterCompleteFeedbacks, positionWorld);
        }

        public bool ContainsPointWorld(Vector3 pointWorld)
        {
            if (triggerCollider == null || !triggerCollider.enabled)
                return false;

            if (triggerCollider is BoxCollider box)
            {
                Transform t = box.transform;
                if (t == null)
                    return false;

                Vector3 local = t.InverseTransformPoint(pointWorld) - box.center;
                Vector3 half = box.size * 0.5f;
                const float eps = 1e-6f;
                return Mathf.Abs(local.x) <= half.x + eps &&
                       Mathf.Abs(local.y) <= half.y + eps &&
                       Mathf.Abs(local.z) <= half.z + eps;
            }

            Vector3 closest = triggerCollider.ClosestPoint(pointWorld);
            Vector3 d = closest - pointWorld;
            return d.sqrMagnitude <= 1e-8f;
        }

        public bool TryGetVolumeBoundsWorld(out Bounds boundsWorld)
        {
            if (triggerCollider == null || !triggerCollider.enabled)
            {
                boundsWorld = default;
                return false;
            }

            boundsWorld = triggerCollider.bounds;
            return true;
        }

        protected virtual void Awake()
        {
            ValidateTriggerCollider();
            ResolveReferences();
            EnsureRestoreGateReference();
        }

        private void Update()
        {
            if (!Application.isPlaying)
                return;

            float interval = Mathf.Max(0f, debugPollIntervalSeconds);
            if (interval <= 0f)
                return;
            if (Time.time < _nextDebugPollTime)
                return;

            _nextDebugPollTime = Time.time + interval;

            EnsureRestoreGateReference();
            if (restoreGate == null)
                return;

            debugGateUnlocked = restoreGate.Unlocked;
            debugGateBaseThreshold = restoreGate.FullThreshold;
            debugGateThresholdDeltaAccumulated = restoreGate.ThresholdDeltaAccumulated;
            debugGateEffectiveThreshold = restoreGate.GetEffectiveFullThreshold();
            debugGateWaterProgress01 = restoreGate.GetWaterProgress01();

            if (PurificationSystem.HasInstance)
            {
                Vector3 pos = restoreGate.GetListenerPosition();
                debugGatePurificationLevel = PurificationSystem.Instance.GetPurificationLevel(pos);
                debugGateToThreshold = debugGateEffectiveThreshold - debugGatePurificationLevel;
            }
        }

        protected virtual void OnEnable()
        {
            Register(this);

            ValidateTriggerCollider();
            ResolveReferences();
            EnsureRestoreGateReference();

            AssertPurificationSystemPresent();
        }

        protected virtual void OnDisable()
        {
            Unregister(this);

            if (_localScaleTransitionRoutine != null)
            {
                StopCoroutine(_localScaleTransitionRoutine);
                _localScaleTransitionRoutine = null;
            }
        }

        protected virtual void OnValidate()
        {
            ValidateTriggerCollider();

            ResolveReferences();
            EnsureRestoreGateReference();
        }

        public virtual void OnPurificationRestored(PurificationRestoreTrigger trigger, Vector3 positionWorld)
        {
            if (completed)
                return;

            completed = true;
            PlayWaterCompleteFeedbacks(positionWorld);
            OnRestoredByPurification(trigger, positionWorld);
        }

        public virtual bool TryGetUnlockIndicatorConfig(
            PurificationRestoreTrigger trigger,
            Vector3 unlockPositionWorld,
            out string indicatorName,
            out Vector3 indicatorPositionWorld)
        {
            indicatorPositionWorld = transform.position;
            indicatorName = string.Empty;
            return true;
        }

        protected virtual void OnRestoredByPurification(PurificationRestoreTrigger trigger, Vector3 positionWorld)
        {
        }

        protected void NotifyRestoreGateWaterAdded(float waterAmount, Vector3 positionWorld)
        {
            EnsureRestoreGateReference();
            if (restoreGate == null)
                return;

            restoreGate.NotifyWaterAdded(waterAmount, positionWorld);
        }

        private void EnsureRestoreGateReference()
        {
            if (restoreGate != null)
                return;

            if (!autoFindRestoreGateOnSelf)
                return;

            restoreGate = GetComponent<PurificationRestoreGate>();
            if (restoreGate == null)
            {
                restoreGate = GetComponentInParent<PurificationRestoreGate>();
            }
        }

        protected void TweenLocalScale(Transform target, Vector3 targetLocalScale, LocalScaleTransition transition, Action onComplete = null)
        {
            if (target == null)
                return;

            float duration = transition != null ? transition.Duration : 0f;
            if (duration <= 0f)
            {
                target.localScale = targetLocalScale;
                onComplete?.Invoke();
                return;
            }

            if (_localScaleTransitionRoutine != null)
            {
                StopCoroutine(_localScaleTransitionRoutine);
                _localScaleTransitionRoutine = null;
            }

            _localScaleTransitionRoutine = StartCoroutine(TweenLocalScaleRoutine(target, targetLocalScale, transition, onComplete));
        }

        private IEnumerator TweenLocalScaleRoutine(Transform target, Vector3 targetLocalScale, LocalScaleTransition transition, Action onComplete)
        {
            if (target == null)
            {
                _localScaleTransitionRoutine = null;
                yield break;
            }

            Vector3 from = target.localScale;
            float duration = Mathf.Max(0.0001f, transition != null ? transition.Duration : 0.0001f);

            float t = 0f;
            while (t < duration)
            {
                if (target == null)
                {
                    _localScaleTransitionRoutine = null;
                    yield break;
                }

                t += Time.deltaTime;
                float u = Mathf.Clamp01(t / duration);
                float cu = transition != null ? transition.Evaluate(u) : u;
                target.localScale = Vector3.LerpUnclamped(from, targetLocalScale, cu);
                yield return null;
            }

            if (target != null)
            {
                target.localScale = targetLocalScale;
            }

            _localScaleTransitionRoutine = null;
            onComplete?.Invoke();
        }

        private void ResolveReferences()
        {
            _target = this as IPbfWaterTarget;
            if (_target == null)
            {
                if (!_warnedMissingTarget)
                {
                    Debug.LogWarning($"[{nameof(PbfWaterReceiver)}] 未实现 {nameof(IPbfWaterTarget)}：请让该组件(或其派生类)实现接口以接收水输入。对象：{name}", this);
                    _warnedMissingTarget = true;
                }
                return;
            }

            _warnedMissingTarget = false;
        }

        private void ValidateTriggerCollider()
        {
            if (triggerCollider == null)
            {
                if (!_warnedMissingTriggerCollider)
                {
                    Debug.LogWarning($"[{nameof(PbfWaterReceiver)}] triggerCollider 未配置：请在 Inspector 手动指定一个 Collider，并勾选 Is Trigger。对象：{name}", this);
                    _warnedMissingTriggerCollider = true;
                }

                _warnedTriggerColliderNotTrigger = false;
                return;
            }

            _warnedMissingTriggerCollider = false;

            if (!triggerCollider.isTrigger)
            {
                if (!_warnedTriggerColliderNotTrigger)
                {
                    Debug.LogWarning($"[{nameof(PbfWaterReceiver)}] triggerCollider 不是 Trigger：请勾选 Is Trigger。Collider：{triggerCollider.name} 对象：{name}", this);
                    _warnedTriggerColliderNotTrigger = true;
                }
                return;
            }

            _warnedTriggerColliderNotTrigger = false;
        }

        private void AssertPurificationSystemPresent()
        {
            if (!Application.isPlaying)
                return;

            Debug.Assert(
                FindFirstObjectByType<PurificationSystem>() != null,
                $"[{nameof(PbfWaterReceiver)}] 场景中未放置 {nameof(PurificationSystem)}，水体净化功能将无法工作。对象：{name}",
                this);
        }

        protected PurificationSystem GetPurificationSystemChecked()
        {
            PurificationSystem system = PurificationSystem.Instance;
            Debug.Assert(system != null, $"[{nameof(PbfWaterReceiver)}] {nameof(PurificationSystem)}.Instance 为 null。对象：{name}", this);
            return system;
        }

        protected PurificationIndicator AddPurificationIndicator(string indicatorName, Vector3 positionWorld)
        {
            PurificationSystem system = GetPurificationSystemChecked();
            if (system == null)
                return null;

            return system.AddIndicator(indicatorName, positionWorld, PurificationContributionValue, PurificationIndicatorType, PurificationRadiationRadius);
        }

        protected PurificationIndicator EnsurePurificationIndicator(ref PurificationIndicator indicator, string indicatorName, Vector3 positionWorld, float contributionValue, string indicatorType, float radiationRadius = 8f)
        {
            PurificationSystem system = GetPurificationSystemChecked();
            if (system == null)
                return null;

            if (indicator == null)
            {
                indicator = system.AddIndicator(indicatorName, positionWorld, contributionValue, indicatorType, radiationRadius);
                return indicator;
            }

            float prevContribution = indicator.ContributionValue;

            indicator.Name = indicatorName;
            indicator.Position = positionWorld;
            indicator.ContributionValue = contributionValue;
            indicator.IndicatorType = indicatorType;
            indicator.RadiationRadius = radiationRadius;

            if (system.UseSpatialField)
            {
                float delta = contributionValue - prevContribution;
                if (delta > 0f)
                {
                    system.AddStamp(positionWorld, delta, indicatorType, radiationRadius);
                }
            }

            return indicator;
        }

        protected bool RemovePurificationIndicator(ref PurificationIndicator indicator)
        {
            if (indicator == null)
                return false;

            PurificationSystem system = GetPurificationSystemChecked();
            if (system == null)
            {
                indicator = null;
                return false;
            }

            bool removed = system.RemoveIndicator(indicator);
            system.NotifyAllListeners();
            indicator = null;
            return removed;
        }

        private void OnDrawGizmosSelected()
        {
            if (!TryGetVolumeBoundsWorld(out Bounds b))
                return;
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireCube(b.center, b.size);
        }
    }

    [Serializable]
    public class LocalScaleTransition
    {
        [ChineseLabel("时长(秒)")]
        [DefaultValue(0.25f)]
        public float Duration = 0.25f;

        [ChineseLabel("Q弹曲线")]
        public AnimationCurve Curve = new AnimationCurve(
            new Keyframe(0f, 0f),
            new Keyframe(0.60f, 1.12f),
            new Keyframe(0.82f, 0.98f),
            new Keyframe(1f, 1f));

        public float Evaluate(float t)
        {
            return Curve != null ? Curve.Evaluate(t) : t;
        }
    }
}
