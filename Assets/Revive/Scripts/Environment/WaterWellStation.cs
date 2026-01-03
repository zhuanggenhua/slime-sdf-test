using MoreMountains.Feedbacks;
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
        [ChineseLabel("水井ID")]
        [Tooltip("用于生成唯一净化指示物名称，默认使用 GameObject 名称")]
        [SerializeField] private string wellId;

        [ChineseHeader("激活条件")]
        [ChineseLabel("激活净化度阈值")]
        [Range(0f, 1f)]
        [SerializeField] private float activationThreshold = 0.6f;

        [ChineseHeader("一次性奖励")]
        [ChineseLabel("最大体积增加量")]
        [Min(0)]
        [SerializeField] private int maxVolumeIncrease = 300;

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
        [SerializeField] private int restoreRatePerSecond = 100;

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
        private float _currentPurificationLevel;
        private bool _isRegistered;
        private bool _playerInTrigger;
        private float _restoreAccumulator;

        // 缓存的玩家组件引用
        private SlimeVolume _cachedSlimeVolume;
        private Slime_PBF _cachedSlimePBF;

        #endregion

        #region Unity 生命周期

        private void Awake()
        {
            if (string.IsNullOrEmpty(wellId))
            {
                wellId = gameObject.name;
            }
        }

        private void Start()
        {
            // 自动注册到净化系统
            if (PurificationSystem.HasInstance)
            {
                PurificationSystem.Instance.RegisterListener(this);
                _isRegistered = true;
            }
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
            if (_playerInTrigger && _currentState != WellState.Inactive)
            {
                ProcessVolumeRestore();
            }
        }

        #endregion

        #region IPurificationListener 实现

        public void OnPurificationChanged(float purificationLevel, Vector3 position)
        {
            _currentPurificationLevel = purificationLevel;

            // 检查激活条件
            if (_currentState == WellState.Inactive && purificationLevel >= activationThreshold)
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
            if (_currentState == WellState.Inactive)
                return;

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
            restoringFeedbacks?.PlayFeedbacks();
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
            _cachedSlimeVolume = null;
            _cachedSlimePBF = null;

            // 停止恢复反馈
            restoringFeedbacks?.StopFeedbacks();
        }

        #endregion

        #region 核心逻辑

        private void ActivateWell()
        {
            _currentState = WellState.Active;
            Debug.Log($"[WaterWellStation] {wellId} 已激活！净化度: {_currentPurificationLevel:F2}");

            activationFeedbacks?.PlayFeedbacks();
        }

        private void ClaimReward()
        {
            if (_currentState != WellState.Active)
                return;

            // 1. 增加最大体积
            if (_cachedSlimeVolume != null && maxVolumeIncrease > 0)
            {
                _cachedSlimeVolume.maxVolume += maxVolumeIncrease;
                Debug.Log($"[WaterWellStation] {wellId} 最大体积增加 {maxVolumeIncrease}，当前上限: {_cachedSlimeVolume.maxVolume}");
            }

            // 2. 创建净化指示物（基于 WellId 生成唯一名称，避免重复）
            CreatePurificationIndicator();

            // 3. 播放奖励反馈
            rewardClaimFeedbacks?.PlayFeedbacks();

            // 4. 状态转换
            _currentState = WellState.RewardClaimed;
            Debug.Log($"[WaterWellStation] {wellId} 奖励已领取！");
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

            // 检查是否需要恢复
            if (!_cachedSlimeVolume.CanAbsorb())
                return;

            // 累积恢复量
            _restoreAccumulator += restoreRatePerSecond * Time.deltaTime;

            // 每累积1个粒子就恢复
            int toRestore = Mathf.FloorToInt(_restoreAccumulator);
            if (toRestore > 0)
            {
                // 限制不超过可吸收量
                int maxAbsorb = _cachedSlimeVolume.GetMaxAbsorbAmount();
                toRestore = Mathf.Min(toRestore, maxAbsorb);

                if (toRestore > 0)
                {
                    int restored = _cachedSlimePBF.RestoreMainBodyParticles(toRestore);
                    _restoreAccumulator -= restored;
                }
                else
                {
                    _restoreAccumulator = 0f;
                }
            }
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
