using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Input;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor;

[EditorOnly]
public struct EditorCameraMover : IEntityComponent
{
	public RenderViewId View;
	public float MoveSpeed;
	public float LookSensitivity;
	public float Yaw;
	public float Pitch;
	public bool Initialized;
}

public class EditorCameraSystem: IUpdate
{
	private readonly IInputSystem _inputSystem;
	private readonly EditorViewportStateBus _viewportStateBus;
	private readonly object _inputSync = new();
	private readonly Dictionary<RenderViewId, CameraPose> _cameraPoses = new();
	private bool _moveForwardHeld;
	private bool _moveBackHeld;
	private bool _moveLeftHeld;
	private bool _moveRightHeld;
	private bool _moveUpHeld;
	private bool _moveDownHeld;
	private bool _speedBoost;
	private bool _isLooking;
	private Vector2 _lookDelta;
	private RenderViewId _activeInputView = RenderViewId.None;
	private Vector2 _frameLookDelta;
	private Vector3 _frameMoveInput;
	private bool _frameSpeedBoost;

	private readonly record struct CameraPose(World World, Entity Entity, Vector3 Position, Vector3 Forward);

	public EditorCameraSystem(IInputSystem inputSystem, EditorViewportStateBus viewportStateBus)
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

	private void OnMoveForward(InputActionCallback<bool> callback) { lock (_inputSync) _moveForwardHeld = callback.Value; }
	private void OnMoveLeft(InputActionCallback<bool> callback) { lock (_inputSync) _moveLeftHeld = callback.Value; }
	private void OnMoveRight(InputActionCallback<bool> callback) { lock (_inputSync) _moveRightHeld = callback.Value; }
	private void OnMoveBack(InputActionCallback<bool> callback) { lock (_inputSync) _moveBackHeld = callback.Value; }
	private void OnMoveUp(InputActionCallback<bool> callback) { lock (_inputSync) _moveUpHeld = callback.Value; }
	private void OnMoveDown(InputActionCallback<bool> callback) { lock (_inputSync) _moveDownHeld = callback.Value; }
	private void OnSpeedUp(InputActionCallback<bool> callback) { lock (_inputSync) _speedBoost = callback.Value; }
	private void OnLookButton(InputActionCallback<bool> callback) { lock (_inputSync) _isLooking = callback.Value; }

	private void OnLookDelta(InputActionCallback<Vector2> callback)
	{
		lock (_inputSync)
		{
			if (_isLooking)
			{
				_lookDelta += callback.Value;
			}
		}
	}

	/// <summary>Routes this frame's input once before the world manager visits any editor worlds.</summary>
	public void BeginFrame()
	{
		bool looking;
		lock (_inputSync) looking = _isLooking;

		var inputView = RenderViewId.None;
		if (looking && !_viewportStateBus.IsGizmoDragging())
		{
			foreach (var view in _viewportStateBus.GetViews())
			{
				var state = _viewportStateBus.GetUiState(view);
				if (state.Visible && state.RightMousePressStartedHere)
				{
					inputView = view;
					if (state.Focused) break;
				}
			}
		}

		lock (_inputSync)
		{
			_frameLookDelta = inputView.IsValid ? _lookDelta : Vector2.Zero;
			_lookDelta = Vector2.Zero;
			_frameMoveInput = inputView.IsValid ? GetMoveInput() : Vector3.Zero;
			_frameSpeedBoost = inputView.IsValid && _speedBoost;
			_activeInputView = inputView;
		}
	}

