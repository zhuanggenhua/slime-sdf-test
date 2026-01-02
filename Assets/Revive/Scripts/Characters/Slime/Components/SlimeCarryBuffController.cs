using MoreMountains.TopDownEngine;
using UnityEngine;

namespace Revive.Slime
{
    [DisallowMultipleComponent]
    public class SlimeCarryBuffController : MonoBehaviour
    {
        [ChineseHeader("绑定自动获取")]
        [ChineseLabel("移动控制器")]
        [SerializeField] private TopDownController3D controller3D;

        private float _baseMaximumFallSpeed;
        private bool _baseMaximumFallSpeedCached;

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
            Clear();
        }

        private void ResolveRefsIfNeeded()
        {
            if (controller3D == null)
            {
                controller3D = GetComponentInChildren<TopDownController3D>(true);
            }
        }

        private void CacheBaseValuesIfNeeded()
        {
            if (_initialized)
            {
                return;
            }

            if (controller3D != null)
            {
                _baseMaximumFallSpeed = controller3D.MaximumFallSpeed;
                _baseMaximumFallSpeedCached = true;
            }
            else
            {
                _baseMaximumFallSpeed = 0f;
                _baseMaximumFallSpeedCached = false;
            }

            _initialized = true;
        }

        public void Apply(SlimeCarryableCarrySpec spec)
        {
            ResolveRefsIfNeeded();
            CacheBaseValuesIfNeeded();

            if (controller3D == null)
            {
                return;
            }

            if (!_baseMaximumFallSpeedCached)
            {
                _baseMaximumFallSpeed = controller3D.MaximumFallSpeed;
                _baseMaximumFallSpeedCached = true;
            }

            if (spec == null)
            {
                controller3D.MaximumFallSpeed = _baseMaximumFallSpeed;
                return;
            }

            float mul = Mathf.Max(0f, spec.MaximumFallSpeedMultiplier);
            if (mul <= 0f)
            {
                controller3D.MaximumFallSpeed = _baseMaximumFallSpeed;
                return;
            }

            float target = Mathf.Max(0.01f, _baseMaximumFallSpeed * mul);
            controller3D.MaximumFallSpeed = Mathf.Min(_baseMaximumFallSpeed, target);
        }

        public void Clear()
        {
            ResolveRefsIfNeeded();
            CacheBaseValuesIfNeeded();

            if (controller3D == null)
            {
                return;
            }

            if (_baseMaximumFallSpeedCached)
            {
                controller3D.MaximumFallSpeed = _baseMaximumFallSpeed;
            }
        }
    }
}
