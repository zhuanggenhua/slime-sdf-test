using Revive.Slime;
using System.Collections;
using System.Collections.Generic;
using Revive.GamePlay.Purification;
using UnityEngine;

namespace Revive.Environment.Watering
{
    /// <summary>
    /// 单脚本版本：挂在目标物体上即可（不依赖 Trigger/Collider）。
    /// 体积使用基类 PbfWaterReceiver 的 Bounds 配置。
    /// 这个脚本同时扮演：
    /// - Receiver：提供接收体积与消耗策略
    /// - Target：接收 WaterInput 并驱动树的 charge/stage 与缩放
    ///
    /// 适用场景：
    /// - 你只想做“树浇水长大”且希望 Prefab/场景配置尽量少。
    ///
    /// 如果后续同一个接收体需要驱动多个目标/效果，或你想复用到不同玩法对象，
    /// 则更推荐用 PbfWaterReceiver + 自己的 IPbfWaterTarget（两脚本解耦）。
    /// </summary>
    public class TreeGrowthWaterReceiver : PbfChargeWaterReceiver
    {
        [ChineseHeader("树")]
        [ChineseLabel("自身模型根(浇水后显示)")]
        [SerializeField] private Transform selfModelRoot;

        [ChineseLabel("出生模型预制体")]
        [SerializeField] private GameObject bornModelPrefab;

        [ChineseLabel("出生模型挂载点(可空)")]
        [SerializeField] private Transform bornModelHost;

        [ChineseHeader("过渡")]
        [ChineseLabel("溶解材质")]
        [SerializeField] private Material dissolveMaterial;

        [ChineseLabel("溶解时长(秒)")]
        [Min(0f), DefaultValue(0.45f)]
        [SerializeField] private float dissolveSeconds = 0.45f;

        [ChineseHeader("净化")]
        [ChineseLabel("指示物名称前缀")]
        [DefaultValue("TreeStage")]
        [SerializeField] private string purificationIndicatorNamePrefix = "TreeStage";

        private GameObject _bornModelInstance;
        private int _purificationIndicatorCounter;

        private Coroutine _transitionCoroutine;
        private Material _runtimeDissolveMaterial;
        private List<RendererMaterials> _transitionSelfBackup;
        private List<RendererMaterials> _transitionBornBackup;
        private List<Material> _transitionCreatedMaterials;
        private static readonly int _dissolveId = Shader.PropertyToID("_Dissolve");
        private static readonly int _dissolveInvertId = Shader.PropertyToID("_DissolveInvert");
        private static readonly int _dissolveMinYId = Shader.PropertyToID("_DissolveMinY");
        private static readonly int _dissolveMaxYId = Shader.PropertyToID("_DissolveMaxY");
        private static readonly int _baseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int _baseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int _mainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int _colorId = Shader.PropertyToID("_Color");
        private static readonly int _cutoffId = Shader.PropertyToID("_Cutoff");
        private static readonly int _metallicId = Shader.PropertyToID("_Metallic");
        private static readonly int _smoothnessId = Shader.PropertyToID("_Smoothness");

        protected override void Awake()
        {
            if (!Completed)
            {
                SetSelfModelRenderersVisible(false);
            }
            base.Awake();
            ApplyModelByStage();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            ApplyModelByStage();
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            StopTransitionIfAny();
            if (_bornModelInstance != null)
            {
                Destroy(_bornModelInstance);
                _bornModelInstance = null;
            }
        }

        private void OnDestroy()
        {
            StopTransitionIfAny();
            if (_runtimeDissolveMaterial != null)
            {
                Destroy(_runtimeDissolveMaterial);
                _runtimeDissolveMaterial = null;
            }
        }

        protected override void OnRestoredByPurification(PurificationRestoreTrigger trigger, Vector3 positionWorld)
        {
            StartTransitionToCompleted();
        }

