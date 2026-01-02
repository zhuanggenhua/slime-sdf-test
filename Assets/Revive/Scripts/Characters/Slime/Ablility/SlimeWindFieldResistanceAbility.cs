using MoreMountains.TopDownEngine;
using Revive.Environment;
using UnityEngine;

namespace Revive.Slime
{
    [AddComponentMenu("TopDown Engine/Character/Abilities/Slime Wind Field Resistance")]
    public class SlimeWindFieldResistanceAbility : CharacterAbility
    {
        [Revive.ChineseHeader("顺风")]
        [Revive.ChineseLabel("顺风加速")]
        [SerializeField, Revive.DefaultValue(false)]
        private bool enableTailwindBoost;

        [SerializeField] private SlimeConsumeBuffController consumeBuffController;

        protected override void Initialization()
        {
            base.Initialization();

            if (consumeBuffController == null)
            {
                consumeBuffController = GetComponentInChildren<SlimeConsumeBuffController>();
                if (consumeBuffController == null)
                {
                    var root = transform.root;
                    if (root != null)
                    {
                        consumeBuffController = root.GetComponentInChildren<SlimeConsumeBuffController>(true);
                    }
                }
            }
        }

        private void FixedUpdate()
        {
            if (!AbilityAuthorized)
                return;

            if (_controller3D == null)
                return;

            if (_characterMovement == null)
                return;

            if (!_controller3D.FreeMovement)
                return;

            if (_controller3D.UpdateMode != TopDownController3D.UpdateModes.FixedUpdate)
                return;

            if (consumeBuffController != null && consumeBuffController.WindFieldImmuneActive)
                return;

            WindFieldRegistry.GetCombinedAtWorldPosition(
                transform.position,
                gameObject,
                out float groundDrag,
                out float airDrag,
                out Vector3 pushVector
            );

            float drag = _controller3D.Grounded ? groundDrag : airDrag;

            Vector3 currentMove = _controller3D.CurrentMovement;
            Vector3 currentMoveXZ = new Vector3(currentMove.x, 0f, currentMove.z);
            Vector3 windXZ = new Vector3(pushVector.x, 0f, pushVector.z);

            if (drag > 0f && currentMoveXZ.sqrMagnitude > 0.000001f)
            {
                float speedMultiplier = 1f / (1f + drag);
                if (windXZ.sqrMagnitude > 0.000001f)
                {
                    Vector3 windDir = windXZ.normalized;
                    Vector3 moveDir = currentMoveXZ.normalized;
                    float dot = Vector3.Dot(moveDir, windDir);
                    if (dot > 0.0001f)
                    {
                        speedMultiplier = enableTailwindBoost ? (1f + drag) : 1f;
                    }
                    else if (dot < -0.0001f)
                    {
                        speedMultiplier = 1f / (1f + drag);
                    }
                    else
                    {
                        speedMultiplier = 1f;
                    }
                }

                if (speedMultiplier != 1f)
                {
                    _controller3D.AddForce(currentMoveXZ * (speedMultiplier - 1f));
                }
            }

            if (!_controller3D.Grounded && pushVector.sqrMagnitude > 0.000001f)
            {
                _controller3D.AddForce(pushVector);
            }
        }

        public override void LateProcessAbility()
        {
            base.LateProcessAbility();

            if (!AbilityAuthorized)
                return;

            if (_controller3D == null)
                return;

            if (_characterMovement == null)
                return;

            if (!_controller3D.FreeMovement)
                return;

            if (_controller3D.UpdateMode != TopDownController3D.UpdateModes.Update)
                return;

            if (consumeBuffController != null && consumeBuffController.WindFieldImmuneActive)
                return;

            WindFieldRegistry.GetCombinedAtWorldPosition(
                transform.position,
                gameObject,
                out float groundDrag,
                out float airDrag,
                out Vector3 pushVector
            );

            float drag = _controller3D.Grounded ? groundDrag : airDrag;

            Vector3 currentMove = _controller3D.CurrentMovement;
            Vector3 currentMoveXZ = new Vector3(currentMove.x, 0f, currentMove.z);
            Vector3 windXZ = new Vector3(pushVector.x, 0f, pushVector.z);

            if (drag > 0f && currentMoveXZ.sqrMagnitude > 0.000001f)
            {
                float speedMultiplier = 1f / (1f + drag);
                if (windXZ.sqrMagnitude > 0.000001f)
                {
                    Vector3 windDir = windXZ.normalized;
                    Vector3 moveDir = currentMoveXZ.normalized;
                    float dot = Vector3.Dot(moveDir, windDir);
                    if (dot > 0.0001f)
                    {
                        speedMultiplier = enableTailwindBoost ? (1f + drag) : 1f;
                    }
                    else if (dot < -0.0001f)
                    {
                        speedMultiplier = 1f / (1f + drag);
                    }
                    else
                    {
                        speedMultiplier = 1f;
                    }
                }

                if (speedMultiplier != 1f)
                {
                    _controller3D.AddForce(currentMoveXZ * (speedMultiplier - 1f));
                }
            }

            if (!_controller3D.Grounded && pushVector.sqrMagnitude > 0.000001f)
            {
                _controller3D.AddForce(pushVector);
            }
        }
    }
}
