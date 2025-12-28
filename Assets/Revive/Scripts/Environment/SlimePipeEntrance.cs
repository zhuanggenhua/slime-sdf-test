using MoreMountains.TopDownEngine;
using UnityEngine;

namespace Revive.Environment
{
    /// <summary>
    /// 管道入口触发器。接触时启动史莱姆管道移动。
    /// </summary>
    [RequireComponent(typeof(Collider))]
    [AddComponentMenu("Revive/Environment/Slime Pipe Entrance")]
    public class SlimePipeEntrance : MonoBehaviour
    {
        public enum DirectionMode
        {
            Forward,
            Reverse,
            Auto
        }

        private SlimePipePath _path;

        [Tooltip("移动方向选择。")]
        public DirectionMode TravelDirection = DirectionMode.Auto;

        [Tooltip("进入时将角色投影到 spline 上的最近点。")]
        public bool AlignToPathOnEnter = true;

        [Tooltip("朝向模式覆盖值。保持默认则使用路径设置。")]
        public TravelRotationMode RotationModeOverride = TravelRotationMode.YawOnly;

        private void Awake()
        {
            _path = GetComponentInParent<SlimePipePath>();
            Debug.Assert(_path != null, $"[SlimePipeEntrance] 未找到 SlimePipePath（请将 SlimePipeEntrance 挂在 SlimePipePath 的子层级下）: {name}", this);
            if (_path == null)
            {
                enabled = false;
                return;
            }
        }

        private void Reset()
        {
            var col = GetComponent<Collider>();
            if (col != null)
            {
                col.isTrigger = true;
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            TryStartTravel(other);
        }

        private void TryStartTravel(Collider other)
        {
            if (_path == null)
                return;

            var character = other.GetComponentInParent<Character>();
            if (character == null)
            {
                return;
            }

            var ability = character.FindAbility<Revive.Slime.SlimePipeTravelAbility>();
            if (ability == null)
            {
                return;
            }

            var pathLength = _path.GetLength();
            if (pathLength <= 0f)
            {
                return;
            }

            float startT = AlignToPathOnEnter ? _path.FindNearestT(other.transform.position) : 0f;
            bool reverse = TravelDirection == DirectionMode.Reverse;
            if (TravelDirection == DirectionMode.Auto)
            {
                var startPos = _path.EvaluatePosition(0f);
                var endPos = _path.EvaluatePosition(1f);
                float distToStart = Vector3.SqrMagnitude(other.transform.position - startPos);
                float distToEnd = Vector3.SqrMagnitude(other.transform.position - endPos);
                reverse = distToEnd < distToStart;
            }

            if (reverse && !AlignToPathOnEnter)
            {
                startT = 1f;
            }

            float speed = _path.DefaultSpeed;
            float maxSpeed = _path.MaxSpeed;
            var rotationMode = RotationModeOverride != 0 ? RotationModeOverride : _path.RotationModeDefault;

            ability.StartTravel(_path, startT, reverse, speed, maxSpeed, rotationMode);
        }
    }
}
