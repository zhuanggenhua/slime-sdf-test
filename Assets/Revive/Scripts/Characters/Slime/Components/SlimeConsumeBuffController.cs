using UnityEngine;

namespace Revive.Slime
{
    [DisallowMultipleComponent]
    public class SlimeConsumeBuffController : MonoBehaviour
    {
        [ChineseHeader("绑定自动获取")]
        [ChineseLabel("Slime_PBF")]
        [SerializeField] private Slime_PBF slimePbf;

        [ChineseLabel("移动能力")]
        [SerializeField] private SlimeMovementAbility movementAbility;

        [ChineseLabel("跳跃能力")]
        [SerializeField] private SlimeJumpAbility jumpAbility;

        [ChineseHeader("表现")]
        [ChineseLabel("变色过渡时长(秒)")]
        [SerializeField, Min(0f), DefaultValue(0.5f)]
        private float tintTransitionSeconds = 0.5f;

        public float CurrentThrowRangeMultiplier { get; private set; } = 1f;

        public bool WindFieldImmuneActive { get; private set; }
        public float DeformLimitMultiplierActive { get; private set; } = 1f;

        private float _buffEndTime = -1f;
        private bool _tintEnabled;
        private Color _tint = Color.white;
        private float _moveSpeedMultiplier = 1f;
        private float _jumpImpulseMultiplier = 1f;
        private int _extraJumps;

        private Color _baseSurfaceTint = Color.white;
        private bool _hasBaseSurfaceTint;
        private bool _surfaceTintApplied;
        private bool _tintTransitionActive;
        private float _tintTransitionStartTime;
        private float _tintTransitionDuration;
        private Color _tintTransitionStartColor;
        private Color _tintTransitionTargetColor;
        private bool _tintTransitionTargetEnabled;
        private Color _currentSurfaceTint;

        private float _baseMovementSpeedMultiplier = 1f;
        private float _baseJumpImpulse = 4f;
        private int _baseNumberOfJumps = 1;

        private bool _initialized;

        private void Awake()
        {
            ResolveRefsIfNeeded();
        }

        private void Start()
        {
            ResolveRefsIfNeeded();
            CacheBaseValuesIfNeeded();
        }

        private void OnDisable()
        {
            _tintTransitionActive = false;
            _surfaceTintApplied = false;
            if (slimePbf != null)
            {
                slimePbf.SetSurfaceTint(Color.white, false);
            }
            Clear();
        }

        private void Update()
        {
            if (_buffEndTime > 0f && Time.time >= _buffEndTime)
            {
                Clear();
            }

            UpdateTintTransition();
        }

        private void ResolveRefsIfNeeded()
        {
            if (slimePbf == null)
            {
                slimePbf = GetComponentInChildren<Slime_PBF>();
            }

            if (movementAbility == null)
            {
                movementAbility = GetComponentInChildren<SlimeMovementAbility>();
            }

            if (jumpAbility == null)
            {
                jumpAbility = GetComponentInChildren<SlimeJumpAbility>();
            }
        }

        private void CacheBaseValuesIfNeeded()
        {
            if (_initialized)
            {
                return;
            }

            if (movementAbility != null)
            {
                _baseMovementSpeedMultiplier = movementAbility.MovementSpeedMultiplier;
            }

            if (jumpAbility != null)
            {
                _baseJumpImpulse = jumpAbility.JumpImpulse;
                _baseNumberOfJumps = jumpAbility.NumberOfJumps;
            }

            if (slimePbf != null && slimePbf.TryGetSurfaceBaseColor(out var baseTint))
            {
                _baseSurfaceTint = baseTint;
                _hasBaseSurfaceTint = true;
            }
            else
            {
                _baseSurfaceTint = Color.white;
                _hasBaseSurfaceTint = false;
            }

            _currentSurfaceTint = _baseSurfaceTint;
            _surfaceTintApplied = false;
            _tintTransitionActive = false;
            _tintTransitionTargetEnabled = false;

            _initialized = true;
        }

        public void Apply(SlimeCarryableConsumeSpec spec)
        {
            if (spec == null)
            {
                return;
            }

            CacheBaseValuesIfNeeded();

            _buffEndTime = Time.time + Mathf.Max(0.01f, spec.DurationSeconds);

            _tintEnabled = spec.OverrideTint;
            _tint = spec.Tint;
            _moveSpeedMultiplier = Mathf.Max(0f, spec.MoveSpeedMultiplier);
            _jumpImpulseMultiplier = Mathf.Max(0f, spec.JumpImpulseMultiplier);
            _extraJumps = spec.ExtraJumps;
            CurrentThrowRangeMultiplier = Mathf.Max(0f, spec.ThrowRangeMultiplier);

            WindFieldImmuneActive = spec.WindFieldImmune;
            DeformLimitMultiplierActive = Mathf.Max(0.01f, spec.DeformLimitMultiplier);

            ApplyRuntimeValues();
        }

