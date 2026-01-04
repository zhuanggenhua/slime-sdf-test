using System.Collections.Generic;
using MoreMountains.Tools;
using Revive.GamePlay.Purification.Rendering;
using Revive.Slime;
using UnityEngine;

namespace Revive.GamePlay.Purification
{
    public enum PurificationRestoreTrigger
    {
        PurificationFull = 0,
    }

    public interface IPurificationRestoreTarget
    {
        void OnPurificationRestored(PurificationRestoreTrigger trigger, Vector3 positionWorld);
    }

    public interface IPurificationUnlockIndicatorProvider
    {
        bool TryGetUnlockIndicatorConfig(
            PurificationRestoreTrigger trigger,
            Vector3 unlockPositionWorld,
            out string indicatorName,
            out Vector3 indicatorPositionWorld);
    }

    [DisallowMultipleComponent]
    [AddComponentMenu("Revive/Purification/Purification Restore Gate")]
    public sealed class PurificationRestoreGate : MonoBehaviour, IPurificationListener
    {
        [ChineseHeader("解锁条件")]
        [ChineseLabel("被动：满净化解锁")]
        [DefaultValue(true)]
        [SerializeField] private bool unlockWhenPurificationFull = true;

        [ChineseLabel("满净化阈值")]
        [Range(0f, 1f)]
        [DefaultValue(0.99f)]
        [SerializeField] private float fullThreshold = 0.99f;

        [ChineseHeader("浇水")]
        [ChineseLabel("阈值降低倍率(每1水量)")]
        [DefaultValue(0.01f)]
        [SerializeField] private float thresholdDeltaPerWater = 0.01f;

        [ChineseHeader("解锁指示物")]
        [ChineseLabel("解锁时生成指示物")]
        [DefaultValue(true)]
        [SerializeField] private bool addUnlockIndicator = true;

        [ChineseLabel("指示物类型")]
        [DefaultValue("Water")]
        [SerializeField] private string unlockIndicatorType = "Water";

        [ChineseLabel("贡献值")]
        [DefaultValue(10f)]
        [SerializeField] private float unlockIndicatorContributionValue = 10f;

        [ChineseLabel("辐射范围(米)")]
        [DefaultValue(8f)]
        [SerializeField] private float unlockIndicatorRadiationRadius = 5f;

        [ChineseLabel("累计阈值降低(运行时)")]
        [SerializeField] private float thresholdDeltaAccumulated;

        [ChineseLabel("监听位置(可空)")]
        [SerializeField] private Transform listenPoint;

        [ChineseHeader("更新")]
        [ChineseLabel("轮询间隔(秒,0=禁用)")]
        [DefaultValue(0f)]
        [SerializeField] private float pollIntervalSeconds = 0f;

        [ChineseLabel("解锁后注销监听")]
        [DefaultValue(true)]
        [SerializeField] private bool unregisterAfterUnlocked = true;

        [ChineseHeader("解锁后切换")]
        [ChineseLabel("解锁后启用物体")]
        [SerializeField] private GameObject[] enableGameObjects;

        [ChineseLabel("解锁后禁用物体")]
        [SerializeField] private GameObject[] disableGameObjects;

        [ChineseLabel("解锁后启用组件")]
        [SerializeField] private Behaviour[] enableBehaviours;

        [ChineseLabel("解锁后禁用组件")]
        [SerializeField] private Behaviour[] disableBehaviours;

        [ChineseHeader("锁定视觉")]
        [ChineseLabel("锁定时视觉渲染器")]
        [SerializeField] private Renderer lockVisualRenderer;

        [ChineseHeader("回调")]
        [ChineseLabel("恢复目标(可空)")]
        [Tooltip("会对实现 IPurificationRestoreTarget 的组件调用 OnPurificationRestored")]
        [SerializeField] private MonoBehaviour[] restoreTargets;

        [ChineseLabel("自动收集本物体上的恢复目标")]
        [DefaultValue(true)]
        [SerializeField] private bool autoCollectRestoreTargetsOnSelf = true;

        [ChineseHeader("运行时")]
        [ChineseLabel("已解锁(运行时)")]
        [SerializeField] private bool unlocked;

