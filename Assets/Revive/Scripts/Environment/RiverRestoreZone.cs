using MoreMountains.Feedbacks;
using MoreMountains.TopDownEngine;
using Revive.Slime;
using UnityEngine;

namespace Revive.Environment
{
    /// <summary>
    /// 河流补给区 - 玩家进入后持续恢复体积（无激活条件、无升级、无净化指示物）
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Revive/Environment/River Restore Zone")]
    public class RiverRestoreZone : MonoBehaviour
    {
        #region 配置参数

        [ChineseHeader("恢复设置")]
        [ChineseLabel("每秒恢复粒子数")]
        [Min(1), DefaultValue(50)]
        [SerializeField] private int restoreRatePerSecond = 50;

        [ChineseHeader("反馈效果")]
        [ChineseLabel("恢复中反馈")]
        [SerializeField] private MMFeedbacks restoringFeedbacks;

        #endregion

        #region 状态

        private bool _playerInTrigger;
        private float _restoreAccumulator;

        private SlimeVolume _cachedSlimeVolume;
        private Slime_PBF _cachedSlimePBF;

        #endregion

        #region Unity 生命周期

        private void Update()
        {
            if (!_playerInTrigger || _cachedSlimePBF == null || _cachedSlimeVolume == null)
                return;

            ProcessVolumeRestore();
        }

        private void OnTriggerEnter(Collider other)
        {
            var controller = other.GetComponentInParent<TopDownController3D>();
            if (controller == null)
                return;

            var character = controller.GetComponent<Character>();
            if (character == null || !character.CharacterType.Equals(Character.CharacterTypes.Player))
                return;

            _cachedSlimeVolume = character.GetComponentInChildren<SlimeVolume>();
            _cachedSlimePBF = character.GetComponentInChildren<Slime_PBF>();

            if (_cachedSlimeVolume == null || _cachedSlimePBF == null)
            {
                Debug.LogWarning($"[RiverRestoreZone] 玩家缺少 SlimeVolume 或 Slime_PBF 组件");
                return;
            }

            _playerInTrigger = true;
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

            restoringFeedbacks?.StopFeedbacks();
        }

        #endregion

        #region 核心逻辑

        private void ProcessVolumeRestore()
        {
            int maxRestore = GetMaxRestoreAmount();
            if (maxRestore <= 0)
                return;

            _restoreAccumulator += restoreRatePerSecond * Time.deltaTime;

            int toRestore = Mathf.FloorToInt(_restoreAccumulator);
            if (toRestore > 0)
            {
                toRestore = Mathf.Min(toRestore, maxRestore);

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

        private int GetMaxRestoreAmount()
        {
            if (_cachedSlimeVolume == null || _cachedSlimePBF == null)
                return 0;

            _cachedSlimePBF.GetVolumeParticleCounts(out int mainBody, out int separated, out int emitted);

            int storedVolume = Mathf.Max(0, _cachedSlimeVolume.CurrentVolume - mainBody);
            int occupied = mainBody + separated + emitted + storedVolume;

            return Mathf.Max(0, _cachedSlimeVolume.MaxVolume - occupied);
        }

        #endregion
    }
}
