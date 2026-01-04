using System.Collections.Generic;
using MoreMountains.Feedbacks;
using UnityEngine;

namespace Revive.Slime
{
    /// <summary>
    /// 静态工具类：自动处理 MMFeedbacks prefab 资产的运行时实例化。
    /// 调用 Play/Stop 时，如果传入的是 prefab 资产引用，会自动实例化并缓存，
    /// 然后对场景中的实例执行操作。
    /// </summary>
    public static class MMFeedbacksHelper
    {
        private static GameObject _runtimeRoot;
        private static readonly Dictionary<int, MMFeedbacks> _instanceCache = new Dictionary<int, MMFeedbacks>();

        /// <summary>
        /// 播放反馈。如果 feedbacks 是 prefab 资产，会自动实例化到场景中再播放。
        /// </summary>
        public static void Play(MMFeedbacks feedbacks)
        {
            if (feedbacks == null) return;
            var instance = GetOrCreateInstance(feedbacks);
            instance?.PlayFeedbacks();
        }

        /// <summary>
        /// 在指定位置播放反馈。
        /// </summary>
        public static void Play(MMFeedbacks feedbacks, Vector3 position, float intensity = 1f, bool forceChangeDirection = false)
        {
            if (feedbacks == null) return;
            var instance = GetOrCreateInstance(feedbacks);
            if (instance == null) return;
            instance.transform.position = position;
            instance.PlayFeedbacks(position, intensity, forceChangeDirection);
        }

        /// <summary>
        /// 停止反馈。
        /// </summary>
        public static void Stop(MMFeedbacks feedbacks, bool stopAllFeedbacks = true)
        {
            if (feedbacks == null) return;
            var instance = GetOrCreateInstance(feedbacks);
            instance?.StopFeedbacks(stopAllFeedbacks);
        }

        /// <summary>
        /// 初始化反馈。
        /// </summary>
        public static void Initialize(MMFeedbacks feedbacks, GameObject owner = null)
        {
            if (feedbacks == null) return;
            var instance = GetOrCreateInstance(feedbacks);
            if (instance == null) return;
            if (owner != null)
                instance.Initialization(owner);
            else
                instance.Initialization();
        }

        /// <summary>
        /// 检查是否仍在播放。
        /// </summary>
        public static bool IsPlaying(MMFeedbacks feedbacks)
        {
            if (feedbacks == null) return false;
            var instance = GetOrCreateInstance(feedbacks);
            return instance != null && instance.IsPlaying;
        }

        private static bool IsPrefabAsset(MMFeedbacks feedbacks)
        {
            return feedbacks != null && !feedbacks.gameObject.scene.IsValid();
        }

        private static MMFeedbacks GetOrCreateInstance(MMFeedbacks feedbacks)
        {
            if (feedbacks == null) return null;

            if (!Application.isPlaying)
                return feedbacks;

            if (!IsPrefabAsset(feedbacks))
                return feedbacks;

            int sourceId = feedbacks.GetInstanceID();
            if (_instanceCache.TryGetValue(sourceId, out var cached) && cached != null)
                return cached;

            EnsureRuntimeRoot();

            GameObject instanceGo = Object.Instantiate(feedbacks.gameObject, _runtimeRoot.transform, false);
            instanceGo.hideFlags = HideFlags.DontSave;
            instanceGo.name = feedbacks.gameObject.name + " (Runtime)";
            instanceGo.SetActive(true);

            MMFeedbacks instance = instanceGo.GetComponent<MMFeedbacks>();
            if (instance != null)
            {
                instance.AutoPlayOnEnable = false;
                instance.AutoPlayOnStart = false;
            }

            _instanceCache[sourceId] = instance;
            return instance;
        }

        private static void EnsureRuntimeRoot()
        {
            if (_runtimeRoot != null) return;
            _runtimeRoot = new GameObject("MMFeedbacksHelper_RuntimeRoot");
            _runtimeRoot.hideFlags = HideFlags.DontSave;
            Object.DontDestroyOnLoad(_runtimeRoot);
        }

        /// <summary>
        /// 清理所有缓存的运行时实例（场景切换时可调用）。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ClearCache()
        {
            _instanceCache.Clear();
            _runtimeRoot = null;
        }
    }
}
