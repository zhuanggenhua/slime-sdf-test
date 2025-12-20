using System.Collections.Generic;
using MoreMountains.Tools;
using MoreMountains.TopDownEngine;
using UnityEngine;

namespace Revive
{
    /// <summary>
    /// The Slime(Temp) Character Ability
    /// </summary>
    public class SlimeAbility : CharacterAbility
    {
        /// <summary>
        /// The list of feets transforms
        /// </summary>
        [Header("Bindings")]
        public List<Transform> Feets;
        
        /// <summary>
        /// The body base transform
        /// </summary>
        public Transform BodyBase;
        
        /// <summary>
        /// Sweat particle system
        /// </summary>
        public ParticleSystem Sweat;

        [Header("Settings")] 
        public float MaxFeetWiggleSpeed = 10f;
        
        private float _maxSpeed;
        private Vector3 _wiggleSpeed;

        private CharacterOrientation3D _characterOrientation3D;
        private ParticleSystem.EmissionModule _sweatEmission;
        private float _initialSweatEmission;
        
        protected override void Initialization()
        {
            base.Initialization();
            _characterOrientation3D = _character.FindAbility<CharacterOrientation3D>();
            _maxSpeed = _characterMovement.WalkSpeed;
            _sweatEmission = Sweat.emission;
            _initialSweatEmission = _sweatEmission.rateOverTimeMultiplier;
        }

        public override void ProcessAbility()
        {
            base.ProcessAbility();
            HandleFeetMovement();
            HandleSweat();
        }

        protected virtual void HandleSweat()
        {
            float multiplier = MMMaths.Remap(_controller.Speed.magnitude, 0f, _maxSpeed, 0f, 1f);
            multiplier *= _initialSweatEmission;
            _sweatEmission.rateOverTimeMultiplier = multiplier;
        }

        protected virtual void HandleFeetMovement()
        {
            _wiggleSpeed.z = MMMaths.Remap(_controller.Speed.magnitude, 0f, _maxSpeed, 0f, MaxFeetWiggleSpeed);
            
            // we make every feet move
            foreach (Transform feet in Feets)
            {
                // example:
                // feet.transform.Rotate(_rotationSpeed * Time.deltaTime, Space.Self);
            }
        }
    }
}