        [ChineseHeader("调试")]
        [ChineseLabel("当前位置净化度(运行时)")]
        [SerializeField, MMReadOnly] private float debugCurrentPurificationLevel;

        [ChineseLabel("有效阈值(运行时)")]
        [SerializeField, MMReadOnly] private float debugEffectiveThreshold;

        [ChineseLabel("距离阈值(运行时)")]
        [SerializeField, MMReadOnly] private float debugToThreshold;

        [ChineseLabel("浇水进度(运行时)")]
        [SerializeField, MMReadOnly] private float debugWaterProgress01;

        private bool _registered;
        private float _nextPollTime;
        private readonly List<IPurificationRestoreTarget> _resolvedTargets = new List<IPurificationRestoreTarget>(8);

        private int _lockedVisualKey;
        private Renderer[] _lockVisualRenderers;
        private bool _lockVisualRegistered;

        private bool _unlockFadeActive;
        private float _unlockFadeStartTime;
        private float _unlockFadeSeconds;
        private float _unlockFadeFeather01;
        private Bounds _unlockFadeBounds;
        private bool _unlockFadeBoundsValid;
        private MaterialPropertyBlock _unlockFadeMpb;

        private static readonly int LockedFadeParamsId = Shader.PropertyToID("_LockedFadeParams");
        private static readonly int LockedFade01Id = Shader.PropertyToID("_LockedFade01");

        public bool Unlocked => unlocked;
        public float FullThreshold => fullThreshold;
        public float ThresholdDeltaAccumulated => thresholdDeltaAccumulated;

        private void Awake()
        {
            ResolveRestoreTargets();
            ResolveLockVisualRenderers();
            _lockedVisualKey = GetInstanceID();
            ApplyCurrentState();
        }

        private void OnEnable()
        {
            ResolveRestoreTargets();
            ResolveLockVisualRenderers();
            _lockedVisualKey = GetInstanceID();
            ApplyCurrentState();
            TryRegister();
        }

        private void OnDisable()
        {
            TryUnregister();
            UnregisterLockedVisual();
        }

        private void Update()
        {
            if (!Application.isPlaying)
                return;

            if (_unlockFadeActive)
            {
                UpdateUnlockFade();
                return;
            }

            if (unlocked)
                return;

            TryRegister();

            RegisterOrUpdateLockedVisual();

            if (!unlockWhenPurificationFull)
                return;

            debugEffectiveThreshold = GetEffectiveFullThreshold();
            debugWaterProgress01 = GetWaterProgress01();

            float interval = Mathf.Max(0f, pollIntervalSeconds);
            if (interval <= 0f)
                return;

            if (Time.time < _nextPollTime)
                return;

            _nextPollTime = Time.time + interval;

            if (!PurificationSystem.HasInstance)
                return;

            Vector3 pos = GetListenerPosition();
            float level = PurificationSystem.Instance.GetPurificationLevel(pos);

            debugCurrentPurificationLevel = level;
            debugEffectiveThreshold = GetEffectiveFullThreshold();
            debugToThreshold = debugEffectiveThreshold - debugCurrentPurificationLevel;
            debugWaterProgress01 = GetWaterProgress01();

            if (level >= GetEffectiveFullThreshold())
            {
                Unlock(PurificationRestoreTrigger.PurificationFull, pos);
            }
        }

        public void NotifyWaterAdded(float waterAmount, Vector3 positionWorld)
        {
            if (unlocked)
                return;

            float perWater = Mathf.Max(0f, thresholdDeltaPerWater);
            if (perWater <= 0f)
                return;

            float delta = waterAmount * perWater;
            if (delta <= 0f)
                return;

            thresholdDeltaAccumulated += delta;

            debugEffectiveThreshold = GetEffectiveFullThreshold();
            debugWaterProgress01 = GetWaterProgress01();

            if (!PurificationSystem.HasInstance)
                return;

            Vector3 pos = GetListenerPosition();
            float level = PurificationSystem.Instance.GetPurificationLevel(pos);

            debugCurrentPurificationLevel = level;
            debugToThreshold = debugEffectiveThreshold - debugCurrentPurificationLevel;

            if (level >= GetEffectiveFullThreshold())
            {
                Unlock(PurificationRestoreTrigger.PurificationFull, pos);
            }
        }

