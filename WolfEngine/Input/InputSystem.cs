using System.Numerics;

namespace WolfEngine.Input;

public interface IInputSystem
{
	void RegisterButton(InputAction action, Action<InputActionCallback<bool>> callback);
	void RegisterAxis1D(InputAction action, Action<InputActionCallback<float>> callback);
	void RegisterAxis2D(InputAction action, Action<InputActionCallback<Vector2>> callback);

	void SetButton(InputActionBinding binding, bool isPressed);
	void SetAxis1D(InputActionBinding binding, float value);
	void SetAxis2D(InputActionBinding binding, Vector2 value);
}

public class InputSystem : IInputSystem
{
	private const float AxisButtonThreshold = 0.5f;
	private const float Axis2DButtonThresholdSquared = AxisButtonThreshold * AxisButtonThreshold;

	private readonly Dictionary<InputActionBinding, bool> _buttonStates = new();
	private readonly Dictionary<InputActionBinding, float> _axis1DStates = new();
	private readonly Dictionary<InputActionBinding, Vector2> _axis2DStates = new();

	private readonly Dictionary<InputActionBinding, List<ButtonRegistration>> _bindingToButtonActions = new();
	private readonly Dictionary<InputActionBinding, List<Axis1DRegistration>> _bindingToAxis1DActions = new();
	private readonly Dictionary<InputActionBinding, List<Axis2DRegistration>> _bindingToAxis2DActions = new();

	public void RegisterButton(InputAction action, Action<InputActionCallback<bool>> callback)
	{
		if (callback == null)
		{
			throw new ArgumentNullException(nameof(callback));
		}

		ValidateAction(action, InputActionType.Button, null);

		var registration = new ButtonRegistration(action, callback);
		foreach (var binding in action.Bindings)
		{
			AddBindingMap(_bindingToButtonActions, binding, registration);
		}
	}

	public void RegisterAxis1D(InputAction action, Action<InputActionCallback<float>> callback)
	{
		if (callback == null)
		{
			throw new ArgumentNullException(nameof(callback));
		}

		ValidateAction(action, InputActionType.Axis1D, BindingKind.Axis1D);

		var registration = new Axis1DRegistration(action, callback);
		foreach (var binding in action.Bindings)
		{
			AddBindingMap(_bindingToAxis1DActions, binding, registration);
			if (_axis1DStates.ContainsKey(binding) == false)
			{
				_axis1DStates[binding] = 0.0f;
			}
		}
	}

	public void RegisterAxis2D(InputAction action, Action<InputActionCallback<Vector2>> callback)
	{
		if (callback == null)
		{
			throw new ArgumentNullException(nameof(callback));
		}

		ValidateAction(action, InputActionType.Axis2D, BindingKind.Axis2D);

		var registration = new Axis2DRegistration(action, callback);
		foreach (var binding in action.Bindings)
		{
			AddBindingMap(_bindingToAxis2DActions, binding, registration);
			if (_axis2DStates.ContainsKey(binding) == false)
			{
				_axis2DStates[binding] = Vector2.Zero;
			}
		}
	}

	public void SetButton(InputActionBinding binding, bool isPressed)
	{
		EnsureBindingKind(binding, BindingKind.Button);

		var previous = _buttonStates.TryGetValue(binding, out var state) && state;
		if (previous == isPressed)
		{
			return;
		}

		_buttonStates[binding] = isPressed;

		if (_bindingToButtonActions.TryGetValue(binding, out var buttonActions))
		{
			foreach (var registration in buttonActions)
			{
				var newValue = EvaluateButton(registration.Action.Bindings);
				if (newValue == registration.CurrentValue)
				{
					continue;
				}

				var callback = new InputActionCallback<bool>(registration.Action, binding, newValue, registration.CurrentValue);
				registration.CurrentValue = newValue;
				registration.Callback(callback);
			}
		}
	}

