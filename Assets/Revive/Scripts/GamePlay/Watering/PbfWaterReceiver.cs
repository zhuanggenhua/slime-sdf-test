using System;
using System.Collections;
using System.Collections.Generic;
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
    public class PbfWaterReceiver : MonoBehaviour
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

        private bool _warnedMissingTriggerCollider;
        private bool _warnedTriggerColliderNotTrigger;
        private bool _warnedMissingTarget;

        private IPbfWaterTarget _target;

        public IPbfWaterTarget Target => _target;

        public Collider TriggerCollider => triggerCollider;

        public float WaterPerParticle => waterPerParticle;
        public int MaxConsumePerUpdate => maxConsumePerUpdate;

        public bool ConsumeEmitted => consumeEmitted;
        public bool ConsumeSeparated => consumeSeparated;
        public bool ConsumeDroplets => consumeDroplets;

        public virtual bool WantsWater => true;

        private Coroutine _localScaleTransitionRoutine;

        public bool ContainsPointWorld(Vector3 pointWorld)
        {
            if (triggerCollider == null || !triggerCollider.enabled)
                return false;

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
        }

        protected virtual void OnEnable()
        {
            Register(this);

            ValidateTriggerCollider();
            ResolveReferences();
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
