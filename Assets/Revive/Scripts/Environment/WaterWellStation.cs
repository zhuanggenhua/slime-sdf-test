using MoreMountains.Feedbacks;
using MoreMountains.Tools;
using MoreMountains.TopDownEngine;
using Revive.GamePlay.Purification;
using Revive.Slime;
using UnityEngine;

namespace Revive.Environment
{
    /// <summary>
    /// 水井补给站 - 净化激活后提供一次性最大体积升级和持续体积恢复
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Revive/Environment/Water Well Station")]
    public class WaterWellStation : MonoBehaviour, IPurificationListener
    {
        #region 配置参数

        [ChineseHeader("水井标识")]
        [ChineseLabel("水井ID（自动生成）")]
        [Tooltip("自动生成的唯一ID，用于净化指示物名称")]
        [SerializeField] private string wellId;

        [ChineseHeader("激活条件")]
        [ChineseLabel("激活净化度阈值")]
        [Range(0f, 1f)]
        [SerializeField] private float activationThreshold = 1f;

        [ChineseHeader("一次性奖励")]
        [ChineseLabel("最大体积增加量")]
        [Min(0)]
        [SerializeField] private int maxVolumeIncrease = 300;

        [ChineseHeader("升级汇聚效果")]
        [ChineseLabel("升级悬空高度(米)")]
        [SerializeField, Min(0f), DefaultValue(1.5f)] private float upgradeCoalesceHoverHeightWorld = 1.5f;

        [ChineseLabel("升级生成范围倍率")]
        [SerializeField, Range(1f, 20f), DefaultValue(3.5f)] private float upgradeCoalesceSpawnRadius = 3.5f;

        [ChineseLabel("升级垂直分布倍率")]
        [SerializeField, Range(0.1f, 2f), DefaultValue(1f)] private float upgradeCoalesceVerticalScale = 1f;

        [ChineseLabel("升级向心速度倍率")]
        [SerializeField, Range(0f, 2f), DefaultValue(0.5f)] private float upgradeCoalesceInwardVelocityScale = 0.5f;

        [ChineseLabel("升级时直接回满体积")]
        [Tooltip("勾选后，升级时会生成足够的粒子直接恢复到满体积")]
        [SerializeField] private bool restoreToFullOnUpgrade = true;

        [ChineseLabel("每批生成粒子数")]
        [Tooltip("分批生成时每批的粒子数量")]
        [Min(50)]
        [SerializeField] private int particlesPerBatch = 200;

        [ChineseLabel("批次间隔(秒)")]
        [Tooltip("分批生成时每批之间的时间间隔")]
        [Min(0.05f)]
        [SerializeField] private float batchInterval = 0.1f;

        [ChineseHeader("净化指示物")]
        [ChineseLabel("辐射半径(米)")]
        [Min(1f)]
        [SerializeField] private float purificationRadiationRadius = 20f;

        [ChineseLabel("贡献值")]
        [Min(0f)]
        [SerializeField] private float purificationContributionValue = 1f;

        [ChineseLabel("指示物类型")]
        [SerializeField] private string purificationIndicatorType = "WaterWell";

        [ChineseHeader("持续恢复")]
        [ChineseLabel("每秒恢复粒子数")]
        [Min(1)]
        [SerializeField] private int restoreRatePerSecond = 50;

        [ChineseHeader("反馈效果")]
        [ChineseLabel("激活反馈")]
        [SerializeField] private MMFeedbacks activationFeedbacks;

        [ChineseLabel("奖励领取反馈")]
        [SerializeField] private MMFeedbacks rewardClaimFeedbacks;

        [ChineseLabel("恢复中反馈")]
        [SerializeField] private MMFeedbacks restoringFeedbacks;

        #endregion

        #region 状态

        private enum WellState
        {
            Inactive,      // 未激活（净化度不足）
            Active,        // 已激活（净化度达标，等待玩家进入）
            RewardClaimed  // 奖励已领取（一次性升级已完成）
        }

