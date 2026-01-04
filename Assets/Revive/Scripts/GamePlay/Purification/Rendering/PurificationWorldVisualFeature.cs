using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif

namespace Revive.GamePlay.Purification.Rendering
{
    [DisallowMultipleRendererFeature("Purification World Visual")]
    public class PurificationWorldVisualFeature : ScriptableRendererFeature
    {
        [Serializable]
        public class Settings
        {
            public RenderPassEvent PassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
            public Shader Shader;

            [Range(0f, 2f)]
            public float UnlockFadeSeconds;

            [Range(0.001f, 0.5f)]
            public float UnlockFadeFeather01 = 0.1f;

            [Range(0f, 1f)]
            public float RuinStrength = 1f;

            [Range(0f, 1f)]
            public float Desaturate = 1f;

            [Range(0f, 1f)]
            public float Darken = 0.6f;
        }

        private static readonly int RuinStrengthId = Shader.PropertyToID("_RuinStrength");
        private static readonly int DesaturateId = Shader.PropertyToID("_Desaturate");
        private static readonly int DarkenId = Shader.PropertyToID("_Darken");

        private static readonly int LockedMaskTexId = Shader.PropertyToID("_LockedMaskTex");

        private static readonly Dictionary<int, Renderer[]> LockedRendererGroups = new Dictionary<int, Renderer[]>(16);
        private static float _currentUnlockFadeSeconds;
        private static float _currentUnlockFadeFeather01;

        [SerializeField] private Settings settings = new Settings();

        private Material _material;
        private PurificationWorldVisualPass _pass;

        public static void SetLockedRenderers(int key, Renderer[] renderers)
        {
            if (renderers == null || renderers.Length == 0)
            {
                LockedRendererGroups.Remove(key);
                return;
            }

            LockedRendererGroups[key] = renderers;
        }

        public static void RemoveLockedRenderers(int key)
        {
            LockedRendererGroups.Remove(key);
        }

        public static bool TryGetUnlockFadeSettings(out float seconds, out float feather01)
        {
            seconds = _currentUnlockFadeSeconds;
            feather01 = _currentUnlockFadeFeather01;
            return seconds > 0f;
        }

        public override void Create()
        {
            if (settings.Shader == null)
            {
                settings.Shader = Shader.Find("Hidden/Revive/PurificationWorldVisual");
            }
            _currentUnlockFadeSeconds = settings.UnlockFadeSeconds;
            _currentUnlockFadeFeather01 = settings.UnlockFadeFeather01;

            if (settings.Shader == null)
            {
                _material = null;
                _pass = null;
                return;
            }

            _material = CoreUtils.CreateEngineMaterial(settings.Shader);
            _pass = new PurificationWorldVisualPass(_material)
            {
                renderPassEvent = settings.PassEvent
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (_pass != null)
            {
                _pass.Dispose();
                _pass = null;
            }

            if (_material != null)
            {
                CoreUtils.Destroy(_material);
                _material = null;
            }
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_pass == null || _material == null)
                return;

            if (renderingData.cameraData.renderType == CameraRenderType.Overlay)
                return;

            _material.SetFloat(RuinStrengthId, settings.RuinStrength);
            _material.SetFloat(DesaturateId, settings.Desaturate);
            _material.SetFloat(DarkenId, settings.Darken);
            _currentUnlockFadeSeconds = settings.UnlockFadeSeconds;
            _currentUnlockFadeFeather01 = settings.UnlockFadeFeather01;

            renderer.EnqueuePass(_pass);
        }

        private sealed class PurificationWorldVisualPass : ScriptableRenderPass
        {
            private const string ProfilerTag = "Purification World Visual";
            private readonly ProfilingSampler _profilingSampler = new ProfilingSampler(ProfilerTag);

            private readonly Material _material;

            private RTHandle _tempColor;
            private RTHandle _lockedMaskMsaa;
            private RTHandle _lockedMaskResolved;

            private static bool _warnedMissingDepth;

            public PurificationWorldVisualPass(Material material)
            {
                _material = material;
                ConfigureInput(ScriptableRenderPassInput.Depth);
            }

            public void Dispose()
            {
                _tempColor?.Release();
                _tempColor = null;

                _lockedMaskMsaa?.Release();
                _lockedMaskMsaa = null;

                _lockedMaskResolved?.Release();
                _lockedMaskResolved = null;
            }