        public void OnPurificationChanged(float purificationLevel, Vector3 position)
        {
            if (unlocked)
                return;

            if (!unlockWhenPurificationFull)
                return;

            if (purificationLevel >= GetEffectiveFullThreshold())
            {
                Unlock(PurificationRestoreTrigger.PurificationFull, position);
            }
        }

        public float GetEffectiveFullThreshold()
        {
            float t = fullThreshold - thresholdDeltaAccumulated;
            return Mathf.Clamp01(t);
        }

        public float GetWaterProgress01()
        {
            float baseT = Mathf.Max(0.0001f, fullThreshold);
            float curr = GetEffectiveFullThreshold();
            return Mathf.Clamp01(1f - (curr / baseT));
        }

        public Vector3 GetListenerPosition()
        {
            if (listenPoint != null)
                return listenPoint.position;

            return transform != null ? transform.position : Vector3.zero;
        }

        public string GetListenerName()
        {
            return name;
        }

        private void TryRegister()
        {
            if (!Application.isPlaying)
                return;
            if (unlocked)
                return;
            if (_registered)
                return;

            if (!PurificationSystem.HasInstance)
                return;

            PurificationSystem.Instance.RegisterListener(this);
            _registered = true;
        }

        private void TryUnregister()
        {
            if (!_registered)
                return;

            if (!PurificationSystem.HasInstance)
            {
                _registered = false;
                return;
            }

            PurificationSystem.Instance.UnregisterListener(this);
            _registered = false;
        }

        private void Unlock(PurificationRestoreTrigger trigger, Vector3 positionWorld)
        {
            if (unlocked)
                return;

            unlocked = true;

            if (!TryStartUnlockFade())
            {
                UnregisterLockedVisual();
            }

            ApplyUnlockedState();

            TryAddUnlockIndicator(trigger, positionWorld);

            ResolveRestoreTargets();
            for (int i = 0; i < _resolvedTargets.Count; i++)
            {
                var t = _resolvedTargets[i];
                if (t == null)
                    continue;

                t.OnPurificationRestored(trigger, positionWorld);
            }

            if (unregisterAfterUnlocked)
            {
                TryUnregister();
            }
        }

        private void TryAddUnlockIndicator(PurificationRestoreTrigger trigger, Vector3 unlockPositionWorld)
        {
            if (!PurificationSystem.HasInstance)
                return;

            if (!addUnlockIndicator)
                return;

            if (string.IsNullOrEmpty(unlockIndicatorType))
                return;
            if (unlockIndicatorContributionValue <= 0f)
                return;
            if (unlockIndicatorRadiationRadius <= 0f)
                return;

            string indicatorName = $"{name}_Unlock_{GetInstanceID()}";
            Vector3 indicatorPositionWorld = GetListenerPosition();

            IPurificationUnlockIndicatorProvider provider = ResolveUnlockIndicatorProvider();
            if (provider != null)
            {
                if (provider.TryGetUnlockIndicatorConfig(
                        trigger,
                        unlockPositionWorld,
                        out string overrideName,
                        out Vector3 overridePos))
                {
                    if (!string.IsNullOrEmpty(overrideName))
                        indicatorName = overrideName;
                    indicatorPositionWorld = overridePos;
                }
            }

            if (string.IsNullOrEmpty(indicatorName))
                return;

            PurificationSystem.Instance.AddIndicator(
                indicatorName,
                indicatorPositionWorld,
                unlockIndicatorContributionValue,
                unlockIndicatorType,
                unlockIndicatorRadiationRadius);
        }

