using MoreMountains.Feedbacks;
using Revive.Environment;
using Revive.GamePlay.Purification;
using Revive.Slime;
using UnityEngine;

namespace Revive.Environment.Watering
{
    /// <summary>
    /// 藤蔓浇水接收器 - 节点式生长
    /// 每蓄满一次水量 → 推进一个生长节点 → 生成净化指示物
    /// </summary>
    public class GrowableVineWaterReceiver : PbfChargeWaterReceiver
    {
        [ChineseHeader("藤蔓")]
        [ChineseLabel("目标藤蔓")]
        [SerializeField] private GrowableVine targetVine;

        [ChineseHeader("反馈")]
        [ChineseLabel("生长中反馈")]
        [SerializeField] private MMFeedbacks growingFeedbacks;

        [ChineseLabel("首次浇水生成网格")]
        [DefaultValue(true)]
        [SerializeField] private bool generateMeshOnFirstWater = true;

        [ChineseLabel("网格生成器(可空)")]
        [SerializeField] private ProceduralVineGenerator vineGenerator;

        [ChineseHeader("生长过渡")]
        [ChineseLabel("节点生长过渡时间(秒)")]
        [SerializeField, Min(0f), DefaultValue(1f)]
        private float nodeGrowthTransitionSeconds = 1f;

        private bool _meshGenerated;
        private Coroutine _growthTransitionRoutine;


        protected override void Awake()
        {
            base.Awake();
            ResolveReferencesIfNeeded();

            if (Completed)
            {
                if (targetVine != null)
                {
                    targetVine.SetGrowthProgress(1f);
                }
            }
        }

        protected override void OnValidate()
        {
            base.OnValidate();
            ResolveReferencesIfNeeded();
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            if (_growthTransitionRoutine != null)
            {
                StopCoroutine(_growthTransitionRoutine);
                _growthTransitionRoutine = null;
            }

            StopGrowingFeedbacks();
        }

        protected override void OnChargeUpdated(WaterInput input)
        {
            ResolveReferencesIfNeeded();

            if (generateMeshOnFirstWater)
            {
                EnsureMeshGenerated();
            }
        }

        protected override void OnRestoredByPurification(PurificationRestoreTrigger trigger, Vector3 positionWorld)
        {
            ResolveReferencesIfNeeded();
            if (targetVine != null)
            {
                SmoothSetGrowthProgress(1f);
            }
        }

        public override bool TryGetUnlockIndicatorConfig(
            PurificationRestoreTrigger trigger,
            Vector3 unlockPositionWorld,
            out string indicatorName,
            out Vector3 indicatorPositionWorld)
        {
            indicatorName = string.Empty;
            indicatorPositionWorld = GetIndicatorPositionWorld(unlockPositionWorld);
            return true;
        }

        private void SmoothSetGrowthProgress(float targetProgress)
        {
            if (targetVine == null)
                return;

            float duration = Mathf.Max(0f, nodeGrowthTransitionSeconds);
            if (duration <= 0f)
            {
                StopGrowingFeedbacks();
                targetVine.SetGrowthProgress(targetProgress);
                return;
            }

            if (_growthTransitionRoutine != null)
            {
                StopCoroutine(_growthTransitionRoutine);
                _growthTransitionRoutine = null;
            }

            StopGrowingFeedbacks();

            float from = targetVine.GetGrowthProgress();

            if (Mathf.Approximately(from, targetProgress))
            {
                targetVine.SetGrowthProgress(targetProgress);
                return;
            }

            _growthTransitionRoutine = StartCoroutine(GrowthTransitionRoutine(from, targetProgress, duration));
            PlayGrowingFeedbacks();
        }

        private System.Collections.IEnumerator GrowthTransitionRoutine(float from, float to, float duration)
        {
            float t = 0f;
            while (t < duration)
            {
                if (targetVine == null)
                {
                    _growthTransitionRoutine = null;
                    StopGrowingFeedbacks();
                    yield break;
                }

                t += Time.deltaTime;
                float u = Mathf.Clamp01(t / duration);
                float su = Mathf.SmoothStep(0f, 1f, u);
                targetVine.SetGrowthProgress(Mathf.Lerp(from, to, su));
                yield return null;
            }

            if (targetVine != null)
            {
                targetVine.SetGrowthProgress(to);
            }

            _growthTransitionRoutine = null;
            StopGrowingFeedbacks();
        }

        private void PlayGrowingFeedbacks()
        {
            if (growingFeedbacks == null)
                return;

            Vector3 pos = GetIndicatorPositionWorld(transform.position);
            MMFeedbacksHelper.Play(growingFeedbacks, pos);
        }

        private void StopGrowingFeedbacks()
        {
            MMFeedbacksHelper.Stop(growingFeedbacks);
        }

        private void ResolveReferencesIfNeeded()
        {
            if (targetVine == null)
                targetVine = GetComponent<GrowableVine>();

            if (vineGenerator == null)
                vineGenerator = GetComponent<ProceduralVineGenerator>();
        }

        private Vector3 GetIndicatorPositionWorld(Vector3 fallback)
        {
            if (targetVine != null)
                return targetVine.transform.position;

            if (transform != null)
                return transform.position;

            return fallback;
        }

        private void EnsureMeshGenerated()
        {
            if (_meshGenerated)
                return;

            if (vineGenerator == null)
                return;

            if (vineGenerator.MeshFilter != null && vineGenerator.MeshFilter.sharedMesh != null)
            {
                _meshGenerated = true;
                return;
            }

            Mesh mesh = vineGenerator.GenerateVineMesh();
            _meshGenerated = mesh != null;
        }
    }
}