            private static void DrawLockedMask(CommandBuffer cmd, Material material)
            {
                if (LockedRendererGroups.Count == 0)
                    return;

                foreach (var kv in LockedRendererGroups)
                {
                    var renderers = kv.Value;
                    if (renderers == null)
                        continue;

                    for (int i = 0; i < renderers.Length; i++)
                    {
                        var r = renderers[i];
                        if (r == null)
                            continue;
                        if (!r.enabled)
                            continue;
                        if (r.forceRenderingOff)
                            continue;

                        var go = r.gameObject;
                        if (go == null || !go.activeInHierarchy)
                            continue;

                        int submeshCount = 1;
                        if (r is SkinnedMeshRenderer skinned)
                        {
                            Mesh m = skinned.sharedMesh;
                            if (m != null)
                                submeshCount = Mathf.Max(1, m.subMeshCount);
                        }
                        else if (r is MeshRenderer)
                        {
                            MeshFilter mf = r.GetComponent<MeshFilter>();
                            Mesh m = mf != null ? mf.sharedMesh : null;
                            if (m != null)
                                submeshCount = Mathf.Max(1, m.subMeshCount);
                        }

                        for (int sm = 0; sm < submeshCount; sm++)
                        {
                            cmd.DrawRenderer(r, material, sm, 1);
                        }
                    }
                }
            }

            private static void DrawLockedMask(IRasterCommandBuffer cmd, Material material)
            {
                if (LockedRendererGroups.Count == 0)
                    return;

                foreach (var kv in LockedRendererGroups)
                {
                    var renderers = kv.Value;
                    if (renderers == null)
                        continue;

                    for (int i = 0; i < renderers.Length; i++)
                    {
                        var r = renderers[i];
                        if (r == null)
                            continue;
                        if (!r.enabled)
                            continue;
                        if (r.forceRenderingOff)
                            continue;

                        var go = r.gameObject;
                        if (go == null || !go.activeInHierarchy)
                            continue;

                        int submeshCount = 1;
                        if (r is SkinnedMeshRenderer skinned)
                        {
                            Mesh m = skinned.sharedMesh;
                            if (m != null)
                                submeshCount = Mathf.Max(1, m.subMeshCount);
                        }
                        else if (r is MeshRenderer)
                        {
                            MeshFilter mf = r.GetComponent<MeshFilter>();
                            Mesh m = mf != null ? mf.sharedMesh : null;
                            if (m != null)
                                submeshCount = Mathf.Max(1, m.subMeshCount);
                        }

                        for (int sm = 0; sm < submeshCount; sm++)
                        {
                            cmd.DrawRenderer(r, material, sm, 1);
                        }
                    }
                }
            }

#if UNITY_6000_0_OR_NEWER
            private sealed class MaskPassData
            {
                internal Material Material;
            }

            private sealed class ResolvePassData
            {
                internal TextureHandle Source;
                internal TextureHandle Dest;
            }

            private sealed class PassData
            {
                internal Material Material;
                internal TextureHandle Source;
                internal TextureHandle Temp;
                internal TextureHandle Depth;
                internal TextureHandle Mask;
            }

            private static void ExecuteMask(MaskPassData data, RasterGraphContext context)
            {
                context.cmd.ClearRenderTarget(false, true, Color.black);
                DrawLockedMask(context.cmd, data.Material);
            }

            private static void ExecuteResolve(ResolvePassData data, UnsafeGraphContext context)
            {
                CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                Blitter.BlitCameraTexture(cmd, data.Source, data.Dest);
            }

