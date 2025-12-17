using MoreMountains.Tools;
using UnityEngine;

namespace MoreMountains.TopDownEngine
{
	/// <summary>
	/// A class you can use in replacement of the CharacterMovement ability to handle a spaceship like movement, where the vertical axis handles acceleration and deceleration, and the horizontal axis handles rotation
	/// </summary>
	[MMHiddenProperties("AbilityStartFeedbacks", "AbilityStopFeedbacks")]
	[AddComponentMenu("TopDown Engine/Character/Abilities/Character Ship Movement")]
	public class CharacterShipMovement : CharacterAbility
	{
		[Header("Settings")]
		/// whether or not movement input is authorized at that time
		[Tooltip("whether or not movement input is authorized at that time")]
		public bool InputAuthorized = true;
		
		[Header("Acceleration")]
		/// the curve to use to accelerate the character
		[Tooltip("the curve to use to accelerate the character")]
		public AnimationCurve AccelerationCurve = new AnimationCurve(new Keyframe(0, 0), new Keyframe(1, 1));
		/// the duration of the acceleration phase, in seconds
		[Tooltip("the duration of the acceleration phase, in seconds")]
		public float AccelerationDuration = 2f;
		/// the curve to use to decelerate the character
		[Tooltip("the curve to use to decelerate the character")]
		public AnimationCurve DecelerationCurve = new AnimationCurve(new Keyframe(0, 1), new Keyframe(1, 0));
		/// the duration of the deceleration phase, in seconds
		[Tooltip("the duration of the deceleration phase, in seconds")]
		public float DecelerationDuration = 2f;
		/// the maximum speed the character can reach
		[Tooltip("the maximum speed the character can reach")]
		public float MaximumSpeed = 5f;
		/// the current speed of the character
		[Tooltip("the current speed of the character")]
		[MMReadOnly]
		public float CurrentSpeed;

		[Header("Rotation")] 
		/// the speed at which the character rotates
		[Tooltip("the speed at which the character rotates")]
		public float RotationSpeed = 75f;
		
		protected float _horizontalMovement;
		protected float _verticalMovement;
		protected float _time;
		protected Vector3 _currentDirection = Vector3.forward;
		protected bool _accelerating;
		protected bool _decelerating;
		
		protected override void Initialization()
		{
			base.Initialization();
			if (_character.CharacterDimension == Character.CharacterDimensions.Type2D)
			{
				_currentDirection = Vector3.right;
			}
		} 
		
		protected override void HandleInput()
		{
			if (InputAuthorized)
			{
				_horizontalMovement = _horizontalInput;
				_verticalMovement = _verticalInput;
			}
			else
			{
				_horizontalMovement = 0f;
				_verticalMovement = 0f;
			}	
		}
		
		public override void ProcessAbility()
		{
			base.ProcessAbility();
			
			if (!AbilityAuthorized
			    || ((_condition.CurrentState != CharacterStates.CharacterConditions.Normal) && (_condition.CurrentState != CharacterStates.CharacterConditions.ControlledMovement)))
			{
				if (AbilityAuthorized)
				{
					StopAbilityUsedSfx();
				}
				return;
			}
			HandleDirection();
			HandleMovement();
		}

		protected virtual void HandleDirection()
		{
			if (_horizontalMovement > _inputManager.Threshold.x)
			{
				_currentDirection = Quaternion.Euler(0, RotationSpeed * Time.deltaTime, 0) * _currentDirection;
			}
			else if (_horizontalMovement < -_inputManager.Threshold.x)
			{
				_currentDirection = Quaternion.Euler(0, -RotationSpeed * Time.deltaTime, 0) * _currentDirection;
			}	
		}

		protected virtual void HandleMovement()
		{
			bool acceleratingThisFrame = (_verticalMovement > InputManager.Instance.Threshold.y);
			bool deceleratingThisFrame = (_verticalMovement < -InputManager.Instance.Threshold.y);
			bool accelerationChange = false;
			
			if (acceleratingThisFrame)
			{
				_accelerating = true;
				_time += Time.deltaTime;
				float t = Mathf.Clamp01(_time / AccelerationDuration);
				CurrentSpeed = AccelerationCurve.Evaluate(t) * MaximumSpeed;
				if (_decelerating)
				{
					accelerationChange = true;
					_decelerating = false;
				}
			}
			else if (deceleratingThisFrame)
			{
				_time += Time.deltaTime;
				float t = Mathf.Clamp01(_time / DecelerationDuration);
				CurrentSpeed = DecelerationCurve.Evaluate(t) * MaximumSpeed;
				if (_accelerating)
				{
					accelerationChange = true;
					_accelerating = false;
				}
			}

			if (accelerationChange)
			{
				_time = 0f;
			}
			
			_controller.CurrentDirection = _currentDirection;
			_controller.SetMovement(_currentDirection * CurrentSpeed); 
		}
	}    
}