        private IPurificationUnlockIndicatorProvider ResolveUnlockIndicatorProvider()
        {
            if (restoreTargets != null)
            {
                for (int i = 0; i < restoreTargets.Length; i++)
                {
                    MonoBehaviour mb = restoreTargets[i];
                    if (mb == null)
                        continue;
                    if (mb == this)
                        continue;

                    if (mb is IPurificationUnlockIndicatorProvider p)
                        return p;
                }
            }

            MonoBehaviour[] monoBehaviours = GetComponents<MonoBehaviour>();
            if (monoBehaviours == null || monoBehaviours.Length == 0)
                return null;

            for (int i = 0; i < monoBehaviours.Length; i++)
            {
                MonoBehaviour mb = monoBehaviours[i];
                if (mb == null)
                    continue;
                if (mb == this)
                    continue;

                if (mb is IPurificationUnlockIndicatorProvider p)
                    return p;
            }

            return null;
        }

        private void ApplyCurrentState()
        {
            if (!unlocked)
                ApplyLockedState();
            else
                ApplyUnlockedState();
        }

        private bool TryStartUnlockFade()
        {
            if (_lockVisualRenderers == null || _lockVisualRenderers.Length == 0)
                return false;

            if (!PurificationWorldVisualFeature.TryGetUnlockFadeSettings(out float seconds, out float feather01))
                return false;

            seconds = Mathf.Max(0f, seconds);
            if (seconds <= 0f)
                return false;

            if (!_lockVisualRegistered)
            {
                PurificationWorldVisualFeature.SetLockedRenderers(_lockedVisualKey, _lockVisualRenderers);
                _lockVisualRegistered = true;
            }

            _unlockFadeSeconds = seconds;
            _unlockFadeFeather01 = Mathf.Clamp(feather01, 0.001f, 0.5f);
            _unlockFadeStartTime = Time.time;
            _unlockFadeActive = true;

            _unlockFadeBoundsValid = false;
            for (int i = 0; i < _lockVisualRenderers.Length; i++)
            {
                var r = _lockVisualRenderers[i];
                if (r == null)
                    continue;

                Bounds b = r.bounds;
                if (!_unlockFadeBoundsValid)
                {
                    _unlockFadeBounds = b;
                    _unlockFadeBoundsValid = true;
                }
                else
                {
                    _unlockFadeBounds.Encapsulate(b);
                }
            }

            if (!_unlockFadeBoundsValid)
            {
                _unlockFadeActive = false;
                return false;
            }

            _unlockFadeMpb ??= new MaterialPropertyBlock();
            ApplyUnlockFade01(0f);
            return true;
        }

        private void UpdateUnlockFade()
        {
            float t = (Time.time - _unlockFadeStartTime) / Mathf.Max(0.0001f, _unlockFadeSeconds);
            if (t >= 1f)
            {
                ApplyUnlockFade01(1f);
                _unlockFadeActive = false;
                UnregisterLockedVisual();
                return;
            }

            ApplyUnlockFade01(Mathf.Clamp01(t));
        }

        private void ApplyUnlockFade01(float fade01)
        {
            if (_lockVisualRenderers == null || _lockVisualRenderers.Length == 0)
                return;
            if (!_unlockFadeBoundsValid)
                return;

            float bottomY = _unlockFadeBounds.min.y;
            float height = Mathf.Max(0.01f, _unlockFadeBounds.max.y - bottomY);
            float invHeight = 1f / height;

            Vector4 fadeParams = new Vector4(bottomY, invHeight, _unlockFadeFeather01, 0f);

            for (int i = 0; i < _lockVisualRenderers.Length; i++)
            {
                var r = _lockVisualRenderers[i];
                if (r == null)
                    continue;

                r.GetPropertyBlock(_unlockFadeMpb);
                _unlockFadeMpb.SetVector(LockedFadeParamsId, fadeParams);
                _unlockFadeMpb.SetFloat(LockedFade01Id, fade01);
                r.SetPropertyBlock(_unlockFadeMpb);
            }
        }

        private void ApplyLockedState()
        {
            RegisterOrUpdateLockedVisual();

            if (enableGameObjects != null)
            {
                for (int i = 0; i < enableGameObjects.Length; i++)
                {
                    var go = enableGameObjects[i];
                    if (go != null)
                        go.SetActive(false);
                }
            }

            if (disableGameObjects != null)
            {
                for (int i = 0; i < disableGameObjects.Length; i++)
                {
                    var go = disableGameObjects[i];
                    if (go != null)
                        go.SetActive(true);
                }
            }

            if (enableBehaviours != null)
            {
                for (int i = 0; i < enableBehaviours.Length; i++)
                {
                    var b = enableBehaviours[i];
                    if (b != null)
                        b.enabled = false;
                }
            }

            if (disableBehaviours != null)
            {
                for (int i = 0; i < disableBehaviours.Length; i++)
                {
                    var b = disableBehaviours[i];
                    if (b != null)
                        b.enabled = true;
                }
            }
        }

