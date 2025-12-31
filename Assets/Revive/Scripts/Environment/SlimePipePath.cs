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

        /// <summary>
        /// 碰撞忽略根节点。按规范，应为 SlimePipePath 所在节点的父节点（整个管线 Prefab 的根）。
        /// </summary>
        public Transform CollisionIgnoreRoot => transform.parent != null ? transform.parent : transform;

        private void Awake()
        {
            Container = GetComponent<SplineContainer>();
            Debug.Assert(Container != null, $"[SlimePipePath] 未找到 SplineContainer（请将其挂在同一 GameObject 上）: {name}", this);
            if (Container == null)
                enabled = false;

            if (transform.parent == null)
            {
                Debug.LogWarning($"[SlimePipePath] {name} 没有父节点，CollisionIgnoreRoot 将只忽略自身。请确保它在管线根节点下。", this);
            }
        }

        [Header("Spline")]
        [Tooltip("SplineContainer 内的 spline 索引。")]
        [SerializeField]
        private int _splineIndex = 0;

        [Header("Travel Settings")]
        [Tooltip("默认移动速度（世界单位/秒）。")]
        [SerializeField]
        private float _defaultSpeed = 4f;

        [Tooltip("是否将该路径视为闭环。")]
        [SerializeField]
        private bool _closedLoop;

        [Tooltip("沿该路径移动时的默认朝向模式。")]
        [SerializeField]
        private TravelRotationMode _rotationModeDefault = TravelRotationMode.YawOnly;

        public int SplineIndex => _splineIndex;
        public float DefaultSpeed => _defaultSpeed;
        public bool ClosedLoop => _closedLoop;
        public TravelRotationMode RotationModeDefault => _rotationModeDefault;

        public bool TryGetSpline(out Spline spline)
        {
            spline = null;
            if (Container == null)
                Container = GetComponent<SplineContainer>();
            if (Container == null)
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
