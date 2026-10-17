using System.Numerics;

namespace WolfEngine.Input;

public readonly record struct PointerInputContext(bool Enabled, bool Available, bool Focused, Vector2 Origin, Vector2 Size);
public readonly record struct PointerInputEvent(InputActionBinding Binding, Vector2 Value, bool Pressed, Vector2 Position, long Buttons,
	bool ShiftKey = false, bool CtrlKey = false, bool AltKey = false, bool MetaKey = false, bool Cancelled = false);

/// <summary>Invoked by the update thread, before forwarding mouse bindings to gameplay.</summary>
public interface IPointerInputRouter
{
	void BeginFrame(PointerInputContext context);
	bool Route(PointerInputEvent input);
	void EndFrame();
}

public sealed class NullPointerInputRouter : IPointerInputRouter
{
	public static NullPointerInputRouter Instance { get; } = new();
	public void BeginFrame(PointerInputContext context) { }
	public bool Route(PointerInputEvent input) => false;
	public void EndFrame() { }
}

/// <summary>Producer-side raw state and ordered mouse events; ImGui receives its own independent copy.</summary>
public sealed class PointerInputQueue
{
	private readonly object _sync = new();
	private List<PointerInputEvent> _pending = [];
	private List<PointerInputEvent> _draining = [];
	private Vector2 _position;
	private bool _hasPosition;
	private long _buttons;
	private readonly HashSet<InputActionBinding> _modifiers = [];
	private volatile bool _focused = true;
	public bool Focused => _focused;
	public void SetFocus(bool focused)
	{
		lock (_sync)
		{
			if (_focused == focused) return;
			_focused = focused;
			if (!focused)
			{
				_modifiers.Clear();
				_pending.Add(new(InputActionBinding.None, default, false, _position, _buttons, Cancelled: true));
				for (var i = 0; i < 5; i++)
				{
					var mask = i switch { 0 => 1L, 1 => 4L, 2 => 2L, _ => 1L << i };
					if ((_buttons & mask) == 0) continue;
					_buttons &= ~mask;
					var binding = i switch { 0 => InputActionBinding.MouseButtonLeft, 1 => InputActionBinding.MouseButtonMiddle, 2 => InputActionBinding.MouseButtonRight, 3 => InputActionBinding.MouseButton4, _ => InputActionBinding.MouseButton5 };
					_pending.Add(Event(binding, default, false));
				}
			}
		}
	}
	public void SetModifier(InputActionBinding binding, bool pressed)
	{
		if (binding is not (InputActionBinding.KeyLeftShift or InputActionBinding.KeyRightShift or InputActionBinding.KeyLeftControl or InputActionBinding.KeyRightControl or InputActionBinding.KeyLeftAlt or InputActionBinding.KeyRightAlt or InputActionBinding.KeyLeftSuper or InputActionBinding.KeyRightSuper)) return;
		lock (_sync) { if (pressed) _modifiers.Add(binding); else _modifiers.Remove(binding); }
	}
	private PointerInputEvent Event(InputActionBinding binding, Vector2 value, bool pressed) => new(binding, value, pressed, _position, _buttons,
		_modifiers.Contains(InputActionBinding.KeyLeftShift) || _modifiers.Contains(InputActionBinding.KeyRightShift),
		_modifiers.Contains(InputActionBinding.KeyLeftControl) || _modifiers.Contains(InputActionBinding.KeyRightControl),
		_modifiers.Contains(InputActionBinding.KeyLeftAlt) || _modifiers.Contains(InputActionBinding.KeyRightAlt),
		_modifiers.Contains(InputActionBinding.KeyLeftSuper) || _modifiers.Contains(InputActionBinding.KeyRightSuper));
	public static int Button(InputActionBinding binding) => binding switch
	{
		InputActionBinding.MouseButtonLeft => 0, InputActionBinding.MouseButtonMiddle => 1,
		InputActionBinding.MouseButtonRight => 2, InputActionBinding.MouseButton4 => 3,
		InputActionBinding.MouseButton5 => 4, _ => -1
	};
	public bool EnqueueButton(InputActionBinding binding, bool pressed)
	{
		var button = Button(binding); if (button < 0) return false;
		lock (_sync)
		{
			var mask = button switch { 0 => 1L, 1 => 4L, 2 => 2L, _ => 1L << button };
			if (((_buttons & mask) != 0) == pressed) return true;
			_buttons = pressed ? _buttons | mask : _buttons & ~mask;
			_pending.Add(Event(binding, default, pressed));
		}
		return true;
	}
	public bool EnqueueAxis(InputActionBinding binding, Vector2 value)
	{
		if (binding is not (InputActionBinding.MousePosition or InputActionBinding.MouseDelta or InputActionBinding.MouseScroll)) return false;
		lock (_sync)
		{
			if (binding == InputActionBinding.MousePosition)
			{
				_hasPosition = true;
				if (_position == value) return true;
				_position = value;
			}
			var next = Event(binding, value, false);
			// Native motion supplies position and delta separately. Coalesce both within a
			// contiguous motion run, never across a button, scroll, or focus transition.
			for (var i = _pending.Count - 1; i >= 0; i--)
			{
				var previous = _pending[i];
				if (previous.Binding == binding)
				{
					_pending[i] = binding == InputActionBinding.MousePosition ? next : next with { Value = previous.Value + value }; return true;
				}
				if (binding == InputActionBinding.MouseScroll || previous.Binding is not (InputActionBinding.MousePosition or InputActionBinding.MouseDelta)) break;
			}
			_pending.Add(next);
		}
		return true;
	}
	internal bool TryGetMousePosition(out Vector2 position)
	{
		lock (_sync)
		{
			position = _position;
			return _hasPosition;
		}
	}
	internal List<PointerInputEvent> Drain()
	{
		lock (_sync) { (_pending, _draining) = (_draining, _pending); }
		return _draining;
	}
}