        public void Clear()
        {
            CacheBaseValuesIfNeeded();

            _buffEndTime = -1f;

            _tintEnabled = false;
            _tint = Color.white;
            _moveSpeedMultiplier = 1f;
            _jumpImpulseMultiplier = 1f;
            _extraJumps = 0;
            CurrentThrowRangeMultiplier = 1f;

            WindFieldImmuneActive = false;
            DeformLimitMultiplierActive = 1f;

            ApplyRuntimeValues();
        }

        private void ApplyRuntimeValues()
        {
            if (movementAbility != null)
            {
                movementAbility.MovementSpeedMultiplier = _baseMovementSpeedMultiplier * _moveSpeedMultiplier;
            }

            if (jumpAbility != null)
            {
                jumpAbility.JumpImpulse = _baseJumpImpulse * _jumpImpulseMultiplier;

                int oldMax = jumpAbility.NumberOfJumps;
                int oldLeft = jumpAbility.NumberOfJumpsLeft;

                jumpAbility.NumberOfJumps = Mathf.Max(0, _baseNumberOfJumps + _extraJumps);

                int delta = jumpAbility.NumberOfJumps - oldMax;
                int newLeft = oldLeft + delta;
                jumpAbility.NumberOfJumpsLeft = Mathf.Clamp(newLeft, 0, jumpAbility.NumberOfJumps);
            }

            ApplyTintTargetWithTransition();

            if (slimePbf != null)
            {
                slimePbf.SetConsumeWindFieldImmune(WindFieldImmuneActive);
                slimePbf.SetConsumeDeformLimitMultiplier(DeformLimitMultiplierActive);
            }
        }

        private void ApplyTintTargetWithTransition()
        {
            if (slimePbf == null)
            {
                return;
            }

            CacheBaseValuesIfNeeded();
            Color baseTint = _hasBaseSurfaceTint ? _baseSurfaceTint : Color.white;

            Color targetColor = _tintEnabled ? _tint : baseTint;
            StartTintTransition(targetColor, _tintEnabled);
        }

        private void StartTintTransition(Color targetColor, bool targetEnabled)
        {
            _tintTransitionTargetEnabled = targetEnabled;

            if (!_surfaceTintApplied && !targetEnabled)
            {
                _tintTransitionActive = false;
                slimePbf.SetSurfaceTint(Color.white, false);
                return;
            }

            float duration = Mathf.Max(0f, tintTransitionSeconds);
            if (duration <= 0f)
            {
                if (targetEnabled)
                {
                    _currentSurfaceTint = targetColor;
                    _surfaceTintApplied = true;
                    slimePbf.SetSurfaceTint(targetColor, true);
                }
                else
                {
                    _currentSurfaceTint = targetColor;
                    _surfaceTintApplied = false;
                    slimePbf.SetSurfaceTint(Color.white, false);
                }
                _tintTransitionActive = false;
                return;
            }

            _tintTransitionStartColor = _surfaceTintApplied ? _currentSurfaceTint : _baseSurfaceTint;
            _tintTransitionTargetColor = targetColor;
            _tintTransitionStartTime = Time.time;
            _tintTransitionDuration = duration;
            _tintTransitionActive = true;

            _currentSurfaceTint = _tintTransitionStartColor;
            _surfaceTintApplied = true;
            slimePbf.SetSurfaceTint(_currentSurfaceTint, true);
        }

        private void UpdateTintTransition()
        {
            if (!_tintTransitionActive)
            {
                return;
            }
            if (slimePbf == null)
            {
                _tintTransitionActive = false;
                return;
            }

            float t = _tintTransitionDuration > 1e-5f ? (Time.time - _tintTransitionStartTime) / _tintTransitionDuration : 1f;
            t = Mathf.Clamp01(t);
            float eased = t * t * (3f - 2f * t);

            _currentSurfaceTint = Color.Lerp(_tintTransitionStartColor, _tintTransitionTargetColor, eased);
            slimePbf.SetSurfaceTint(_currentSurfaceTint, true);

            if (t >= 1f)
            {
                _tintTransitionActive = false;

                if (_tintTransitionTargetEnabled)
                {
                    _surfaceTintApplied = true;
                    slimePbf.SetSurfaceTint(_tintTransitionTargetColor, true);
                }
                else
                {
                    _surfaceTintApplied = false;
                    slimePbf.SetSurfaceTint(Color.white, false);
                }
            }
        }
    }
}
