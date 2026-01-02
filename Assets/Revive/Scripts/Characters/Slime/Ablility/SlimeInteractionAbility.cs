using System.Collections;
using MoreMountains.Tools;
using MoreMountains.TopDownEngine;
using UnityEngine;

namespace Revive.Slime
{
    /// <summary>
    /// 史莱姆交互能力 - 处理 Emit/Recall/SwitchInstance 输入
    /// 使用 TopDownEngine 的 InputManager 按钮映射
    /// </summary>
    [AddComponentMenu("TopDown Engine/Character/Abilities/Slime Interaction")]
    public class SlimeInteractionAbility : CharacterAbility
    {
        [Header("Slime Reference")]
        [Tooltip("Slime_PBF 组件引用")]
        public Slime_PBF SlimePBF;
        public SlimeCarrySlot CarrySlot;
        public SlimeConsumeBuffController ConsumeBuffController;

        private static readonly int _dissolveId = Shader.PropertyToID("_Dissolve");
        private static readonly int _baseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int _baseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int _mainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int _colorId = Shader.PropertyToID("_Color");

        private bool _isConsuming;
        private Coroutine _consumeCoroutine;

        [Header("Button Mapping")]
        [Tooltip("发射粒子使用的按钮（默认 Shoot）")]
        public bool UseShootForEmit = true;
        
        [Tooltip("召回粒子使用的按钮（默认 SecondaryShoot）")]
        public bool UseSecondaryShootForRecall = true;
        
        [Tooltip("切换实例使用的按钮（默认 SwitchCharacter）")]
        public bool UseSwitchCharacterForSwitch = true;

        protected float _lastEmitTime;

        protected override void Initialization()
        {
            base.Initialization();

            if (SlimePBF == null)
            {
                SlimePBF = GetComponentInChildren<Slime_PBF>();
            }

            if (CarrySlot == null)
            {
                CarrySlot = GetComponentInChildren<SlimeCarrySlot>();
            }

            if (ConsumeBuffController == null)
            {
                ConsumeBuffController = GetComponentInChildren<SlimeConsumeBuffController>();
                if (ConsumeBuffController == null)
                {
                    var root = transform.root;
                    if (root != null)
                    {
                        ConsumeBuffController = root.GetComponentInChildren<SlimeConsumeBuffController>(true);
                    }
                }
            }

            if (SlimePBF == null)
            {
                Debug.LogWarning("[SlimeInteractionAbility] SlimePBF is null and no Slime_PBF component was found on the same GameObject.", this);
            }
        }

        public override void ProcessAbility()
        {
            base.ProcessAbility();
            
            if (_inputManager == null || SlimePBF == null)
                return;

            if (_isConsuming)
                return;
            
            HandleEmitInput();
            HandleRecallInput();
            HandleSwitchInput();
        }

        protected virtual void HandleEmitInput()
        {
            if (!UseShootForEmit)
                return;
            
            var shootState = _inputManager.ShootButton.State.CurrentState;
            bool wantEmitOnce = shootState == MMInput.ButtonStates.ButtonDown;
            bool wantEmitRepeat = shootState == MMInput.ButtonStates.ButtonPressed;

            if (!wantEmitOnce && !wantEmitRepeat)
                return;

            if (CarrySlot != null && CarrySlot.HasHeldObject)
            {
                if (CarrySlot.ThrowHeld())
                {
                    _lastEmitTime = Time.time;
                    return;
                }
            }

            if (Time.time - _lastEmitTime < SlimePBF.EmitCooldown)
                return;

            _lastEmitTime = Time.time;
            SlimePBF.EmitParticles();
        }

        protected virtual void HandleRecallInput()
        {
            if (!UseSecondaryShootForRecall)
                return;
            
            if (_inputManager.SecondaryShootButton.State.CurrentState == MMInput.ButtonStates.ButtonDown)
            {
                if (CarrySlot != null && CarrySlot.HasHeldObject)
                {
                    var held = CarrySlot.HeldObject;
                    var consumeSpec = held != null ? held.GetComponent<SlimeCarryableConsumeSpec>() : null;
                    if (consumeSpec != null)
                    {
                        if (_consumeCoroutine != null)
                        {
                            StopCoroutine(_consumeCoroutine);
                            _consumeCoroutine = null;
                        }

                        _consumeCoroutine = StartCoroutine(ConsumeHeldRoutine(held, consumeSpec));
                        return;
                    }
                }

                SlimePBF.StartRecall();
            }
        }