        private void ApplyUnlockedState()
        {
            if (!_unlockFadeActive)
                UnregisterLockedVisual();

            if (disableGameObjects != null)
            {
                for (int i = 0; i < disableGameObjects.Length; i++)
                {
                    var go = disableGameObjects[i];
                    if (go != null)
                        go.SetActive(false);
                }
            }

            if (enableGameObjects != null)
            {
                for (int i = 0; i < enableGameObjects.Length; i++)
                {
                    var go = enableGameObjects[i];
                    if (go != null)
                        go.SetActive(true);
                }
            }

            if (disableBehaviours != null)
            {
                for (int i = 0; i < disableBehaviours.Length; i++)
                {
                    var b = disableBehaviours[i];
                    if (b != null)
                        b.enabled = false;
                }
            }

            if (enableBehaviours != null)
            {
                for (int i = 0; i < enableBehaviours.Length; i++)
                {
                    var b = enableBehaviours[i];
                    if (b != null)
                        b.enabled = true;
                }
            }
        }

        private void ResolveRestoreTargets()
        {
            _resolvedTargets.Clear();

            if (restoreTargets != null)
            {
                for (int i = 0; i < restoreTargets.Length; i++)
                {
                    var mb = restoreTargets[i];
                    if (mb == null)
                        continue;

                    if (mb is IPurificationRestoreTarget t)
                    {
                        _resolvedTargets.Add(t);
                    }
                }
            }

            if (_resolvedTargets.Count > 0)
                return;

            if (!autoCollectRestoreTargetsOnSelf)
                return;

            var monoBehaviours = GetComponents<MonoBehaviour>();
            if (monoBehaviours == null || monoBehaviours.Length == 0)
                return;

            for (int i = 0; i < monoBehaviours.Length; i++)
            {
                var mb = monoBehaviours[i];
                if (mb == null)
                    continue;
                if (mb == this)
                    continue;

                if (mb is IPurificationRestoreTarget t)
                {
                    _resolvedTargets.Add(t);
                }
            }
        }

        private void RegisterOrUpdateLockedVisual()
        {
            if (unlocked)
                return;
            if (!Application.isPlaying)
                return;
            if (_lockVisualRegistered)
                return;

            if (_lockVisualRenderers == null || _lockVisualRenderers.Length == 0)
                ResolveLockVisualRenderers();
            if (_lockVisualRenderers == null || _lockVisualRenderers.Length == 0)
                return;

            PurificationWorldVisualFeature.SetLockedRenderers(_lockedVisualKey, _lockVisualRenderers);
            _lockVisualRegistered = true;
        }

        private void UnregisterLockedVisual()
        {
            if (!Application.isPlaying)
                return;

            PurificationWorldVisualFeature.RemoveLockedRenderers(_lockedVisualKey);
            _lockVisualRegistered = false;
        }

        private void ResolveLockVisualRenderers()
        {
            if (lockVisualRenderer != null)
            {
                _lockVisualRenderers = new[] { lockVisualRenderer };
                return;
            }

            Renderer[] all = GetComponentsInChildren<Renderer>(includeInactive: true);
            if (all == null || all.Length == 0)
            {
                _lockVisualRenderers = null;
                return;
            }

            int count = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Renderer r = all[i];
                if (r == null)
                    continue;
                if (r is MeshRenderer || r is SkinnedMeshRenderer)
                    count++;
            }

            if (count <= 0)
            {
                _lockVisualRenderers = null;
                return;
            }

            _lockVisualRenderers = new Renderer[count];
            int w = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Renderer r = all[i];
                if (r == null)
                    continue;
                if (r is MeshRenderer || r is SkinnedMeshRenderer)
                    _lockVisualRenderers[w++] = r;
            }
        }
    }
}
