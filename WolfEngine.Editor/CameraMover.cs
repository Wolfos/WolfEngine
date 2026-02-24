using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Input;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor;

public struct CameraMover : IEntityComponent
{
	public float MoveSpeed;
	public float LookSensitivity;
	public float Yaw;
	public float Pitch;
	public bool Initialized;
}

public class CameraMoverSystem: IUpdateable
{
	private readonly IInputSystem _inputSystem;
	private readonly EditorViewportStateBus _viewportStateBus;
	private Vector3 _moveInput;
	private bool _speedBoost;
	private bool _isLooking;
	private Vector2 _lookDelta;
	private bool _hadViewportControl;

	public CameraMoverSystem(IInputSystem inputSystem, EditorViewportStateBus viewportStateBus)
	{
		_inputSystem = inputSystem;
		_viewportStateBus = viewportStateBus ?? throw new ArgumentNullException(nameof(viewportStateBus));

		inputSystem.RegisterButton(NewAction("MoveForward", InputActionBinding.KeyW), OnMoveForward);
		inputSystem.RegisterButton(NewAction("MoveBack", InputActionBinding.KeyS), OnMoveBack);
		inputSystem.RegisterButton(NewAction("MoveLeft", InputActionBinding.KeyA), OnMoveLeft);
		inputSystem.RegisterButton(NewAction("MoveRight", InputActionBinding.KeyD), OnMoveRight);
		inputSystem.RegisterButton(NewAction("MoveUp", InputActionBinding.KeyE), OnMoveUp);
		inputSystem.RegisterButton(NewAction("MoveDown", InputActionBinding.KeyQ), OnMoveDown);
		inputSystem.RegisterButton(NewAction("SpeedBoost", InputActionBinding.KeyLeftShift), OnSpeedUp);
		inputSystem.RegisterButton(NewAction("LookEnable", InputActionBinding.MouseButtonRight), OnLookButton);
		inputSystem.RegisterAxis2D(NewAxis2DAction("LookDelta", InputActionBinding.MouseDelta), OnLookDelta);
	}

	private static InputAction NewAction(string name, InputActionBinding binding)
	{
		return new InputAction
		{
			Name = name,
			Type = InputActionType.Button,
			Bindings = new[] { binding }
		};
	}

	private static InputAction NewAxis2DAction(string name, InputActionBinding binding)
	{
		return new InputAction
		{
			Name = name,
			Type = InputActionType.Axis2D,
			Bindings = new[] { binding }
		};
	}

	private void OnMoveForward(InputActionCallback<bool> callback) => ApplyMoveDelta(new Vector3(0, 0, 1), callback.Value);
	private void OnMoveLeft(InputActionCallback<bool> callback) => ApplyMoveDelta(new Vector3(-1, 0, 0), callback.Value);
	private void OnMoveRight(InputActionCallback<bool> callback) => ApplyMoveDelta(new Vector3(1, 0, 0), callback.Value);
	private void OnMoveBack(InputActionCallback<bool> callback) => ApplyMoveDelta(new Vector3(0, 0, -1), callback.Value);
	private void OnMoveUp(InputActionCallback<bool> callback) => ApplyMoveDelta(new Vector3(0, 1, 0), callback.Value);
	private void OnMoveDown(InputActionCallback<bool> callback) => ApplyMoveDelta(new Vector3(0, -1, 0), callback.Value);
	private void OnSpeedUp(InputActionCallback<bool> callback) => _speedBoost = callback.Value;
	private void OnLookButton(InputActionCallback<bool> callback) => _isLooking = callback.Value;

	private void OnLookDelta(InputActionCallback<Vector2> callback)
	{
		if (_isLooking == false)
		{
			return;
		}

		_lookDelta += callback.Value;
	}

	private void ApplyMoveDelta(Vector3 direction, bool isPressed)
	{
		// Accumulate movement intent; releases subtract the previously added direction.
		_moveInput += direction * (isPressed ? 1.0f : -1.0f);
	}

	public void Update(float deltaTime, World world)
	{
		var viewportState = _viewportStateBus.GetUiState();
		var viewportControlActive = viewportState.Visible && (viewportState.Hovered || viewportState.Focused) && _isLooking;
		if (viewportControlActive == false)
		{
			if (_hadViewportControl)
			{
				_moveInput = Vector3.Zero;
				_lookDelta = Vector2.Zero;
			}

			_hadViewportControl = false;
		}
		else
		{
			_hadViewportControl = true;
		}

		foreach (var entry in world.View<LocalTransform, CameraMover>())
		{
			ref var transform = ref entry.First;
			ref var mover = ref entry.Second;

			EnsureDefaults(ref mover);
			EnsureOrientationFromTransform(ref mover, transform);

			if (viewportControlActive && _lookDelta != Vector2.Zero)
			{
				mover.Yaw += _lookDelta.X * mover.LookSensitivity;
				mover.Pitch += _lookDelta.Y * mover.LookSensitivity;
				mover.Pitch = Math.Clamp(mover.Pitch, -1.55f, 1.55f);
			}

			var rotation = Quaternion.CreateFromYawPitchRoll(mover.Yaw, mover.Pitch, 0.0f);
			world.SetLocalRotation(entry.Entity, rotation);

			var forward = Vector3.Transform(Vector3.UnitZ, rotation);
			var right = Vector3.Transform(Vector3.UnitX, rotation);
			var up = Vector3.Transform(Vector3.UnitY, rotation);

			var moveInput = viewportControlActive ? _moveInput : Vector3.Zero;
			var move = right * moveInput.X + up * moveInput.Y + forward * moveInput.Z;
			var speed = mover.MoveSpeed * (_speedBoost ? 2.0f : 1.0f);
			
			world.Translate(entry.Entity, move * speed * deltaTime, true);
		}

		_lookDelta = Vector2.Zero;
	}

	public WorldTag GetTag() => WorldTag.Editor;

	private static void EnsureDefaults(ref CameraMover mover)
	{
		if (mover.MoveSpeed <= 0.0f)
		{
			mover.MoveSpeed = 5.0f;
		}

		if (mover.LookSensitivity <= 0.0f)
		{
			mover.LookSensitivity = 0.0025f;
		}
	}

	private static void EnsureOrientationFromTransform(ref CameraMover mover, LocalTransform localTransform)
	{
		if (mover.Initialized)
		{
			return;
		}

		var forward = Vector3.Transform(Vector3.UnitZ, localTransform.LocalRotation);
		if (forward != Vector3.Zero)
		{
			forward = Vector3.Normalize(forward);
			mover.Yaw = MathF.Atan2(forward.X, forward.Z);
			mover.Pitch = MathF.Asin(Math.Clamp(forward.Y, -1.0f, 1.0f));
		}

		mover.Initialized = true;
	}
}
