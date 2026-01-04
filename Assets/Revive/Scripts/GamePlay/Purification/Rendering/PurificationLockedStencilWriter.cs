using UnityEngine;

namespace Revive.GamePlay.Purification.Rendering
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Revive/Purification/Purification Locked Stencil Writer")]
    public sealed class PurificationLockedStencilWriter : MonoBehaviour
    {
        [SerializeField] private Renderer[] targetRenderers;

        [SerializeField] private bool autoCollectFromChildren = true;

        private Material _maskMaterial;
        private Material[][] _originalSharedMaterials;
        private bool _applied;

        private void Awake()
        {
            ResolveRenderersIfNeeded();
        }

        private void OnEnable()
        {
            ResolveRenderersIfNeeded();
            Apply();
        }

        private void OnDisable()
        {
            Revert();
        }

        private void ResolveRenderersIfNeeded()
        {
            if (targetRenderers != null && targetRenderers.Length > 0)
                return;

            if (!autoCollectFromChildren)
            {
                Renderer r = GetComponent<Renderer>();
                if (r != null)
                    targetRenderers = new[] { r };
                return;
            }

            Renderer[] all = GetComponentsInChildren<Renderer>(includeInactive: true);
            if (all == null || all.Length == 0)
                return;

            int count = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Renderer r = all[i];
                if (r == null)
                    continue;
                if (r is MeshRenderer || r is SkinnedMeshRenderer)
                    count++;
            }

            if (count <= 0)
                return;

            targetRenderers = new Renderer[count];
            int w = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Renderer r = all[i];
                if (r == null)
                    continue;
                if (r is MeshRenderer || r is SkinnedMeshRenderer)
                    targetRenderers[w++] = r;
            }
        }

        private void EnsureMaterial()
        {
            if (_maskMaterial != null)
                return;

            Shader shader = Shader.Find("Hidden/Revive/SlimeStencilMask");
            if (shader == null)
                return;

            _maskMaterial = new Material(shader)
            {
                hideFlags = HideFlags.HideAndDontSave,
                renderQueue = 3002
            };

            if (_maskMaterial.HasProperty("_StencilRef"))
            {
                _maskMaterial.SetFloat("_StencilRef", 2f);
            }
        }

        private void Apply()
        {
            if (_applied)
                return;

            EnsureMaterial();
            if (_maskMaterial == null)
                return;

            if (targetRenderers == null || targetRenderers.Length == 0)
                return;

            _originalSharedMaterials = new Material[targetRenderers.Length][];

            for (int i = 0; i < targetRenderers.Length; i++)
            {
                Renderer r = targetRenderers[i];
                if (r == null)
                    continue;

                Material[] original = r.sharedMaterials;
                _originalSharedMaterials[i] = original;

                int originalCount = original != null ? original.Length : 0;
                Material[] next = new Material[originalCount + 1];
                for (int j = 0; j < originalCount; j++)
                {
                    next[j] = original[j];
                }

                next[originalCount] = _maskMaterial;
                r.sharedMaterials = next;
            }

            _applied = true;
        }

        private void Revert()
        {
            if (!_applied)
                return;

            if (targetRenderers != null && _originalSharedMaterials != null)
            {
                for (int i = 0; i < targetRenderers.Length; i++)
                {
                    Renderer r = targetRenderers[i];
                    if (r == null)
                        continue;

                    if (i < _originalSharedMaterials.Length && _originalSharedMaterials[i] != null)
                    {
                        r.sharedMaterials = _originalSharedMaterials[i];
                    }
                }
            }

            _applied = false;
            _originalSharedMaterials = null;

            if (_maskMaterial != null)
            {
                Destroy(_maskMaterial);
                _maskMaterial = null;
            }
        }
    }
}