	public void SetAxis1D(InputActionBinding binding, float value)
	{
		EnsureBindingKind(binding, BindingKind.Axis1D);

		var previous = _axis1DStates.TryGetValue(binding, out var state) ? state : 0.0f;
		if (FloatEquals(previous, value))
		{
			_axis1DStates[binding] = value;
			return;
		}

		_axis1DStates[binding] = value;

		if (_bindingToAxis1DActions.TryGetValue(binding, out var axisActions))
		{
			foreach (var registration in axisActions)
			{
				var newValue = EvaluateAxis1D(registration.Action.Bindings);
				if (FloatEquals(newValue, registration.CurrentValue))
				{
					continue;
				}

				var callback = new InputActionCallback<float>(registration.Action, binding, newValue, registration.CurrentValue);
				registration.CurrentValue = newValue;
				registration.Callback(callback);
			}
		}

		if (_bindingToButtonActions.TryGetValue(binding, out var buttonActions))
		{
			foreach (var registration in buttonActions)
			{
				var newValue = EvaluateButton(registration.Action.Bindings);
				if (newValue == registration.CurrentValue)
				{
					continue;
				}

				var callback = new InputActionCallback<bool>(registration.Action, binding, newValue, registration.CurrentValue);
				registration.CurrentValue = newValue;
				registration.Callback(callback);
			}
		}
	}

	public void SetAxis2D(InputActionBinding binding, Vector2 value)
	{
		EnsureBindingKind(binding, BindingKind.Axis2D);

		var previous = _axis2DStates.TryGetValue(binding, out var state) ? state : Vector2.Zero;
		if (Vector2Equals(previous, value))
		{
			_axis2DStates[binding] = value;
			return;
		}

		_axis2DStates[binding] = value;

		if (_bindingToAxis2DActions.TryGetValue(binding, out var axisActions))
		{
			foreach (var registration in axisActions)
			{
				var newValue = EvaluateAxis2D(registration.Action.Bindings);
				if (Vector2Equals(newValue, registration.CurrentValue))
				{
					continue;
				}

				var callback = new InputActionCallback<Vector2>(registration.Action, binding, newValue, registration.CurrentValue);
				registration.CurrentValue = newValue;
				registration.Callback(callback);
			}
		}

		if (_bindingToButtonActions.TryGetValue(binding, out var buttonActions))
		{
			foreach (var registration in buttonActions)
			{
				var newValue = EvaluateButton(registration.Action.Bindings);
				if (newValue == registration.CurrentValue)
				{
					continue;
				}

				var callback = new InputActionCallback<bool>(registration.Action, binding, newValue, registration.CurrentValue);
				registration.CurrentValue = newValue;
				registration.Callback(callback);
			}
		}
	}

	private bool EvaluateButton(InputActionBinding[] bindings)
	{
		foreach (var binding in bindings)
		{
			switch (GetBindingKind(binding))
			{
				case BindingKind.Button when _buttonStates.TryGetValue(binding, out var isPressed) && isPressed:
					return true;
				case BindingKind.Axis1D when _axis1DStates.TryGetValue(binding, out var axis1D):
					if (Math.Abs(axis1D) >= AxisButtonThreshold)
					{
						return true;
					}
					break;
				case BindingKind.Axis2D when _axis2DStates.TryGetValue(binding, out var axis2D):
					if (axis2D.LengthSquared() >= Axis2DButtonThresholdSquared)
					{
						return true;
					}
					break;
			}
		}

		return false;
	}

	private float EvaluateAxis1D(InputActionBinding[] bindings)
	{
		var bestValue = 0.0f;
		var bestMagnitude = 0.0f;

		foreach (var binding in bindings)
		{
			if (_axis1DStates.TryGetValue(binding, out var value) == false)
			{
				continue;
			}

			var magnitude = Math.Abs(value);
			if (magnitude > bestMagnitude)
			{
				bestMagnitude = magnitude;
				bestValue = value;
			}
		}

		return bestValue;
	}

