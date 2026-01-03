using Revive.Environment;
using Revive.Slime;
using UnityEngine;

namespace Revive.Environment.Watering
{
    public class GrowableVineWaterReceiver : PbfWaterReceiver, IPbfWaterTarget
    {
        [ChineseHeader("藤蔓")]
        [ChineseLabel("目标藤蔓")]
        [SerializeField] private GrowableVine targetVine;

        [ChineseLabel("首次浇水生成网格")]
        [DefaultValue(true)]
        [SerializeField] private bool generateMeshOnFirstWater = true;

        [ChineseLabel("网格生成器(可空)")]
        [SerializeField] private ProceduralVineGenerator vineGenerator;

        [ChineseHeader("浇水→生长")]
        [ChineseLabel("完全生长所需水量")]
        [DefaultValue(25f)]
        [SerializeField] private float waterRequiredToFullyGrow = 25f;

        [ChineseLabel("累计水量(运行时)")]
        [SerializeField] private float accumulatedWater;

        [ChineseLabel("使用藤蔓曲线")]
        [DefaultValue(true)]
        [SerializeField] private bool applyVineGrowthCurve = true;

        private bool _meshGenerated;

        public override bool WantsWater
        {
            get
            {
                GrowableVine vine = targetVine != null ? targetVine : GetComponent<GrowableVine>();
                if (vine == null)
                    return true;
                return vine.GetGrowthProgress() < 1f;
            }
        }

        protected override void Awake()
        {
            base.Awake();
            ResolveReferencesIfNeeded();
        }

        protected override void OnValidate()
        {
            base.OnValidate();
            ResolveReferencesIfNeeded();
        }

        public void ReceiveWater(WaterInput input)
        {
            ResolveReferencesIfNeeded();

            if (targetVine == null)
                return;

            if (generateMeshOnFirstWater)
            {
                EnsureMeshGenerated();
            }

            accumulatedWater += Mathf.Max(0f, input.Amount);

            float progress;
            if (waterRequiredToFullyGrow <= 0f)
            {
                progress = 1f;
            }
            else
            {
                progress = Mathf.Clamp01(accumulatedWater / waterRequiredToFullyGrow);
            }

            targetVine.SetGrowthProgress(progress, applyVineGrowthCurve);
        }

        private void ResolveReferencesIfNeeded()
        {
            if (targetVine == null)
                targetVine = GetComponent<GrowableVine>();

            if (vineGenerator == null)
                vineGenerator = GetComponent<ProceduralVineGenerator>();
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