        private void ApplyModelByStage()
        {
            if (Completed)
            {
                SetSelfModelRenderersVisible(true);

                if (_bornModelInstance != null)
                {
                    Destroy(_bornModelInstance);
                    _bornModelInstance = null;
                }

                return;
            }

            SetSelfModelRenderersVisible(false);

            if (_bornModelInstance == null && bornModelPrefab != null)
            {
                Transform host = bornModelHost != null ? bornModelHost : transform;
                _bornModelInstance = Instantiate(bornModelPrefab, host, false);
            }
        }

        private void StartTransitionToCompleted()
        {
            StopTransitionIfAny();

            if (dissolveSeconds <= 0f)
            {
                ApplyModelByStage();
                return;
            }

            var mat = ResolveDissolveMaterial();
            if (mat == null)
            {
                ApplyModelByStage();
                return;
            }

            _transitionCoroutine = StartCoroutine(TransitionToCompletedRoutine(mat, dissolveSeconds));
        }

        private void StopTransitionIfAny()
        {
            if (_transitionCoroutine != null)
            {
                StopCoroutine(_transitionCoroutine);
                _transitionCoroutine = null;
            }

            RestoreTransitionStateIfAny();

            if (Completed)
            {
                SetSelfModelRenderersVisible(true);
            }
            else
            {
                SetSelfModelRenderersVisible(false);
            }
        }

        private void RestoreTransitionStateIfAny()
        {
            if (_transitionSelfBackup != null)
            {
                RestoreSharedMaterials(_transitionSelfBackup);
                ClearPropertyBlocksFromBackup(_transitionSelfBackup);
                _transitionSelfBackup = null;
            }

            if (_transitionBornBackup != null)
            {
                RestoreSharedMaterials(_transitionBornBackup);
                ClearPropertyBlocksFromBackup(_transitionBornBackup);
                _transitionBornBackup = null;
            }

            if (_transitionCreatedMaterials != null)
            {
                for (int i = 0; i < _transitionCreatedMaterials.Count; i++)
                {
                    var m = _transitionCreatedMaterials[i];
                    if (m != null)
                    {
                        Destroy(m);
                    }
                }
                _transitionCreatedMaterials = null;
            }
        }

        private Material ResolveDissolveMaterial()
        {
            if (dissolveMaterial != null)
                return dissolveMaterial;

            if (_runtimeDissolveMaterial != null)
                return _runtimeDissolveMaterial;

            var shader = Shader.Find("Revive/ConsumeDissolveVerticalLit");
            if (shader == null)
            {
                shader = Shader.Find("Revive/ConsumeDissolveVertical");
            }
            if (shader == null)
                return null;

            _runtimeDissolveMaterial = new Material(shader);
            _runtimeDissolveMaterial.hideFlags = HideFlags.DontSave;
            return _runtimeDissolveMaterial;
        }