        private IEnumerator ConsumeHeldRoutine(SlimeCarryableObject held, SlimeCarryableConsumeSpec consumeSpec)
        {
            _isConsuming = true;

            if (ConsumeBuffController != null && consumeSpec != null)
            {
                ConsumeBuffController.Apply(consumeSpec);
            }

            if (SlimePBF != null && consumeSpec != null)
            {
                SlimePBF.TriggerConsumeBubbleBurst(
                    consumeSpec.ConsumeBubbleBurstCount,
                    consumeSpec.ConsumeBubbleLifetimeSeconds,
                    consumeSpec.ConsumeBubbleRadiusMultiplier,
                    consumeSpec.ConsumeBubbleUpSpeedWorld
                );

                if (consumeSpec.ConsumeBubbleBoostSeconds > 0f)
                {
                    SlimePBF.TriggerConsumeBubbleBoost(
                        consumeSpec.ConsumeBubbleBoostMultiplier,
                        consumeSpec.ConsumeBubbleBoostSeconds,
                        consumeSpec.ConsumeBubbleBoostSizeMultiplier
                    );
                }
            }

            if (held == null)
            {
                _isConsuming = false;
                _consumeCoroutine = null;
                yield break;
            }

            var renderers = held.GetComponentsInChildren<Renderer>(true);
            var dissolveMat = consumeSpec != null ? consumeSpec.ConsumeDissolveMaterial : null;
            float dissolveSeconds = consumeSpec != null ? Mathf.Max(0f, consumeSpec.ConsumeDissolveSeconds) : 0f;

            var mpb = new MaterialPropertyBlock();
            if (renderers != null && renderers.Length > 0)
            {
                var sourceMat = renderers[0] != null ? renderers[0].sharedMaterial : null;
                CopyCommonMaterialPropertiesToBlock(sourceMat, mpb);

                if (dissolveMat != null)
                {
                    for (int i = 0; i < renderers.Length; i++)
                    {
                        var r = renderers[i];
                        if (r == null)
                            continue;
                        var mats = r.sharedMaterials;
                        if (mats == null || mats.Length == 0)
                            continue;

                        var swapped = new Material[mats.Length];
                        for (int mi = 0; mi < swapped.Length; mi++)
                        {
                            swapped[mi] = dissolveMat;
                        }
                        r.sharedMaterials = swapped;
                    }
                }
            }

            if (dissolveSeconds > 0f)
            {
                float elapsed = 0f;
                while (elapsed < dissolveSeconds)
                {
                    float t = Mathf.Clamp01(elapsed / dissolveSeconds);
                    mpb.SetFloat(_dissolveId, t);
                    ApplyBlockToRenderers(renderers, mpb);
                    elapsed += Time.deltaTime;
                    yield return null;
                }
            }

            mpb.SetFloat(_dissolveId, 1f);
            ApplyBlockToRenderers(renderers, mpb);

            if (CarrySlot != null)
            {
                if (CarrySlot.ConsumeHeld(out var consumed, false))
                {
                    if (consumed != null)
                    {
                        Destroy(consumed.gameObject);
                    }
                }
            }

            _isConsuming = false;
            _consumeCoroutine = null;
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

        private static void CopyCommonMaterialPropertiesToBlock(Material source, MaterialPropertyBlock mpb)
        {
            if (mpb == null)
                return;

            if (source == null)
                return;

            mpb.SetFloat(_dissolveId, 0f);

            Texture baseMap = null;
            if (source.HasProperty(_baseMapId))
            {
                baseMap = source.GetTexture(_baseMapId);
            }
            else if (source.HasProperty(_mainTexId))
            {
                baseMap = source.GetTexture(_mainTexId);
            }
            if (baseMap != null)
            {
                mpb.SetTexture(_baseMapId, baseMap);
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
            if (hasBaseColor)
            {
                mpb.SetColor(_baseColorId, baseColor);
            }
        }

        protected virtual void HandleSwitchInput()
        {
            if (!UseSwitchCharacterForSwitch)
                return;
            
            if (_inputManager.SwitchCharacterButton.State.CurrentState == MMInput.ButtonStates.ButtonDown)
            {
                SlimePBF.SwitchInstance();
            }
        }
    }
}
