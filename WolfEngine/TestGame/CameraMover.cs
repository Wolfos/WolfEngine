using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Input;

namespace WolfEngine.TestGame;

public struct CameraMover: IEntityComponent
{
	
}

public class CameraMoverSystem
{
	private readonly IInputSystem _inputSystem;
	private readonly World _world;
	private Vector3 _moveInput;
	private bool _speedBoost;

	public CameraMoverSystem(IInputSystem inputSystem, World world)
	{
		_inputSystem = inputSystem;
		_world = world;

		inputSystem.RegisterButton(NewAction("MoveForward", InputActionBinding.KeyW), OnMoveForward);
		inputSystem.RegisterButton(NewAction("MoveBack", InputActionBinding.KeyS), OnMoveBack);
		inputSystem.RegisterButton(NewAction("MoveLeft", InputActionBinding.KeyA), OnMoveLeft);
		inputSystem.RegisterButton(NewAction("MoveRight", InputActionBinding.KeyD), OnMoveRight);
		inputSystem.RegisterButton(NewAction("MoveUp", InputActionBinding.KeyE), OnMoveUp);
		inputSystem.RegisterButton(NewAction("MoveDown", InputActionBinding.KeyQ), OnMoveDown);
		inputSystem.RegisterButton(NewAction("SpeedBoost", InputActionBinding.KeyLeftShift), OnSpeedUp);
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

	private void OnMoveForward(InputActionCallback<bool> callback)
	{
		ApplyMoveDelta(new Vector3(0, 0, 1), callback.Value);
	}
	
	private void OnMoveLeft(InputActionCallback<bool> callback)
	{
		ApplyMoveDelta(new Vector3(-1, 0, 0), callback.Value);
	}
	
	private void OnMoveRight(InputActionCallback<bool> callback)
	{
		ApplyMoveDelta(new Vector3(1, 0, 0), callback.Value);
	}
	
	private void OnMoveBack(InputActionCallback<bool> callback)
	{
		ApplyMoveDelta(new Vector3(0, 0, -1), callback.Value);
	}
	
	private void OnMoveUp(InputActionCallback<bool> callback)
	{
		ApplyMoveDelta(new Vector3(0, 1, 0), callback.Value);
	}
	
	private void OnMoveDown(InputActionCallback<bool> callback)
	{
		ApplyMoveDelta(new Vector3(0, -1, 0), callback.Value);
	}

	private void OnSpeedUp(InputActionCallback<bool> callback)
	{
		_speedBoost = callback.Value;
	}

	private void ApplyMoveDelta(Vector3 direction, bool isPressed)
	{
		// Accumulate movement intent; releases subtract the previously added direction.
		_moveInput += direction * (isPressed ? 1.0f : -1.0f);
	}

	public void Update(float deltaTime)
	{
		foreach (var entry in _world.View<Transform, CameraMover>())
		{
			ref var transform = ref entry.First;
			ref var mover = ref entry.Second;

			transform.LocalPosition += _moveInput * deltaTime * (_speedBoost ? 2 : 1);
		}
	}
}