        private IEnumerator TransitionToCompletedRoutine(Material mat, float seconds)
        {
            var selfRenderers = CollectSelfRenderers();
            var bornRenderers = CollectBornRenderers();

            ComputeBoundsY(selfRenderers, out float selfMinY, out float selfMaxY);
            ComputeBoundsY(bornRenderers, out float bornMinY, out float bornMaxY);

            var selfBackup = BackupSharedMaterials(selfRenderers);
            var bornBackup = BackupSharedMaterials(bornRenderers);

            _transitionSelfBackup = selfBackup;
            _transitionBornBackup = bornBackup;
            _transitionCreatedMaterials = new List<Material>();

            SwapAllMaterialsWithClones(selfBackup, mat, _transitionCreatedMaterials);
            SwapAllMaterialsWithClones(bornBackup, mat, _transitionCreatedMaterials);

            SetRenderersVisible(selfRenderers, true);

            var selfBlock = new MaterialPropertyBlock();
            var bornBlock = new MaterialPropertyBlock();

            selfBlock.SetFloat(_dissolveId, 1f);
            selfBlock.SetFloat(_dissolveInvertId, 1f);
            selfBlock.SetFloat(_dissolveMinYId, selfMinY);
            selfBlock.SetFloat(_dissolveMaxYId, selfMaxY);

            bornBlock.SetFloat(_dissolveId, 0f);
            bornBlock.SetFloat(_dissolveInvertId, 0f);
            bornBlock.SetFloat(_dissolveMinYId, bornMinY);
            bornBlock.SetFloat(_dissolveMaxYId, bornMaxY);

            ApplyBlockToRenderers(selfRenderers, selfBlock);
            ApplyBlockToRenderers(bornRenderers, bornBlock);

            float elapsed = 0f;
            while (elapsed < seconds)
            {
                float t = Mathf.Clamp01(elapsed / seconds);

                selfBlock.SetFloat(_dissolveId, 1f - t);
                selfBlock.SetFloat(_dissolveInvertId, 1f);
                selfBlock.SetFloat(_dissolveMinYId, selfMinY);
                selfBlock.SetFloat(_dissolveMaxYId, selfMaxY);

                bornBlock.SetFloat(_dissolveId, t);
                bornBlock.SetFloat(_dissolveInvertId, 0f);
                bornBlock.SetFloat(_dissolveMinYId, bornMinY);
                bornBlock.SetFloat(_dissolveMaxYId, bornMaxY);

                ApplyBlockToRenderers(selfRenderers, selfBlock);
                ApplyBlockToRenderers(bornRenderers, bornBlock);

                elapsed += Time.deltaTime;
                yield return null;
            }

            selfBlock.SetFloat(_dissolveId, 0f);
            selfBlock.SetFloat(_dissolveInvertId, 1f);
            selfBlock.SetFloat(_dissolveMinYId, selfMinY);
            selfBlock.SetFloat(_dissolveMaxYId, selfMaxY);

            bornBlock.SetFloat(_dissolveId, 1f);
            bornBlock.SetFloat(_dissolveInvertId, 0f);
            bornBlock.SetFloat(_dissolveMinYId, bornMinY);
            bornBlock.SetFloat(_dissolveMaxYId, bornMaxY);
            ApplyBlockToRenderers(selfRenderers, selfBlock);
            ApplyBlockToRenderers(bornRenderers, bornBlock);

            RestoreSharedMaterials(selfBackup);
            ClearPropertyBlocks(selfRenderers);
            ClearPropertyBlocks(bornRenderers);

            RestoreTransitionStateIfAny();

            if (_bornModelInstance != null)
            {
                Destroy(_bornModelInstance);
                _bornModelInstance = null;
            }

            SetSelfModelRenderersVisible(true);
            _transitionCoroutine = null;
        }

        private static void ComputeBoundsY(Renderer[] renderers, out float minY, out float maxY)
        {
            minY = 0f;
            maxY = 1f;

            if (renderers == null || renderers.Length == 0)
                return;

            bool has = false;
            float mn = 0f;
            float mx = 0f;
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null)
                    continue;

                var b = r.bounds;
                if (!has)
                {
                    mn = b.min.y;
                    mx = b.max.y;
                    has = true;
                }
                else
                {
                    mn = Mathf.Min(mn, b.min.y);
                    mx = Mathf.Max(mx, b.max.y);
                }
            }

            if (!has)
                return;

