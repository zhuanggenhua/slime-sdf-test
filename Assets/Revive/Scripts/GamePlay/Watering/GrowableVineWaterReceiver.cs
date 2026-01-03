using Revive.Environment;
using Revive.GamePlay.Purification;
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

        [ChineseHeader("浇水=净化")]
        [ChineseLabel("净化100%所需水量")]
        [DefaultValue(25f)]
        [SerializeField] private float waterRequiredToFullyPurify = 25f;

        [ChineseLabel("累计水量(运行时)")]
        [SerializeField] private float accumulatedWater;

        [ChineseLabel("净化指示物名称")]
        [DefaultValue("VineWater")]
        [SerializeField] private string purificationIndicatorName = "VineWater";

        private bool _meshGenerated;

        private PurificationIndicator _indicator;

        public override bool WantsWater
        {
            get
            {
                if (waterRequiredToFullyPurify <= 0f)
                    return false;

                return accumulatedWater < waterRequiredToFullyPurify;
            }
        }

        protected override void Awake()
        {
            base.Awake();
            ResolveReferencesIfNeeded();
        }

        protected override void OnDisable()
        {
            ClearIndicator();
            base.OnDisable();
        }

        private void OnDestroy()
        {
            ClearIndicator();
        }

        protected override void OnValidate()
        {
            base.OnValidate();
            ResolveReferencesIfNeeded();
        }

        public void ReceiveWater(WaterInput input)
        {
            ResolveReferencesIfNeeded();

            if (generateMeshOnFirstWater)
            {
                EnsureMeshGenerated();
            }

            accumulatedWater += Mathf.Max(0f, input.Amount);

            float required = Mathf.Max(0.0001f, waterRequiredToFullyPurify);
            float normalized = Mathf.Clamp01(accumulatedWater / required);

            var system = GetPurificationSystemChecked();
            if (system == null)
                return;

            float contribution = normalized * system.TargetPurificationValue;
            Vector3 pos = GetIndicatorPositionWorld(input.PositionWorld);
            EnsurePurificationIndicator(ref _indicator, purificationIndicatorName, pos, contribution, PurificationIndicatorType);

            system.NotifyAllListeners();
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

        private void ClearIndicator()
        {
            RemovePurificationIndicator(ref _indicator);
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