            private static void ExecutePass(PassData data, UnsafeGraphContext context)
            {
                CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

                Blitter.BlitCameraTexture(cmd, data.Source, data.Temp, data.Material, pass: 0);
                Blitter.BlitCameraTexture(cmd, data.Temp, data.Source);
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

                TextureHandle source = resourceData.activeColorTexture;
                TextureHandle depth = resourceData.activeDepthTexture.IsValid() ? resourceData.activeDepthTexture : resourceData.cameraDepthTexture;

                RenderTextureDescriptor desc = cameraData.cameraTargetDescriptor;
                desc.depthBufferBits = 0;
                desc.msaaSamples = 1;

                TextureHandle temp = UniversalRenderer.CreateRenderGraphTexture(
                    renderGraph,
                    desc,
                    name: "_PurificationWorldVisualTemp",
                    clear: false,
                    filterMode: FilterMode.Bilinear,
                    wrapMode: TextureWrapMode.Clamp);

                RenderTextureDescriptor maskDesc = cameraData.cameraTargetDescriptor;
                maskDesc.depthBufferBits = 0;
                maskDesc.graphicsFormat = GraphicsFormat.R8_UNorm;

                RenderTextureDescriptor maskResolvedDesc = maskDesc;
                maskResolvedDesc.msaaSamples = 1;

                TextureHandle maskResolved = UniversalRenderer.CreateRenderGraphTexture(
                    renderGraph,
                    maskResolvedDesc,
                    name: "_PurificationLockedMaskTexResolved",
                    clear: false,
                    filterMode: FilterMode.Point,
                    wrapMode: TextureWrapMode.Clamp);

                bool hasValidDepth = depth.IsValid();
                bool hasLockedRenderers = LockedRendererGroups.Count > 0;
                if (!hasLockedRenderers || !hasValidDepth)
                {
                    using (var builder = renderGraph.AddRasterRenderPass<MaskPassData>("Purification Locked Mask Clear", out var passData))
                    {
                        passData.Material = null;
                        builder.SetRenderAttachment(maskResolved, 0, AccessFlags.Write);
                        builder.SetGlobalTextureAfterPass(maskResolved, LockedMaskTexId);
                        builder.AllowPassCulling(false);
                        builder.SetRenderFunc((MaskPassData data, RasterGraphContext context) => context.cmd.ClearRenderTarget(false, true, Color.black));
                    }

                    using (var builder = renderGraph.AddUnsafePass<PassData>(ProfilerTag, out var passData))
                    {
                        passData.Material = _material;
                        passData.Source = source;
                        passData.Temp = temp;
                        passData.Mask = maskResolved;

                        builder.UseTexture(passData.Source, AccessFlags.ReadWrite);
                        builder.UseTexture(passData.Temp, AccessFlags.ReadWrite);
                        builder.UseTexture(passData.Mask, AccessFlags.Read);

                        builder.AllowPassCulling(false);

                        builder.SetRenderFunc((PassData data, UnsafeGraphContext context) => ExecutePass(data, context));
                    }

                    return;
                }

                bool msaa = cameraData.cameraTargetDescriptor.msaaSamples > 1;
                TextureHandle maskDraw = maskResolved;
                TextureHandle maskMsaa = default;
                if (msaa)
                {
                    maskMsaa = UniversalRenderer.CreateRenderGraphTexture(
                        renderGraph,
                        maskDesc,
                        name: "_PurificationLockedMaskTexMsaa",
                        clear: false,
                        filterMode: FilterMode.Point,
                        wrapMode: TextureWrapMode.Clamp);
                    maskDraw = maskMsaa;
                }

                using (var builder = renderGraph.AddRasterRenderPass<MaskPassData>("Purification Locked Mask", out var passData))
                {
                    passData.Material = _material;

                    builder.SetRenderAttachment(maskDraw, 0, AccessFlags.Write);
                    builder.SetRenderAttachmentDepth(depth, AccessFlags.ReadWrite);

                    if (!msaa)
                        builder.SetGlobalTextureAfterPass(maskResolved, LockedMaskTexId);
                    builder.AllowPassCulling(false);

                    builder.SetRenderFunc((MaskPassData data, RasterGraphContext context) => ExecuteMask(data, context));
                }

                if (msaa)
                {
                    using (var builder = renderGraph.AddUnsafePass<ResolvePassData>("Purification Locked Mask Resolve", out var resolveData))
                    {
                        resolveData.Source = maskMsaa;
                        resolveData.Dest = maskResolved;

                        builder.UseTexture(resolveData.Source, AccessFlags.Read);
                        builder.UseTexture(resolveData.Dest, AccessFlags.Write);
                        builder.AllowPassCulling(false);
                        builder.SetGlobalTextureAfterPass(maskResolved, LockedMaskTexId);

                        builder.SetRenderFunc((ResolvePassData data, UnsafeGraphContext context) => ExecuteResolve(data, context));
                    }
                }

                using (var builder = renderGraph.AddUnsafePass<PassData>(ProfilerTag, out var passData))
                {
                    passData.Material = _material;
                    passData.Source = source;
                    passData.Temp = temp;
                    passData.Mask = maskResolved;

                    builder.UseTexture(passData.Source, AccessFlags.ReadWrite);
                    builder.UseTexture(passData.Temp, AccessFlags.ReadWrite);
                    builder.UseTexture(passData.Mask, AccessFlags.Read);

                    builder.AllowPassCulling(false);

                    builder.SetRenderFunc((PassData data, UnsafeGraphContext context) => ExecutePass(data, context));
                }
            }
 #endif

#if UNITY_6000_0_OR_NEWER
            [Obsolete]
#endif
            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
            {
                RenderTextureDescriptor desc = renderingData.cameraData.cameraTargetDescriptor;
                desc.depthBufferBits = 0;
                desc.msaaSamples = 1;

#if UNITY_6000_0_OR_NEWER
                RenderingUtils.ReAllocateHandleIfNeeded(ref _tempColor, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_PurificationWorldVisualTemp");
#else
                RenderingUtils.ReAllocateIfNeeded(ref _tempColor, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_PurificationWorldVisualTemp");
#endif

                RenderTextureDescriptor maskDesc = renderingData.cameraData.cameraTargetDescriptor;
                maskDesc.depthBufferBits = 0;
                maskDesc.graphicsFormat = GraphicsFormat.R8_UNorm;

                RenderTextureDescriptor maskResolvedDesc = maskDesc;
                maskResolvedDesc.msaaSamples = 1;
#if UNITY_6000_0_OR_NEWER
                RenderingUtils.ReAllocateHandleIfNeeded(ref _lockedMaskResolved, maskResolvedDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "_PurificationLockedMaskTexResolved");
#else
                RenderingUtils.ReAllocateIfNeeded(ref _lockedMaskResolved, maskResolvedDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "_PurificationLockedMaskTexResolved");
#endif

                if (renderingData.cameraData.cameraTargetDescriptor.msaaSamples > 1)
                {
#if UNITY_6000_0_OR_NEWER
                    RenderingUtils.ReAllocateHandleIfNeeded(ref _lockedMaskMsaa, maskDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "_PurificationLockedMaskTexMsaa");
#else
                    RenderingUtils.ReAllocateIfNeeded(ref _lockedMaskMsaa, maskDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "_PurificationLockedMaskTexMsaa");
#endif
                }
                else
                {
                    _lockedMaskMsaa?.Release();
                    _lockedMaskMsaa = null;
                }
            }

#if UNITY_6000_0_OR_NEWER
            [Obsolete]
#endif
            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (_material == null)
                    return;

                RTHandle source = renderingData.cameraData.renderer.cameraColorTargetHandle;
                if (source == null)
                    return;

                CommandBuffer cmd = CommandBufferPool.Get(ProfilerTag);
                using (new ProfilingScope(cmd, _profilingSampler))
                {
                    if (LockedRendererGroups.Count > 0 && _lockedMaskResolved != null)
                    {
                        RTHandle depth = renderingData.cameraData.renderer.cameraDepthTargetHandle;
                        if (depth != null)
                        {
                            bool msaa = renderingData.cameraData.cameraTargetDescriptor.msaaSamples > 1 && _lockedMaskMsaa != null;
                            RTHandle maskDraw = msaa ? _lockedMaskMsaa : _lockedMaskResolved;

                            cmd.SetRenderTarget(maskDraw, depth);
                            cmd.ClearRenderTarget(false, true, Color.black);
                            DrawLockedMask(cmd, _material);

                            if (msaa)
                            {
                                Blitter.BlitCameraTexture(cmd, _lockedMaskMsaa, _lockedMaskResolved);
                            }
                            cmd.SetGlobalTexture(LockedMaskTexId, _lockedMaskResolved);
                        }
                        else
                        {
                            if (!_warnedMissingDepth)
                            {
                                _warnedMissingDepth = true;
                                Debug.LogWarning("[PurificationWorldVisual] Locked mask needs a valid camera depth target. Please ensure Depth Texture is available for this camera/renderer.");
                            }
                            cmd.SetGlobalTexture(LockedMaskTexId, Texture2D.blackTexture);
                        }
                    }
                    else
                    {
                        cmd.SetGlobalTexture(LockedMaskTexId, Texture2D.blackTexture);
                    }

                    Blitter.BlitCameraTexture(cmd, source, _tempColor, _material, pass: 0);
                    Blitter.BlitCameraTexture(cmd, _tempColor, source);
                }
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
        }
    }
}