	private Vector2 EvaluateAxis2D(InputActionBinding[] bindings)
	{
		var bestValue = Vector2.Zero;
		var bestLengthSquared = 0.0f;

		foreach (var binding in bindings)
		{
			if (_axis2DStates.TryGetValue(binding, out var value) == false)
			{
				continue;
			}

			var lengthSquared = value.LengthSquared();
			if (lengthSquared > bestLengthSquared)
			{
				bestLengthSquared = lengthSquared;
				bestValue = value;
			}
		}

		return bestValue;
	}

	private static void AddBindingMap<TRegistration>(Dictionary<InputActionBinding, List<TRegistration>> map, InputActionBinding binding, TRegistration registration)
	{
		if (map.TryGetValue(binding, out var registrations))
		{
			registrations.Add(registration);
			return;
		}

		map[binding] = new List<TRegistration> { registration };
	}

	private static void ValidateAction(InputAction action, InputActionType expectedType, BindingKind? expectedBindingKind)
	{
		if (action.Type != expectedType)
		{
			throw new ArgumentException($"InputAction '{action.Name}' must be registered as {expectedType}.", nameof(action));
		}

		if (action.Bindings == null || action.Bindings.Length == 0)
		{
			throw new ArgumentException($"InputAction '{action.Name}' must declare at least one binding.", nameof(action));
		}

		foreach (var binding in action.Bindings)
		{
			if (binding == InputActionBinding.None)
			{
				throw new ArgumentException($"InputAction '{action.Name}' cannot use the 'None' binding.", nameof(action));
			}

			if (expectedBindingKind.HasValue == false)
			{
				continue;
			}

			var kind = GetBindingKind(binding);
			if (kind != expectedBindingKind.Value)
			{
				throw new ArgumentException($"InputAction '{action.Name}' expects {expectedBindingKind.Value} bindings but found {binding} ({kind}).", nameof(action));
			}
		}
	}

	private static void EnsureBindingKind(InputActionBinding binding, BindingKind expected)
	{
		var kind = GetBindingKind(binding);
		if (kind != expected)
		{
			throw new ArgumentException($"Binding {binding} has kind {kind} but expected {expected}.", nameof(binding));
		}
	}

	private static BindingKind GetBindingKind(InputActionBinding binding) => binding switch
	{
		InputActionBinding.MousePosition => BindingKind.Axis2D,
		InputActionBinding.MouseDelta => BindingKind.Axis2D,
		InputActionBinding.MouseScroll => BindingKind.Axis2D,
		InputActionBinding.GamepadLeftStick => BindingKind.Axis2D,
		InputActionBinding.GamepadRightStick => BindingKind.Axis2D,
		InputActionBinding.GamepadDpad => BindingKind.Axis2D,
		InputActionBinding.GamepadLeftTrigger => BindingKind.Axis1D,
		InputActionBinding.GamepadRightTrigger => BindingKind.Axis1D,
		_ => BindingKind.Button
	};

	private static bool FloatEquals(float a, float b) => Math.Abs(a - b) <= 0.0001f;

	private static bool Vector2Equals(Vector2 a, Vector2 b) => Vector2.DistanceSquared(a, b) <= 0.0001f;

	private sealed class ButtonRegistration
	{
		public ButtonRegistration(InputAction action, Action<InputActionCallback<bool>> callback)
		{
			Action = action;
			Callback = callback;
		}

		public InputAction Action { get; }
		public Action<InputActionCallback<bool>> Callback { get; }
		public bool CurrentValue { get; set; }
	}

	private sealed class Axis1DRegistration
	{
		public Axis1DRegistration(InputAction action, Action<InputActionCallback<float>> callback)
		{
			Action = action;
			Callback = callback;
		}

		public InputAction Action { get; }
		public Action<InputActionCallback<float>> Callback { get; }
		public float CurrentValue { get; set; }
	}

	private sealed class Axis2DRegistration
	{
		public Axis2DRegistration(InputAction action, Action<InputActionCallback<Vector2>> callback)
		{
			Action = action;
			Callback = callback;
		}

		public InputAction Action { get; }
		public Action<InputActionCallback<Vector2>> Callback { get; }
		public Vector2 CurrentValue { get; set; }
	}

	private enum BindingKind
	{
		Button,
		Axis1D,
		Axis2D
	}
}