        private WellState _currentState = WellState.Inactive;

        [ChineseHeader("调试")]
        [ChineseLabel("当前位置净化度(运行时)")]
        [SerializeField, MMReadOnly] private float _currentPurificationLevel;

        [ChineseLabel("激活阈值(运行时)")]
        [SerializeField, MMReadOnly] private float _debugActivationThreshold;

        [ChineseLabel("距离激活阈值(运行时)")]
        [SerializeField, MMReadOnly] private float _debugToActivationThreshold;

        private bool _isRegistered;
        private bool _playerInTrigger;
        private float _restoreAccumulator;

        // 分批生成状态
        private int _pendingRestoreParticles;
        private float _nextBatchTime;

        // 缓存的玩家组件引用
        private SlimeVolume _cachedSlimeVolume;
        private Slime_PBF _cachedSlimePBF;

        #endregion

        #region Unity 生命周期

        private void Awake()
        {
            // 自动生成唯一 WellId（基于 GameObject InstanceID）
            if (string.IsNullOrEmpty(wellId))
            {
                wellId = $"{gameObject.name}_{gameObject.GetInstanceID()}";
            }
        }

        private void Start()
        {
            // 自动注册到净化系统
            TryRegister();
        }

        private void OnDestroy()
        {
            if (_isRegistered && PurificationSystem.HasInstance)
            {
                PurificationSystem.Instance.UnregisterListener(this);
            }
        }

        private void Update()
        {
            TryRegister();

            // 处理分批生成
            if (_pendingRestoreParticles > 0 && _cachedSlimePBF != null)
            {
                ProcessBatchRestore();
            }
            // 分批生成完成后才进行持续恢复
            else if (_playerInTrigger && _currentState == WellState.RewardClaimed)
            {
                ProcessVolumeRestore();
            }
        }

        #endregion

        #region IPurificationListener 实现

        public void OnPurificationChanged(float purificationLevel, Vector3 position)
        {
            _currentPurificationLevel = purificationLevel;
            _debugActivationThreshold = activationThreshold;
            _debugToActivationThreshold = activationThreshold - _currentPurificationLevel;

            // 检查激活条件
            if (_currentState == WellState.Inactive && purificationLevel >= activationThreshold)
            {
                ActivateWell();
            }
        }

        private void TryRegister()
        {
            if (_isRegistered)
                return;
            if (!Application.isPlaying)
                return;
            if (!PurificationSystem.HasInstance)
                return;

            PurificationSystem.Instance.RegisterListener(this);
            _isRegistered = true;

            _debugActivationThreshold = activationThreshold;

            float level = PurificationSystem.Instance.GetPurificationLevel(GetListenerPosition());
            _currentPurificationLevel = level;
            _debugToActivationThreshold = activationThreshold - _currentPurificationLevel;
            if (_currentState == WellState.Inactive && level >= activationThreshold)
            {
                ActivateWell();
            }
        }

        public Vector3 GetListenerPosition()
        {
            return transform.position;
        }

        public string GetListenerName()
        {
            return $"WaterWell_{wellId}";
        }

        #endregion

        #region 触发器逻辑

        private void OnTriggerEnter(Collider other)
        {
            // 检测玩家
            var controller = other.GetComponentInParent<TopDownController3D>();
            if (controller == null)
                return;

            var character = controller.GetComponent<Character>();
            if (character == null || !character.CharacterType.Equals(Character.CharacterTypes.Player))
                return;

            // 缓存组件引用
            _cachedSlimeVolume = character.GetComponentInChildren<SlimeVolume>();
            _cachedSlimePBF = character.GetComponentInChildren<Slime_PBF>();

            if (_cachedSlimeVolume == null || _cachedSlimePBF == null)
            {
                Debug.LogWarning($"[WaterWellStation] 玩家缺少 SlimeVolume 或 Slime_PBF 组件");
                return;
            }

            _playerInTrigger = true;

            // 首次进入激活状态的水井 -> 领取一次性奖励
            if (_currentState == WellState.Active)
            {
                ClaimReward();
            }

            // 开始恢复反馈
            if (_currentState != WellState.Inactive)
            {
                MMFeedbacksHelper.Play(restoringFeedbacks);

                if (_cachedSlimePBF != null && GetMaxRestoreAmountIncludingSeparatedEmitted() > 0)
                {
                    _cachedSlimePBF.PlayMergeSfx();
                }
            }
        }

