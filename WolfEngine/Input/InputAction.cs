using System.Collections.Generic;

namespace WolfEngine.Input;

public struct InputAction
{
	public string Name;
	public InputActionType Type;
	public InputActionBinding[] Bindings;
}

public enum InputActionType
{
	Button, Axis1D, Axis2D
}

public readonly struct InputActionCallback<T>
{
	public InputActionCallback(InputAction action, InputActionBinding binding, T value, T previousValue)
	{
		Action = action;
		Binding = binding;
		Value = value;
		PreviousValue = previousValue;
	}

	public InputAction Action { get; }
	public InputActionBinding Binding { get; }
	public T Value { get; }
	public T PreviousValue { get; }
	public bool Changed => EqualityComparer<T>.Default.Equals(Value, PreviousValue) == false;
}
