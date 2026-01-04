using MoreMountains.Tools;
using MoreMountains.TopDownEngine;
using UnityEngine;

namespace Revive.Slime
{
    public class SlimeVolumeTopDownHealthSync : MonoBehaviour, MMEventListener<SlimeVolumeChangeEvent>
    {
        [Tooltip("要同步的 TopDownEngine Health（留空则自动在父级查找）")]
        [SerializeField] private Health targetHealth;

        private SlimeVolume _targetVolume;
        private Character _character;

        private void OnEnable()
        {
            if (_targetVolume == null)
            {
                _targetVolume = GetComponentInParent<SlimeVolume>();
            }

            if (targetHealth == null)
            {
                targetHealth = GetComponentInParent<Health>();
            }

            if (_character == null)
            {
                _character = GetComponentInParent<Character>();
            }

            this.MMEventStartListening<SlimeVolumeChangeEvent>();
        }

        private void OnDisable()
        {
            this.MMEventStopListening<SlimeVolumeChangeEvent>();
        }

        public void OnMMEvent(SlimeVolumeChangeEvent volumeEvent)
        {
            if (_targetVolume == null)
            {
                _targetVolume = GetComponentInParent<SlimeVolume>();
            }

            if (_targetVolume == null || targetHealth == null)
            {
                return;
            }

            if (volumeEvent.AffectedVolume != _targetVolume)
            {
                return;
            }

            float max = Mathf.Max(0.0001f, volumeEvent.MaxVolume);
            float current = Mathf.Clamp(volumeEvent.CurrentVolume, 0f, max);
            current = Mathf.Max(0.0001f, current);

            targetHealth.MaximumHealth = max;
            if (targetHealth.CurrentHealth != current)
            {
                targetHealth.SetHealth(current);
            }

            if (GUIManager.HasInstance && (_character != null) && !string.IsNullOrEmpty(_character.PlayerID))
            {
                GUIManager.Instance.UpdateHealthBar(current, 0f, max, _character.PlayerID);
            }
        }
    }
}
