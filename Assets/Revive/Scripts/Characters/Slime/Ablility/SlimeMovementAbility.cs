using System.Collections;
using MoreMountains.TopDownEngine;
using UnityEngine;

namespace Revive.Slime
{
    /// <summary>
    /// 史莱姆移动能力 - 继承 CharacterMovement，自动绑定 Slime_PBF
    /// 替换 CharacterMovement 使用
    /// </summary>
    [AddComponentMenu("TopDown Engine/Character/Abilities/Slime Movement")]
    public class SlimeMovementAbility : CharacterMovement
    {
        [Header("Slime Bindings")]
        [Tooltip("Slime_PBF 组件，用于自动绑定 trans")]
        public Slime_PBF SlimePBF;

        [Header("Footstep")]
        public AudioSource FootstepAudioSource;
        public AudioClip[] FootstepClips;
        public Vector2 NextClipDelaySeconds = Vector2.zero;
        public bool StopImmediatelyWhenNotWalking = true;

        private Coroutine _footstepRoutine;
        private int _lastFootstepClipIndex = -1;

        protected override void Initialization()
        {
            base.Initialization();

            if (SlimePBF == null)
            {
                SlimePBF = GetComponentInChildren<Slime_PBF>();
            }

            if (SlimePBF == null)
            {
                Debug.LogWarning("[SlimeMovementAbility] SlimePBF is null and no Slime_PBF component was found on the same GameObject.", this);
                return;
            }

            if (SlimePBF.trans == null)
            {
                SlimePBF.trans = transform;
            }

            EnsureFootstepAudioSource();
        }

        public override void ProcessAbility()
        {
            base.ProcessAbility();

            if (!AbilityAuthorized
                || ((_condition.CurrentState != CharacterStates.CharacterConditions.Normal)
                    && (_condition.CurrentState != CharacterStates.CharacterConditions.ControlledMovement)))
            {
                StopFootstepLoop(true);
                return;
            }

            if (_controller == null)
            {
                StopFootstepLoop(true);
                return;
            }

            if (!ShouldPlayFootsteps())
            {
                StopFootstepLoop(StopImmediatelyWhenNotWalking);
                return;
            }

            StartFootstepLoopIfNeeded();
        }

        protected virtual void OnDisable()
        {
            StopFootstepLoop(true);
        }

        private bool ShouldPlayFootsteps()
        {
            return _controller.Grounded
                   && (_movement != null)
                   && (_movement.CurrentState == CharacterStates.MovementStates.Walking)
                   && (_controller.CurrentMovement.magnitude > IdleThreshold)
                   && (FootstepClips != null)
                   && (FootstepClips.Length > 0);
        }

        private void EnsureFootstepAudioSource()
        {
            if (FootstepAudioSource != null)
            {
                return;
            }

            FootstepAudioSource = GetComponent<AudioSource>();
            if (FootstepAudioSource == null)
            {
                FootstepAudioSource = gameObject.AddComponent<AudioSource>();
            }
        }

        private void StartFootstepLoopIfNeeded()
        {
            EnsureFootstepAudioSource();

            if (_footstepRoutine != null)
            {
                return;
            }

            if (!ShouldPlayFootsteps())
            {
                return;
            }

            _footstepRoutine = StartCoroutine(FootstepLoop());
        }

        private void StopFootstepLoop(bool stopAudioImmediately)
        {
            if (_footstepRoutine != null)
            {
                StopCoroutine(_footstepRoutine);
                _footstepRoutine = null;
            }

            if (stopAudioImmediately && FootstepAudioSource != null)
            {
                FootstepAudioSource.Stop();
            }
        }

        private IEnumerator FootstepLoop()
        {
            while (true)
            {
                if (!ShouldPlayFootsteps())
                {
                    _footstepRoutine = null;
                    yield break;
                }

                EnsureFootstepAudioSource();

                int clipIndex = GetNextClipIndex();
                AudioClip clip = FootstepClips[clipIndex];
                _lastFootstepClipIndex = clipIndex;

                if (clip == null)
                {
                    yield return null;
                    continue;
                }

                FootstepAudioSource.clip = clip;
                FootstepAudioSource.Play();

                while (FootstepAudioSource != null && FootstepAudioSource.isPlaying)
                {
                    if (!ShouldPlayFootsteps() && StopImmediatelyWhenNotWalking)
                    {
                        FootstepAudioSource.Stop();
                        _footstepRoutine = null;
                        yield break;
                    }

                    yield return null;
                }

                float delay = GetNextClipDelaySeconds();
                if (delay > 0f)
                {
                    float t = 0f;
                    while (t < delay)
                    {
                        if (!ShouldPlayFootsteps())
                        {
                            _footstepRoutine = null;
                            yield break;
                        }

                        t += Time.deltaTime;
                        yield return null;
                    }
                }
            }
        }

        private int GetNextClipIndex()
        {
            int length = FootstepClips.Length;
            if (length <= 1)
            {
                return 0;
            }

            int index = Random.Range(0, length);
            if (index == _lastFootstepClipIndex)
            {
                index = (index + 1) % length;
            }

            return index;
        }

        private float GetNextClipDelaySeconds()
        {
            float min = Mathf.Min(NextClipDelaySeconds.x, NextClipDelaySeconds.y);
            float max = Mathf.Max(NextClipDelaySeconds.x, NextClipDelaySeconds.y);
            if (max <= 0f)
            {
                return 0f;
            }

            return Random.Range(Mathf.Max(0f, min), max);
        }
    }
}