        private void OnTriggerExit(Collider other)
        {
            var controller = other.GetComponentInParent<TopDownController3D>();
            if (controller == null)
                return;

            var character = controller.GetComponent<Character>();
            if (character == null || !character.CharacterType.Equals(Character.CharacterTypes.Player))
                return;

            _playerInTrigger = false;
            _restoreAccumulator = 0f;
            _pendingRestoreParticles = 0;
            _nextBatchTime = 0f;
            _cachedSlimePBF?.EndExternalCoalesceLock();
            _cachedSlimeVolume = null;
            _cachedSlimePBF = null;

            // 停止恢复反馈
            MMFeedbacksHelper.Stop(restoringFeedbacks);
        }

        #endregion

        #region 核心逻辑

        private void ActivateWell()
        {
            _currentState = WellState.Active;
            Debug.Log($"[WaterWellStation] {wellId} 已激活！净化度: {_currentPurificationLevel:F2}");

            MMFeedbacksHelper.Play(activationFeedbacks);

            if (_playerInTrigger && _cachedSlimeVolume != null && _cachedSlimePBF != null)
            {
                ClaimReward();
                MMFeedbacksHelper.Play(restoringFeedbacks);
            }
        }

        private void ClaimReward()
        {
            if (_currentState != WellState.Active)
                return;

            // 1. 增加最大体积
            if (_cachedSlimeVolume != null && maxVolumeIncrease > 0)
            {
                _cachedSlimeVolume.maxVolume += maxVolumeIncrease;
                _cachedSlimeVolume.ForceBroadcast();
                Debug.Log($"[WaterWellStation] {wellId} 最大体积增加 {maxVolumeIncrease}，当前上限: {_cachedSlimeVolume.maxVolume}");
            }

            _cachedSlimePBF?.PlayMergeSfx();

            // 2. 如果开启直接回满，计算需要恢复的粒子数并启动分批生成
            if (restoreToFullOnUpgrade && _cachedSlimeVolume != null && _cachedSlimePBF != null)
            {
                int needToRestore = GetMaxRestoreAmountIncludingSeparatedEmitted();
                if (needToRestore > 0)
                {
                    Vector3 anchorWorld = transform.position;
                    bool locked = _cachedSlimePBF.BeginExternalCoalesceLockAtWorld(anchorWorld, upgradeCoalesceHoverHeightWorld);
                    if (locked)
                    {
                        _pendingRestoreParticles = needToRestore;
                        _nextBatchTime = Time.time; // 立即开始第一批
                        Debug.Log($"[WaterWellStation] {wellId} 启动分批恢复，总计 {needToRestore} 粒子");
                    }
                }
            }

            // 3. 创建净化指示物（基于 WellId 生成唯一名称，避免重复）
            CreatePurificationIndicator();

            // 4. 播放奖励反馈
            MMFeedbacksHelper.Play(rewardClaimFeedbacks);

            // 5. 状态转换
            _currentState = WellState.RewardClaimed;
            Debug.Log($"[WaterWellStation] {wellId} 奖励已领取！");
        }

