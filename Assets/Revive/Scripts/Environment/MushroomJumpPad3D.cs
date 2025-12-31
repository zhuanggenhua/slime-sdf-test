using System;
using System.Collections;
using UnityEngine;
using MoreMountains.TopDownEngine;
using MoreMountains.Feedbacks;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Revive.Environment
{
    /// <summary>
    /// 弹跳平台 - 实体平台+顶部Trigger检测
    /// </summary>
    [AddComponentMenu("Revive/Environment/Mushroom Jump Pad 3D")]
    public class MushroomJumpPad3D : MonoBehaviour
    {
        public event Action<MushroomJumpPad3D, TopDownController3D> Bounced;
        public bool OneShot { get; set; }
        public float OneShotDestroyDelay => _sustainDuration;

        [Header("弹跳参数")]
        [Tooltip("弹跳速度（和原版跳跃保持一致，默认4）")]
        [SerializeField] private float _jumpSpeed = 4f;
        
        [Tooltip("保持最大速度的时间（秒）")]
        [SerializeField] private float _sustainDuration = 1f;

        [Tooltip("上升到最大速度所需时间（秒），0=立即达到最大速度")]
        [SerializeField] private float _rampUpDuration = 1f;

        [Tooltip("起步上升速度（避免刚脱离地面时速度过低导致重新接地）")]
        [SerializeField] private float _startJumpSpeed = 0.5f;
        
        [Header("反馈")]
        [Tooltip("弹跳时播放的反馈")]
        [SerializeField] private MMFeedbacks _bounceFeedback;

        [Tooltip("用于做平台缩放反馈的目标Transform（建议指向可视模型，不要指向碰撞体）")]
        [SerializeField] private Transform _platformFeedbackTarget;

        [Tooltip("下压时Y缩放倍率（1=不缩放）")]
        [SerializeField] private float _platformPressYScale = 0.85f;

        [Tooltip("下压时间（秒）")]
        [SerializeField] private float _platformPressDuration = 0.06f;

        [Tooltip("回弹时间（秒）")]
        [SerializeField] private float _platformReleaseDuration = 0.10f;

        [Tooltip("缩放插值曲线")]
        [SerializeField] private AnimationCurve _platformScaleCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
        
        [Header("设置")]
        [Tooltip("冷却时间（防止连续触发）")]
        [SerializeField] private float _cooldown = 0.5f;

        [Tooltip("实体平台使用的主Collider（建议指定为模型/碰撞体子物体上的非Trigger Collider）")]
        [SerializeField] private Collider _platformCollider;

        [Tooltip("触发区向上覆盖高度（米）")]
        [SerializeField] private float _triggerHeightAbove = 0.6f;
        
        [Tooltip("触发区向下侵入高度（米），保证落地/贴地时仍在触发区内")]
        [SerializeField] private float _triggerOverlapDown = 0.1f;

        [SerializeField] private bool _debugLogs = true;
        [SerializeField] private bool _debugSceneLabel = true;
        
        private float _lastBounceTime = -1000f;
        
        // 持续上升状态
        private TopDownController3D _activeController;
        private float _sustainEndTime;
        private float _sustainStartTime;
        
        private TopDownController3D _armedController;
        private float _armedUntilTime;

        private TopDownController3D _lastControllerInTrigger;
        private float _lastControllerInTriggerTime;

        private Coroutine _platformScaleRoutine;
        private Vector3 _platformBaseScale;

        private Collider _resolvedPlatformCollider;
        private Transform _triggerZoneTransform;
        private bool _oneShotDestroyQueued;
        private Rigidbody _createdRigidbody;
        
        private void Awake()
        {
            SetupColliders();
            _bounceFeedback?.Initialization(this.gameObject);
            ResolvePlatformColliderIfNeeded();
            ResolvePlatformFeedbackTargetIfNeeded();
            if (_platformFeedbackTarget != null)
            {
                _platformBaseScale = _platformFeedbackTarget.localScale;
            }
        }
        
        private void Update()
        {
            TickSustain(isFixedTick: false);
        }
        
        private void FixedUpdate()
        {
            TickSustain(isFixedTick: true);
        }
        
        private void TickSustain(bool isFixedTick)
        {
            if (_activeController == null)
            {
                return;
            }
            
            bool controllerFixed = (_activeController.UpdateMode == TopDownController3D.UpdateModes.FixedUpdate);
            if (controllerFixed != isFixedTick)
            {
                return;
            }
            
            if (Time.time < _sustainEndTime)
            {
                float rampT = 1f;
                if (_rampUpDuration > 0f)
                {
                    rampT = Mathf.Clamp01((Time.time - _sustainStartTime) / _rampUpDuration);
                }

                float startSpeed = Mathf.Max(0.01f, Mathf.Min(_startJumpSpeed, _jumpSpeed));
                float targetVy = Mathf.SmoothStep(startSpeed, _jumpSpeed, rampT);
                _activeController.AddedForce = new Vector3(0f, targetVy, 0f);
                _activeController.Grounded = false;
            }
            else
            {
                _activeController.AddedForce = Vector3.zero;
                _activeController.GravityActive = true;
                _activeController = null;
            }
        }
        
        private void SetupColliders()
        {
            ResolvePlatformColliderIfNeeded();
            if (_resolvedPlatformCollider == null)
            {
                Debug.LogError($"[MushroomJumpPad3D] {gameObject.name} 需要一个 Collider！");
                return;
            }

            Collider mainCollider = _resolvedPlatformCollider;
            
            // 确保主碰撞体是实体
            mainCollider.isTrigger = false;
            
            // 创建顶部检测区域（子物体）
            Transform parent = mainCollider.transform != null ? mainCollider.transform : transform;
            GameObject triggerZone = parent.Find("TriggerZone")?.gameObject;
            if (triggerZone == null)
            {
                triggerZone = new GameObject("TriggerZone");
                triggerZone.transform.SetParent(parent);
                triggerZone.transform.localPosition = Vector3.zero;
                triggerZone.transform.localRotation = Quaternion.identity;
                triggerZone.transform.localScale = Vector3.one;
                triggerZone.layer = parent.gameObject.layer;
            }
            else
            {
                triggerZone.layer = parent.gameObject.layer;
            }

            _triggerZoneTransform = triggerZone.transform;
            
            Collider[] existing = triggerZone.GetComponents<Collider>();
            for (int i = 0; i < existing.Length; i++)
            {
                existing[i].enabled = false;
                if (Application.isPlaying)
                {
                    Destroy(existing[i]);
                }
                else
                {
                    DestroyImmediate(existing[i]);
                }
            }
            
            float heightAbove = Mathf.Max(0.05f, _triggerHeightAbove);
            float overlapDown = Mathf.Max(0f, _triggerOverlapDown);
            float totalHeight = heightAbove + overlapDown;
            
            Bounds b = mainCollider.bounds;
            Vector3 lossy = parent.lossyScale;
            float sx = Mathf.Max(0.0001f, Mathf.Abs(lossy.x));
            float sy = Mathf.Max(0.0001f, Mathf.Abs(lossy.y));
            float sz = Mathf.Max(0.0001f, Mathf.Abs(lossy.z));
            
            float centerY = b.max.y + (heightAbove - overlapDown) * 0.5f;
            Vector3 worldCenter = new Vector3(b.center.x, centerY, b.center.z);
            Vector3 localCenter = parent.InverseTransformPoint(worldCenter);
            Vector3 localSize = new Vector3(b.size.x / sx, totalHeight / sy, b.size.z / sz);
            
            BoxCollider trigger = triggerZone.AddComponent<BoxCollider>();
            trigger.center = localCenter;
            trigger.size = localSize;
            trigger.isTrigger = true;
            
            // 添加检测脚本到子物体
            JumpPadTrigger jpTrigger = triggerZone.GetComponent<JumpPadTrigger>();
            if (jpTrigger == null)
            {
                jpTrigger = triggerZone.AddComponent<JumpPadTrigger>();
            }
            jpTrigger.jumpPad = this;
        }
        
        private void Start()
        {
            // 确保有Rigidbody（Trigger检测需要）
            Rigidbody rb = GetComponent<Rigidbody>();
            if (rb == null)
            {
                rb = gameObject.AddComponent<Rigidbody>();
                rb.isKinematic = true;
                rb.useGravity = false;
                _createdRigidbody = rb;
            }
        }
        
        private void OnDestroy()
        {
            if (_triggerZoneTransform != null)
            {
                if (Application.isPlaying)
                {
                    Destroy(_triggerZoneTransform.gameObject);
                }
                else
                {
                    DestroyImmediate(_triggerZoneTransform.gameObject);
                }
                _triggerZoneTransform = null;
            }

            if (_createdRigidbody != null)
            {
                if (Application.isPlaying)
                {
                    Destroy(_createdRigidbody);
                }
                else
                {
                    DestroyImmediate(_createdRigidbody);
                }
                _createdRigidbody = null;
            }
        }
        
        // 公开方法供子物体调用
        public void OnPlayerEnterTrigger(Collider other)
        {
            OnTriggerCheck(other);
        }

        public void OnPlayerExitTrigger(Collider other)
        {
            TopDownController3D controller = other.GetComponent<TopDownController3D>();
            if (controller == null)
            {
                controller = other.GetComponentInParent<TopDownController3D>();
            }

            if (controller != null && _armedController == controller)
            {
                _armedController = null;
                _armedUntilTime = 0f;
            }
        }
        
        private void OnTriggerCheck(Collider other)
        {
            // 检查是否是玩家
            TopDownController3D controller = other.GetComponent<TopDownController3D>();
            if (controller == null)
            {
                controller = other.GetComponentInParent<TopDownController3D>();
            }
            
            if (controller == null) 
            {
                return;
            }

            _lastControllerInTrigger = controller;
            _lastControllerInTriggerTime = Time.time;
            
            // 冷却检查
            float timeSinceLast = Time.time - _lastBounceTime;
            bool cooldownReady = (timeSinceLast >= _cooldown);
            
            if (_armedController != null && Time.time > _armedUntilTime)
            {
                _armedController = null;
                _armedUntilTime = 0f;
            }

            float gravityStep = controller.Gravity * Time.deltaTime;
            float armThreshold = Mathf.Max(0.05f, gravityStep + 0.05f);
            bool airborne = !controller.Grounded;
            bool fallingFast = airborne && ((controller.Velocity.y < -armThreshold) || (controller.VelocityLastFrame.y < -armThreshold));
            
            if (fallingFast)
            {
                bool justArmed = (_armedController != controller);
                _armedController = controller;
                _armedUntilTime = Time.time + 1.0f;

                if (_debugLogs && justArmed)
                {
                }
            }

            bool armed = (_armedController == controller) && (Time.time <= _armedUntilTime);
            bool landed = controller.Grounded
                          || controller.JustGotGrounded
                          || (controller.VelocityLastFrame.y < -armThreshold && controller.Velocity.y > -0.2f);

            if (cooldownReady && armed && landed)
            {
                _armedController = null;
                _armedUntilTime = 0f;

                if (_debugLogs)
                {
                }

                PerformBounce(controller);
                return;
            }
        }
        
        private void PerformBounce(TopDownController3D controller)
        {
            _lastBounceTime = Time.time;
            
            // 设置持续上升状态（由Update处理）
            _activeController = controller;
            _sustainEndTime = Time.time + _sustainDuration;
            _sustainStartTime = Time.time;
            
            // 【关键】使用TopDown官方的DetachFromGround方法
            // 这样只有当Velocity.y <= 0时才会重新接地
            controller.DetachFromGround();
            controller.Grounded = false;
            controller.GravityActive = false;
            
            // 初始速度
            float startSpeed = Mathf.Max(0.01f, Mathf.Min(_startJumpSpeed, _jumpSpeed));
            controller.AddedForce = new Vector3(0f, startSpeed, 0f);
            
            _bounceFeedback?.PlayFeedbacks(transform.position, _jumpSpeed);
            PlayPlatformScaleFeedback();

            Bounced?.Invoke(this, controller);
            if (OneShot && !_oneShotDestroyQueued)
            {
                _oneShotDestroyQueued = true;
                StartCoroutine(OneShotDestroyRoutine());
            }
        }

        private IEnumerator OneShotDestroyRoutine()
        {
            float delay = Mathf.Max(0f, OneShotDestroyDelay);
            if (delay > 0f)
            {
                yield return new WaitForSeconds(delay);
            }

            if (_triggerZoneTransform != null)
            {
                Collider c = _triggerZoneTransform.GetComponent<Collider>();
                if (c != null)
                    c.enabled = false;

                JumpPadTrigger t = _triggerZoneTransform.GetComponent<JumpPadTrigger>();
                if (t != null)
                    t.enabled = false;

                if (Application.isPlaying)
                {
                    Destroy(_triggerZoneTransform.gameObject);
                }
                else
                {
                    DestroyImmediate(_triggerZoneTransform.gameObject);
                }
                _triggerZoneTransform = null;
            }

            Destroy(this);
        }

        private void ResolvePlatformFeedbackTargetIfNeeded()
        {
            if (_platformFeedbackTarget != null)
            {
                return;
            }

            ResolvePlatformColliderIfNeeded();
            if (_resolvedPlatformCollider != null && _resolvedPlatformCollider.transform != null)
            {
                _platformFeedbackTarget = _resolvedPlatformCollider.transform;
                return;
            }

            Renderer[] renderers = GetComponentsInChildren<Renderer>(includeInactive: true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Transform t = renderers[i].transform;
                if (t == transform)
                {
                    continue;
                }

                if (t.name == "TriggerZone")
                {
                    continue;
                }

                _platformFeedbackTarget = t;
                return;
            }
        }

        private void ResolvePlatformColliderIfNeeded()
        {
            if (_resolvedPlatformCollider != null)
            {
                return;
            }

            if (_platformCollider != null)
            {
                _resolvedPlatformCollider = _platformCollider;
                return;
            }

            Collider[] cols = GetComponentsInChildren<Collider>(includeInactive: true);
            for (int i = 0; i < cols.Length; i++)
            {
                Collider c = cols[i];
                if (c == null)
                {
                    continue;
                }

                if (c.isTrigger)
                {
                    continue;
                }

                if (c.transform != null && c.transform.name == "TriggerZone")
                {
                    continue;
                }

                _resolvedPlatformCollider = c;
                return;
            }
        }

        private void PlayPlatformScaleFeedback()
        {
            ResolvePlatformFeedbackTargetIfNeeded();
            if (_platformFeedbackTarget == null)
            {
                return;
            }

            if (_platformScaleRoutine != null)
            {
                StopCoroutine(_platformScaleRoutine);
                _platformScaleRoutine = null;
                if (_platformBaseScale == Vector3.zero)
                {
                    _platformBaseScale = _platformFeedbackTarget.localScale;
                }
                _platformFeedbackTarget.localScale = _platformBaseScale;
            }

            _platformScaleRoutine = StartCoroutine(PlatformScaleRoutine());
        }

        public void RefreshPlatformFeedbackBaseScale()
        {
            ResolvePlatformFeedbackTargetIfNeeded();
            if (_platformFeedbackTarget == null)
                return;

            _platformBaseScale = _platformFeedbackTarget.localScale;
        }

        private IEnumerator PlatformScaleRoutine()
        {
            if (_platformFeedbackTarget == null)
            {
                yield break;
            }

            if (_platformBaseScale == Vector3.zero)
            {
                _platformBaseScale = _platformFeedbackTarget.localScale;
            }

            float pressY = Mathf.Max(0.01f, _platformPressYScale);
            float pressDur = Mathf.Max(0.001f, _platformPressDuration);
            float releaseDur = Mathf.Max(0.001f, _platformReleaseDuration);

            Vector3 from = _platformBaseScale;
            Vector3 to = new Vector3(_platformBaseScale.x, _platformBaseScale.y * pressY, _platformBaseScale.z);

            yield return LerpLocalScale(from, to, pressDur);
            yield return LerpLocalScale(to, from, releaseDur);

            if (_platformFeedbackTarget != null)
            {
                _platformFeedbackTarget.localScale = _platformBaseScale;
            }

            _platformScaleRoutine = null;
        }

        private IEnumerator LerpLocalScale(Vector3 from, Vector3 to, float duration)
        {
            float t = 0f;
            while (t < duration)
            {
                t += Time.deltaTime;
                float u = duration <= 0f ? 1f : Mathf.Clamp01(t / duration);
                float cu = _platformScaleCurve != null ? _platformScaleCurve.Evaluate(u) : u;
                if (_platformFeedbackTarget != null)
                {
                    _platformFeedbackTarget.localScale = Vector3.LerpUnclamped(from, to, cu);
                }
                yield return null;
            }

            if (_platformFeedbackTarget != null)
            {
                _platformFeedbackTarget.localScale = to;
            }
        }
        
        
        private void OnDrawGizmosSelected()
        {
            // 可视化触发区域
            Collider col = _resolvedPlatformCollider != null ? _resolvedPlatformCollider : GetComponent<Collider>();
            if (col == null)
            {
                col = GetComponentInChildren<Collider>();
            }

            if (col != null)
            {
                Gizmos.color = new Color(0f, 1f, 0f, 0.3f);
                if (col is BoxCollider box)
                {
                    Matrix4x4 oldMatrix = Gizmos.matrix;
                    Gizmos.matrix = box.transform.localToWorldMatrix;
                    Gizmos.DrawCube(box.center, box.size);
                    Gizmos.color = Color.green;
                    Gizmos.DrawWireCube(box.center, box.size);
                    Gizmos.matrix = oldMatrix;
                }
                else if (col is SphereCollider sphere)
                {
                    Vector3 s = sphere.transform.lossyScale;
                    float maxScale = Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
                    Vector3 centerW = sphere.transform.TransformPoint(sphere.center);
                    Gizmos.DrawSphere(centerW, sphere.radius * maxScale);
                    Gizmos.color = Color.green;
                    Gizmos.DrawWireSphere(centerW, sphere.radius * maxScale);
                }
                
            }

#if UNITY_EDITOR
            if (!_debugSceneLabel)
            {
                return;
            }

            TopDownController3D c = _lastControllerInTrigger;
            if (c == null)
            {
                return;
            }

            if (Application.isPlaying && (Time.time - _lastControllerInTriggerTime > 2f))
            {
                return;
            }

            Vector3 labelPos = transform.position + Vector3.up * 1.0f;

            float timeSinceLast = Application.isPlaying ? (Time.time - _lastBounceTime) : 0f;
            bool cooldownReady = Application.isPlaying ? (timeSinceLast >= _cooldown) : false;
            bool armed = (_armedController == c) && (Application.isPlaying ? (Time.time <= _armedUntilTime) : false);
            bool active = (_activeController == c);

            string text =
                $"JumpPad\n" +
                $"vY={c.Velocity.y:F2}  lastY={c.VelocityLastFrame.y:F2}\n" +
                $"vXZ={new Vector2(c.Velocity.x, c.Velocity.z).magnitude:F2}\n" +
                $"grounded={c.Grounded}  justGrounded={c.JustGotGrounded}\n" +
                $"gravityActive={c.GravityActive}  mode={c.UpdateMode}\n" +
                $"armed={armed}  active={active}\n" +
                $"cooldownReady={cooldownReady}  tSinceLast={timeSinceLast:F2}";

            Handles.color = Color.white;
            Handles.Label(labelPos, text);
#endif
        }
    }
}
