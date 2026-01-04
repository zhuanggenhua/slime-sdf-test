using MoreMountains.Tools;
using MoreMountains.TopDownEngine;
using Revive.Slime;
using UnityEngine;

namespace Revive.Audio
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Revive/Audio/Environment Ambient Audio 3D")]
    public sealed class EnvironmentAmbientAudio3D : MonoBehaviour
    {
        [ChineseHeader("音频")]
        [ChineseLabel("音频片段")]
        public AudioClip Clip;

        [ChineseLabel("随机音频(可选)")]
        public AudioClip[] RandomClips;

        [ChineseLabel("SFX轨道")]
        public MMSoundManager.MMSoundManagerTracks Track = MMSoundManager.MMSoundManagerTracks.Sfx;

        [ChineseLabel("启用即播放")]
        [DefaultValue(true)]
        public bool PlayOnEnable = true;

        [ChineseLabel("禁用即停止")]
        [DefaultValue(true)]
        public bool StopOnDisable = true;

        [ChineseLabel("循环")]
        [DefaultValue(true)]
        public bool Loop = true;

        [ChineseLabel("音量")]
        [Range(0f, 2f), DefaultValue(1f)]
        public float Volume = 1f;

        [ChineseLabel("音高范围")]
        public Vector2 PitchRange = new Vector2(1f, 1f);

        [ChineseHeader("3D 设置")]
        [ChineseLabel("3D混合")]
        [Range(0f, 1f), DefaultValue(1f)]
        public float SpatialBlend = 1f;

        [ChineseLabel("多普勒")]
        [Range(0f, 5f), DefaultValue(1f)]
        public float DopplerLevel = 1f;

        [ChineseLabel("扩散角")]
        [Range(0, 360), DefaultValue(0f)]
        public int Spread = 0;

        [ChineseLabel("衰减模式")]
        public AudioRolloffMode RolloffMode = AudioRolloffMode.Logarithmic;

        [ChineseLabel("最小距离")]
        [Min(0.01f), DefaultValue(2f)]
        public float MinDistance = 2f;

        [ChineseLabel("最大距离")]
        [Min(0.01f), DefaultValue(25f)]
        public float MaxDistance = 25f;

        [ChineseLabel("跟随Transform")]
        [DefaultValue(true)]
        public bool FollowTransform = true;

        [ChineseHeader("实现")]
        [ChineseLabel("优先使用MMSoundManager")]
        [DefaultValue(true)]
        public bool PreferMMSoundManager = true;

        [ChineseHeader("自动播放")]
        [ChineseLabel("距离检测间隔(秒)")]
        [Min(0.02f), DefaultValue(0.2f)]
        public float DistanceCheckInterval = 0.2f;

        private AudioSource _playingSource;
        private bool _playingFromMMSoundManager;
        private Transform _playerTransform;
        private float _nextDistanceCheckTime;

        private void OnEnable()
        {
            _nextDistanceCheckTime = 0f;
            RefreshPlayerTransform();
            UpdateDistanceAutoPlayStop(force: true);
        }

        private void OnDisable()
        {
            if (StopOnDisable)
            {
                Stop();
            }
        }

        private void OnDestroy()
        {
            Stop();
        }

        private void Update()
        {
            UpdateDistanceAutoPlayStop(force: false);
        }

        public void Play()
        {
            if (_playingSource != null && _playingSource.isPlaying)
            {
                return;
            }

            AudioClip clip = PickClip();
            if (clip == null)
            {
                return;
            }

            float pitch = PickPitch();

            _playingSource = null;
            _playingFromMMSoundManager = false;

            if (PreferMMSoundManager && MMSoundManager.Instance != null)
            {
                MMSoundManagerPlayOptions options = MMSoundManagerPlayOptions.Default;
                options.MmSoundManagerTrack = Track;
                options.Location = transform.position;
                options.AttachToTransform = FollowTransform ? transform : null;
                options.Loop = Loop;
                options.Volume = Mathf.Clamp(Volume, 0f, 2f);
                options.Pitch = pitch;
                options.SpatialBlend = Mathf.Clamp01(SpatialBlend);
                options.DopplerLevel = Mathf.Clamp(DopplerLevel, 0f, 5f);
                options.Spread = Mathf.Clamp(Spread, 0, 360);
                options.RolloffMode = RolloffMode;
                options.MinDistance = Mathf.Max(0.01f, MinDistance);
                options.MaxDistance = Mathf.Max(options.MinDistance, MaxDistance);
                options.DoNotAutoRecycleIfNotDonePlaying = true;

                _playingSource = MMSoundManagerSoundPlayEvent.Trigger(clip, options);
                _playingFromMMSoundManager = _playingSource != null;
            }

            if (_playingSource == null)
            {
                _playingSource = GetComponent<AudioSource>();
                if (_playingSource == null)
                {
                    _playingSource = gameObject.AddComponent<AudioSource>();
                }

                _playingSource.playOnAwake = false;
                _playingSource.transform.position = transform.position;
                _playingSource.clip = clip;
                _playingSource.loop = Loop;
                _playingSource.volume = Mathf.Clamp(Volume, 0f, 2f);
                _playingSource.pitch = pitch;
                _playingSource.spatialBlend = Mathf.Clamp01(SpatialBlend);
                _playingSource.dopplerLevel = Mathf.Clamp(DopplerLevel, 0f, 5f);
                _playingSource.spread = Mathf.Clamp(Spread, 0, 360);
                _playingSource.rolloffMode = RolloffMode;
                _playingSource.minDistance = Mathf.Max(0.01f, MinDistance);
                _playingSource.maxDistance = Mathf.Max(_playingSource.minDistance, MaxDistance);
                _playingSource.Play();
            }
        }

        public void Stop()
        {
            if (_playingSource == null)
            {
                return;
            }

            if (_playingFromMMSoundManager && MMSoundManager.Instance != null)
            {
                MMSoundManagerSoundControlEvent.Trigger(MMSoundManagerSoundControlEventTypes.Free, 0, _playingSource);
            }
            else
            {
                _playingSource.Stop();
            }

            _playingSource = null;
            _playingFromMMSoundManager = false;
        }

        private AudioClip PickClip()
        {
            if (RandomClips != null && RandomClips.Length > 0)
            {
                int idx = Random.Range(0, RandomClips.Length);
                return RandomClips[idx];
            }

            return Clip;
        }

        private float PickPitch()
        {
            float min = PitchRange.x;
            float max = PitchRange.y;

            if (float.IsNaN(min) || float.IsInfinity(min)) min = 1f;
            if (float.IsNaN(max) || float.IsInfinity(max)) max = 1f;

            if (min > max)
            {
                float tmp = min;
                min = max;
                max = tmp;
            }

            return Random.Range(min, max);
        }

        private void UpdateDistanceAutoPlayStop(bool force)
        {
            if (!force)
            {
                if (Time.time < _nextDistanceCheckTime)
                {
                    return;
                }
            }

            _nextDistanceCheckTime = Time.time + Mathf.Max(0.02f, DistanceCheckInterval);

            if (_playerTransform == null || !_playerTransform.gameObject.activeInHierarchy)
            {
                RefreshPlayerTransform();
            }

            if (_playerTransform == null)
            {
                Stop();
                return;
            }

            float effectiveMaxDistance = Mathf.Max(MinDistance, MaxDistance);
            float dist = Vector3.Distance(transform.position, _playerTransform.position);

            if (dist <= effectiveMaxDistance)
            {
                Play();
            }
            else
            {
                Stop();
            }
        }

        private void RefreshPlayerTransform()
        {
#if UNITY_6000_0_OR_NEWER
            var controllers = Object.FindObjectsByType<TopDownController3D>(FindObjectsSortMode.None);
#else
            var controllers = Object.FindObjectsOfType<TopDownController3D>();
#endif
            foreach (var controller in controllers)
            {
                if (controller == null)
                {
                    continue;
                }

                var character = controller.GetComponent<Character>();
                if (character == null || !character.CharacterType.Equals(Character.CharacterTypes.Player))
                {
                    continue;
                }

                _playerTransform = character.transform;
                return;
            }

            _playerTransform = null;
        }

        private void OnValidate()
        {
            if (MinDistance < 0.01f) MinDistance = 0.01f;
            if (MaxDistance < MinDistance) MaxDistance = MinDistance;

            if (PitchRange.x > PitchRange.y)
            {
                PitchRange = new Vector2(PitchRange.y, PitchRange.x);
            }
        }

        private void OnDrawGizmosSelected()
        {
            DrawRangeGizmos(selected: true);
        }

        private void DrawRangeGizmos(bool selected)
        {
            float min = MinDistance;
            float max = MaxDistance;

            if (float.IsNaN(min) || float.IsInfinity(min) || min <= 0f) min = 0f;
            if (float.IsNaN(max) || float.IsInfinity(max) || max <= 0f) max = 0f;
            if (max < min)
            {
                float tmp = max;
                max = min;
                min = tmp;
            }

            if (min <= 0f && max <= 0f)
            {
                return;
            }

            Vector3 pos = transform.position;
            Color old = Gizmos.color;

            float a = selected ? 0.9f : 0.3f;

            if (max > 0f)
            {
                Gizmos.color = new Color(0.25f, 0.9f, 1f, a);
                Gizmos.DrawWireSphere(pos, max);
            }

            if (min > 0f)
            {
                Gizmos.color = new Color(1f, 0.85f, 0.2f, a);
                Gizmos.DrawWireSphere(pos, min);
            }

            Gizmos.color = old;
        }
    }
}