        private void ProcessBatchRestore()
        {
            if (Time.time < _nextBatchTime)
                return;

            int thisBatch = Mathf.Min(_pendingRestoreParticles, particlesPerBatch);
            if (thisBatch <= 0)
            {
                _pendingRestoreParticles = 0;
                return;
            }

            Vector3 coalesceCenterWorld = transform.position + Vector3.up * Mathf.Max(0f, upgradeCoalesceHoverHeightWorld);
            int restored = _cachedSlimePBF.RestoreMainBodyParticlesAtWorldCenter(thisBatch, coalesceCenterWorld, upgradeCoalesceSpawnRadius, upgradeCoalesceVerticalScale, upgradeCoalesceInwardVelocityScale);
            _pendingRestoreParticles -= restored;
            _nextBatchTime = Time.time + batchInterval;

            if (restored > 0)
            {
                _cachedSlimePBF.PlayMergeSfx();
            }

            if (_pendingRestoreParticles <= 0)
            {
                _cachedSlimePBF.EndExternalCoalesceLock();
                Debug.Log($"[WaterWellStation] {wellId} 分批恢复完成");
            }
        }

        private void CreatePurificationIndicator()
        {
            if (!PurificationSystem.HasInstance)
                return;

            string indicatorName = $"WaterWell_{wellId}";

            // 检查是否已存在同名指示物（防止重复创建）
            var existingIndicators = PurificationSystem.Instance.GetAllIndicators();
            foreach (var indicator in existingIndicators)
            {
                if (indicator.Name == indicatorName)
                {
                    Debug.Log($"[WaterWellStation] 指示物 {indicatorName} 已存在，跳过创建");
                    return;
                }
            }

            // 创建新指示物
            PurificationSystem.Instance.AddIndicator(
                indicatorName,
                transform.position,
                purificationContributionValue,
                purificationIndicatorType,
                purificationRadiationRadius
            );

            Debug.Log($"[WaterWellStation] 创建净化指示物: {indicatorName}, 半径: {purificationRadiationRadius}m");
        }

        private void ProcessVolumeRestore()
        {
            if (_cachedSlimeVolume == null || _cachedSlimePBF == null)
                return;

            int maxRestore = GetMaxRestoreAmountIncludingSeparatedEmitted();
            if (maxRestore <= 0)
                return;

            // 累积恢复量
            _restoreAccumulator += restoreRatePerSecond * Time.deltaTime;

            // 每累积1个粒子就恢复
            int toRestore = Mathf.FloorToInt(_restoreAccumulator);
            if (toRestore > 0)
            {
                // 限制不超过“真实可恢复量”（包含 Separated/Emitted 占用）
                toRestore = Mathf.Min(toRestore, maxRestore);

                if (toRestore > 0)
                {
                    int restored = _cachedSlimePBF.RestoreMainBodyParticles(toRestore);
                    _restoreAccumulator -= restored;

                    if (restored > 0)
                    {
                        _cachedSlimePBF.PlayMergeSfx();
                    }
                }
                else
                {
                    _restoreAccumulator = 0f;
                }
            }
        }

        private int GetMaxRestoreAmountIncludingSeparatedEmitted()
        {
            if (_cachedSlimeVolume == null || _cachedSlimePBF == null)
                return 0;

            _cachedSlimePBF.GetVolumeParticleCounts(out int mainBody, out int separated, out int emitted);

            // SlimeVolume.currentVolume = mainBody + storedVolume
            // => storedVolume = currentVolume - mainBody
            int storedVolume = Mathf.Max(0, _cachedSlimeVolume.CurrentVolume - mainBody);

            int occupied = mainBody + separated + emitted + storedVolume;
            return Mathf.Max(0, _cachedSlimeVolume.MaxVolume - occupied);
        }

        #endregion

        #region Editor / Debug

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            // 绘制净化辐射范围
            Gizmos.color = new Color(0.2f, 0.6f, 1f, 0.3f);
            Gizmos.DrawWireSphere(transform.position, purificationRadiationRadius);

            // 绘制状态指示
            Gizmos.color = _currentState switch
            {
                WellState.Inactive => Color.gray,
                WellState.Active => Color.yellow,
                WellState.RewardClaimed => Color.green,
                _ => Color.white
            };
            Gizmos.DrawWireSphere(transform.position, 1f);
        }
#endif

        #endregion
    }
}