            minY = mn;
            maxY = Mathf.Max(mn + 1e-3f, mx);
        }

        private struct RendererMaterials
        {
            public Renderer Renderer;
            public Material[] Materials;
        }

        private List<RendererMaterials> BackupSharedMaterials(Renderer[] renderers)
        {
            var list = new List<RendererMaterials>();
            if (renderers == null)
                return list;

            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null)
                    continue;

                var mats = r.sharedMaterials;
                if (mats == null)
                    continue;

                list.Add(new RendererMaterials { Renderer = r, Materials = mats });
            }

            return list;
        }

        private static void RestoreSharedMaterials(List<RendererMaterials> backup)
        {
            if (backup == null)
                return;

            for (int i = 0; i < backup.Count; i++)
            {
                var entry = backup[i];
                if (entry.Renderer == null)
                    continue;

                entry.Renderer.sharedMaterials = entry.Materials;
            }
        }

        private static void SwapAllMaterialsWithClones(List<RendererMaterials> backup, Material template, List<Material> created)
        {
            if (backup == null || template == null)
                return;

            for (int i = 0; i < backup.Count; i++)
            {
                var entry = backup[i];
                if (entry.Renderer == null)
                    continue;

                var mats = entry.Materials;
                if (mats == null || mats.Length == 0)
                    continue;

                var swapped = new Material[mats.Length];
                for (int mi = 0; mi < mats.Length; mi++)
                {
                    var src = mats[mi];
                    var clone = new Material(template);
                    clone.hideFlags = HideFlags.DontSave;
                    CopyCommonMaterialPropertiesToMaterial(src, clone);
                    swapped[mi] = clone;
                    created?.Add(clone);
                }

                entry.Renderer.sharedMaterials = swapped;
            }
        }

        private void SetSelfModelRenderersVisible(bool visible)
        {
            var renderers = CollectSelfRenderers();
            SetRenderersVisible(renderers, visible);
        }

        private Renderer[] CollectSelfRenderers()
        {
            Transform root = selfModelRoot != null ? selfModelRoot : transform;
            var renderers = root.GetComponentsInChildren<Renderer>(true);

            if (_bornModelInstance == null)
                return renderers;

            var filtered = new List<Renderer>();
            var bornT = _bornModelInstance.transform;
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null)
                    continue;

                if (bornT != null && r.transform != null && r.transform.IsChildOf(bornT))
                    continue;

                filtered.Add(r);
            }
            return filtered.ToArray();
        }

        private Renderer[] CollectBornRenderers()
        {
            if (_bornModelInstance == null)
                return null;

            return _bornModelInstance.GetComponentsInChildren<Renderer>(true);
        }

        private static void SetRenderersVisible(Renderer[] renderers, bool visible)
        {
            if (renderers == null)
                return;

            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null)
                    continue;
                r.forceRenderingOff = !visible;
            }
        }

        private static void ApplyBlockToRenderers(Renderer[] renderers, MaterialPropertyBlock mpb)
        {
            if (renderers == null)
                return;

            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null)
                    continue;
                r.SetPropertyBlock(mpb);
            }
        }

        private static void ClearPropertyBlocks(Renderer[] renderers)
        {
            if (renderers == null)
                return;

            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null)
                    continue;
                r.SetPropertyBlock(null);
            }
        }

        private static void ClearPropertyBlocksFromBackup(List<RendererMaterials> backup)
        {
            if (backup == null)
                return;

            for (int i = 0; i < backup.Count; i++)
            {
                var r = backup[i].Renderer;
                if (r == null)
                    continue;
                r.SetPropertyBlock(null);
            }
        }

        private static void CopyCommonMaterialPropertiesToMaterial(Material source, Material target)
        {
            if (source == null)
                return;
            if (target == null)
                return;

            Texture baseMap = null;
            if (source.HasProperty(_baseMapId))
            {
                baseMap = source.GetTexture(_baseMapId);
            }
            else if (source.HasProperty(_mainTexId))
            {
                baseMap = source.GetTexture(_mainTexId);
            }
            if (baseMap != null && target.HasProperty(_baseMapId))
            {
                target.SetTexture(_baseMapId, baseMap);
            }

            Color baseColor = Color.white;
            bool hasBaseColor = false;
            if (source.HasProperty(_baseColorId))
            {
                baseColor = source.GetColor(_baseColorId);
                hasBaseColor = true;
            }
            else if (source.HasProperty(_colorId))
            {
                baseColor = source.GetColor(_colorId);
                hasBaseColor = true;
            }
            if (hasBaseColor && target.HasProperty(_baseColorId))
            {
                target.SetColor(_baseColorId, baseColor);
            }

            if (source.HasProperty(_cutoffId) && target.HasProperty(_cutoffId))
            {
                target.SetFloat(_cutoffId, source.GetFloat(_cutoffId));
            }

            if (source.HasProperty(_metallicId) && target.HasProperty(_metallicId))
            {
                target.SetFloat(_metallicId, source.GetFloat(_metallicId));
            }

            if (source.HasProperty(_smoothnessId) && target.HasProperty(_smoothnessId))
            {
                target.SetFloat(_smoothnessId, source.GetFloat(_smoothnessId));
            }
        }
    }
}
