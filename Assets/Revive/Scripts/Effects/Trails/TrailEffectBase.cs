using UnityEngine;
using MoreMountains.TopDownEngine;
using MoreMountains.Tools;

namespace Revive.Effects
{
    /// <summary>
    /// 尾迹效果基类，提供共享的地面检测和生成逻辑
    /// </summary>
    public abstract class TrailEffectBase : MonoBehaviour
    {
        [Header("Spawn Settings")]
        [Tooltip("每移动多少米触发一次生成")]
        public float SpawnDistanceThreshold = 1.0f;
        
        [Header("Ground Detection")]
        [Tooltip("哪些层被视为地面")]
        public LayerMask GroundLayerMask = -1;
        
        [Tooltip("射线检测距离")]
        public float RaycastDistance = 10f;
        
        [Tooltip("射线起始点相对角色的偏移")]
        public Vector3 RaycastOriginOffset = Vector3.zero;
        
        [Header("Debug")]
        [Tooltip("是否显示调试信息")]
        public bool ShowDebugInfo = false;
        
        [Tooltip("调试时显示射线")]
        public bool ShowDebugRaycast = false;
        
        protected Character _character;
        protected TopDownController _controller;
        protected Vector3 _lastSpawnPosition;
        protected bool _initialized = false;
        
        /// <summary>
        /// 当前激活的效果数量（用于调试显示）
        /// </summary>
        public abstract int ActiveEffectCount { get; }
        
        /// <summary>
        /// 上次生成时间（用于调试显示）
        /// </summary>
        public float LastSpawnTime { get; protected set; }
        
        protected virtual void Awake()
        {
            InitializeReferences();
        }
        
        protected virtual void Start()
        {
            if (_character != null)
            {
                _lastSpawnPosition = _character.transform.position;
                LastSpawnTime = Time.time;
            }
        }
        
        protected virtual void Update()
        {
            if (!_initialized || _character == null || _controller == null)
            {
                return;
            }
            
            // 更新现有效果
            UpdateEffects();
            
            // 检查角色是否在地面上
            if (!_controller.Grounded)
            {
                return;
            }
            
            // 检查移动距离
            float distanceMoved = Vector3.Distance(_character.transform.position, _lastSpawnPosition);
            if (distanceMoved >= SpawnDistanceThreshold)
            {
                Vector3 hitPoint, hitNormal;
                if (TryGetGroundInfo(_character.transform.position, out hitPoint, out hitNormal))
                {
                    SpawnEffect(hitPoint, hitNormal);
                    _lastSpawnPosition = _character.transform.position;
                    LastSpawnTime = Time.time;
                }
            }
        }
        
        /// <summary>
        /// 初始化引用
        /// </summary>
        protected virtual void InitializeReferences()
        {
            _character = GetComponentInParent<Character>();
            if (_character != null)
            {
                _controller = _character.GetComponent<TopDownController>();
            }
            
            _initialized = (_character != null && _controller != null);
            
            if (!_initialized)
            {
                Debug.LogWarning($"[{GetType().Name}] 无法找到Character或TopDownController组件");
            }
        }
        
        /// <summary>
        /// 尝试获取地面信息
        /// </summary>
        /// <param name="origin">射线起始点</param>
        /// <param name="hitPoint">击中点</param>
        /// <param name="hitNormal">击中面的法线</param>
        /// <returns>是否成功检测到地面</returns>
        protected bool TryGetGroundInfo(Vector3 origin, out Vector3 hitPoint, out Vector3 hitNormal)
        {
            Vector3 rayOrigin = origin + RaycastOriginOffset;
            RaycastHit hit;
            
            bool didHit = Physics.Raycast(rayOrigin, Vector3.down, out hit, RaycastDistance, GroundLayerMask);
            
            if (ShowDebugRaycast)
            {
                Debug.DrawRay(rayOrigin, Vector3.down * RaycastDistance, didHit ? Color.green : Color.red, 0.1f);
            }
            
            if (didHit)
            {
                hitPoint = hit.point;
                hitNormal = hit.normal;
                return true;
            }
            
            hitPoint = Vector3.zero;
            hitNormal = Vector3.up;
            return false;
        }
        
        /// <summary>
        /// 生成效果（子类实现）
        /// </summary>
        /// <param name="position">生成位置</param>
        /// <param name="normal">地面法线</param>
        protected abstract void SpawnEffect(Vector3 position, Vector3 normal);
        
        /// <summary>
        /// 更新现有效果（子类实现）
        /// </summary>
        protected abstract void UpdateEffects();
        
        /// <summary>
        /// 绘制调试信息（子类可选实现）
        /// </summary>
        protected virtual void OnDrawGizmos()
        {
            if (!ShowDebugInfo || !Application.isPlaying)
            {
                return;
            }
            
            DrawDebugGizmos();
        }
        
        /// <summary>
        /// 子类实现具体的调试绘制
        /// </summary>
        protected abstract void DrawDebugGizmos();
    }
}

