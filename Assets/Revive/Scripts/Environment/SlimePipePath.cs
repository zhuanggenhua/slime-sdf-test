using UnityEngine;
using UnityEngine.Splines;

namespace Revive.Environment
{
    public enum TravelRotationMode
    {
        YawOnly,
        FollowFullTangent,
    }

    /// <summary>
    /// 定义史莱姆管道移动使用的样条路径。封装 SplineContainer 并暴露移动所需的元数据。
    /// </summary>
    [AddComponentMenu("Revive/Environment/Slime Pipe Path")]
    public class SlimePipePath : MonoBehaviour
    {
        public SplineContainer Container { get; private set; }

        [Header("References")]
        [SerializeField]
        private SplineContainer _container;

        [SerializeField]
        private Transform _collisionIgnoreRootOverride;

        public Transform CollisionIgnoreRoot => _collisionIgnoreRootOverride != null ? _collisionIgnoreRootOverride : transform;

        private bool EnsureContainer()
        {
            if (Container != null)
                return true;

            if (_container == null)
            {
                _container = GetComponent<SplineContainer>();
                if (_container == null)
                {
                    _container = GetComponentInChildren<SplineContainer>(includeInactive: true);
                }
            }

            Container = _container;
            return Container != null;
        }

        private void Awake()
        {
            bool ok = EnsureContainer();
            Debug.Assert(ok, $"[SlimePipePath] 未找到 SplineContainer（请将其挂在同一 GameObject 或其子物体上，或在 Inspector 指定 _container）: {name}", this);
            if (!ok)
                enabled = false;
        }

        [Header("Spline")]
        [Tooltip("SplineContainer 内的 spline 索引。")]
        [SerializeField]
        private int _splineIndex = 0;

        [Header("Travel Settings")]
        [Tooltip("默认移动速度（世界单位/秒）。")]
        [SerializeField]
        private float _defaultSpeed = 4f;

        [SerializeField]
        private float _speedRampSeconds = 0.5f;

        [Tooltip("是否将该路径视为闭环。")]
        [SerializeField]
        private bool _closedLoop;

        [Tooltip("沿该路径移动时的默认朝向模式。")]
        [SerializeField]
        private TravelRotationMode _rotationModeDefault = TravelRotationMode.YawOnly;

        public int SplineIndex => _splineIndex;
        public float DefaultSpeed => _defaultSpeed;
        public float SpeedRampSeconds => _speedRampSeconds;
        public bool ClosedLoop => _closedLoop;
        public TravelRotationMode RotationModeDefault => _rotationModeDefault;

        public bool TryGetSpline(out Spline spline)
        {
            spline = null;
            if (!EnsureContainer())
                return false;

            var splines = Container.Splines;
            if (splines == null || splines.Count == 0)
                return false;

            var idx = Mathf.Clamp(_splineIndex, 0, splines.Count - 1);
            spline = splines[idx];
            return spline != null;
        }

        private void OnDrawGizmosSelected()
        {
            if (!TryGetSpline(out _))
                return;

            Vector3 p0 = EvaluatePosition(0f);
            Vector3 p1 = EvaluatePosition(1f);
            Vector3 t0 = EvaluateTangent(0f);
            Vector3 t1 = EvaluateTangent(1f);

            Gizmos.color = Color.cyan;
            Gizmos.DrawSphere(p0, 0.2f);
            Gizmos.DrawSphere(p1, 0.2f);

            if (t0.sqrMagnitude > 1e-6f)
                Gizmos.DrawRay(p0, t0.normalized * 1.0f);

            if (t1.sqrMagnitude > 1e-6f)
                Gizmos.DrawRay(p1, t1.normalized * 1.0f);
        }

        public float GetLength()
        {
            if (!TryGetSpline(out _))
                return 0f;
            return Container.CalculateLength(Mathf.Clamp(_splineIndex, 0, Container.Splines.Count - 1));
        }

        public Vector3 EvaluatePosition(float t)
        {
            if (!TryGetSpline(out _))
                return transform.position;
            return Container.EvaluatePosition(Mathf.Clamp(_splineIndex, 0, Container.Splines.Count - 1), t);
        }

        public Vector3 EvaluateTangent(float t)
        {
            if (!TryGetSpline(out _))
                return Vector3.forward;
            return Container.EvaluateTangent(Mathf.Clamp(_splineIndex, 0, Container.Splines.Count - 1), t);
        }

        public Vector3 EvaluateUp(float t)
        {
            if (!TryGetSpline(out _))
                return Vector3.up;
            return Container.EvaluateUpVector(Mathf.Clamp(_splineIndex, 0, Container.Splines.Count - 1), t);
        }

        /// <summary>
        /// 通过采样在 spline 上近似寻找最近点。实现简单且足够稳健，用于入口触发对齐。
        /// </summary>
        public float FindNearestT(Vector3 worldPos, int samples = 32)
        {
            if (!TryGetSpline(out _))
                return 0f;

            samples = Mathf.Max(4, samples);
            float bestT = 0f;
            float bestDist = float.MaxValue;
            for (int i = 0; i <= samples; i++)
            {
                float t = i / (float)samples;
                var p = EvaluatePosition(t);
                float d = (p - worldPos).sqrMagnitude;
                if (d < bestDist)
                {
                    bestDist = d;
                    bestT = t;
                }
            }
            return bestT;
        }
    }
}