	public void Update(float deltaTime, World world)
	{
		foreach (var entry in world.View<LocalTransform, EditorCameraMover>())
		{
			ref var transform = ref entry.First;
			ref var mover = ref entry.Second;
			var view = mover.View.IsValid ? mover.View : RenderViewId.Primary;
			var viewportControlActive = view == _activeInputView;

			EnsureDefaults(ref mover);
			EnsureOrientationFromTransform(ref mover, transform);

			if (viewportControlActive && _frameLookDelta != Vector2.Zero)
			{
				mover.Yaw += _frameLookDelta.X * mover.LookSensitivity;
				mover.Pitch += _frameLookDelta.Y * mover.LookSensitivity;
				mover.Pitch = Math.Clamp(mover.Pitch, -1.55f, 1.55f);
			}

			var rotation = Quaternion.CreateFromYawPitchRoll(mover.Yaw, mover.Pitch, 0.0f);
			world.SetLocalRotation(entry.Entity, rotation);

			var forward = Vector3.Transform(Vector3.UnitZ, rotation);
			var right = Vector3.Transform(Vector3.UnitX, rotation);
			var up = Vector3.Transform(Vector3.UnitY, rotation);

			var moveInput = viewportControlActive ? _frameMoveInput : Vector3.Zero;
			var move = right * moveInput.X + up * moveInput.Y + forward * moveInput.Z;
			var speed = mover.MoveSpeed * (_frameSpeedBoost ? 2.0f : 1.0f);
			
			world.Translate(entry.Entity, move * speed * deltaTime, true);
			_cameraPoses[view] = new CameraPose(world, entry.Entity, transform.LocalPosition, forward);
		}
	}

	public bool TryGetCameraPose(out Vector3 position, out Vector3 forward) =>
		TryGetCameraPose(RenderViewId.Primary, out position, out forward);

	public bool TryGetCameraPose(RenderViewId view, out Vector3 position, out Vector3 forward)
	{
		if (_cameraPoses.TryGetValue(view, out var pose) && pose.World.IsAlive(pose.Entity))
		{
			position = pose.Position;
			forward = pose.Forward;
			return true;
		}
		position = default;
		forward = Vector3.UnitZ;
		return false;
	}

	/// <summary>Frames the camera backing a view without changing another view's input or pose.</summary>
	public bool SetCameraPose(RenderViewId view, Vector3 position, Vector3 forward)
	{
		if (!_cameraPoses.TryGetValue(view, out var pose) ||
		    !pose.World.IsAlive(pose.Entity) ||
		    !pose.World.HasComponent<EditorCameraMover>(pose.Entity) ||
		    !float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z) ||
		    !float.IsFinite(forward.X) || !float.IsFinite(forward.Y) || !float.IsFinite(forward.Z) ||
		    forward.LengthSquared() < 1e-8f)
		{
			return false;
		}

		forward = Vector3.Normalize(forward);
		ref var mover = ref pose.World.GetComponent<EditorCameraMover>(pose.Entity);
		mover.Yaw = MathF.Atan2(forward.X, forward.Z);
		mover.Pitch = -MathF.Asin(Math.Clamp(forward.Y, -1.0f, 1.0f));
		mover.Initialized = true;
		pose.World.SetLocalPosition(pose.Entity, position);
		pose.World.SetLocalRotation(pose.Entity, Quaternion.CreateFromYawPitchRoll(mover.Yaw, mover.Pitch, 0.0f));
		_cameraPoses[view] = pose with { Position = position, Forward = forward };
		return true;
	}

	public WorldTag GetTag() => WorldTag.Editor;

	private Vector3 GetMoveInput()
	{
		return new Vector3(
			(_moveRightHeld ? 1.0f : 0.0f) - (_moveLeftHeld ? 1.0f : 0.0f),
			(_moveUpHeld ? 1.0f : 0.0f) - (_moveDownHeld ? 1.0f : 0.0f),
			(_moveForwardHeld ? 1.0f : 0.0f) - (_moveBackHeld ? 1.0f : 0.0f));
	}

	private static void EnsureDefaults(ref EditorCameraMover mover)
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

	private static void EnsureOrientationFromTransform(ref EditorCameraMover mover, LocalTransform localTransform)
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
			mover.Pitch = -MathF.Asin(Math.Clamp(forward.Y, -1.0f, 1.0f));
		}

		mover.Initialized = true;
	}
}
