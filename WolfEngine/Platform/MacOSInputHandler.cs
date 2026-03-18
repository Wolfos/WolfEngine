using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using ImGuiNET;
using Silk.NET.SDL;
using WolfEngine.Input;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Platform;

internal interface IMacOSInputHandler
{
	internal void HandleInputEvents(ref Event @event);
}

public class MacOsInputHandler: IMacOSInputHandler
{
	private readonly IInputSystem _inputSystem;
	private readonly IImGuiInputSink _imguiInputSink;
	
	private bool _hasMousePosition;

	public MacOsInputHandler(IInputSystem inputSystem, IImGuiInputSink imguiInputSink)
	{
		_inputSystem = inputSystem;
		_imguiInputSink = imguiInputSink;
	}

	public void HandleInputEvents(ref Event @event)
	{
		switch ((EventType)@event.Type)
		{
			case EventType.Keydown:
				HandleKeyDown(@event.Key);
				break;
			case EventType.Keyup:
				HandleKeyUp(@event.Key);
				break;
			case EventType.Textinput:
				HandleTextInput(@event.Text);
				break;
			case EventType.Mousemotion:
				HandleMouseMotion(@event.Motion);
				break;
			case EventType.Mousebuttondown:
				HandleMouseButton(@event.Button, true);
				break;
			case EventType.Mousebuttonup:
				HandleMouseButton(@event.Button, false);
				break;
			case EventType.Mousewheel:
				HandleMouseWheel(@event.Wheel);
				break;
		}
	}

	private void HandleKeyDown(KeyboardEvent keyEvent)
	{
		var scancode = keyEvent.Keysym.Scancode;
		if (TryMapKey(scancode, out var binding))
		{
			_inputSystem.SetButton(binding, true);
		}

		if (TryConvertKey(scancode, out var imguiKey))
		{
			_imguiInputSink.SetKey(imguiKey, true);
		}
	}

	private void HandleKeyUp(KeyboardEvent keyEvent)
	{
		var scancode = keyEvent.Keysym.Scancode;
		if (TryMapKey(scancode, out var binding))
		{
			_inputSystem.SetButton(binding, false);
		}

		if (TryConvertKey(scancode, out var imguiKey))
		{
			_imguiInputSink.SetKey(imguiKey, false);
		}
	}

	private void HandleTextInput(TextInputEvent textEvent)
	{
		const int textSize = 32;
		unsafe
		{
			var textPtr = (byte*)Unsafe.AsPointer(ref textEvent.Text[0]);
			var span = new ReadOnlySpan<byte>(textPtr, textSize);
			var terminator = span.IndexOf((byte)0);
			if (terminator >= 0)
			{
				span = span[..terminator];
			}

			if (span.IsEmpty)
			{
				return;
			}

			var text = Encoding.UTF8.GetString(span);
			for (var i = 0; i < text.Length; i++)
			{
				_imguiInputSink.AddChar(text[i]);
			}
		}
	}

	private void HandleMouseMotion(MouseMotionEvent motionEvent)
	{
		var position = new Vector2(motionEvent.X, motionEvent.Y);
		_inputSystem.SetAxis2D(InputActionBinding.MousePosition, position);
		_imguiInputSink.SetMousePosition(position);

		var delta = new Vector2(motionEvent.Xrel, motionEvent.Yrel);
		if (_hasMousePosition || delta != Vector2.Zero)
		{
			_inputSystem.SetAxis2D(InputActionBinding.MouseDelta, delta);
		}

		_hasMousePosition = true;
	}

	private void HandleMouseButton(MouseButtonEvent buttonEvent, bool isDown)
	{
		if (TryMapMouseButton(buttonEvent.Button, out var binding))
		{
			_inputSystem.SetButton(binding, isDown);
		}

		var imguiIndex = buttonEvent.Button switch
		{
			Sdl.ButtonLeft => 0,
			Sdl.ButtonRight => 1,
			Sdl.ButtonMiddle => 2,
			Sdl.ButtonX1 => 3,
			Sdl.ButtonX2 => 4,
			_ => -1
		};
		if (imguiIndex >= 0)
		{
			_imguiInputSink.SetMouseButton(imguiIndex, isDown);
		}
	}

	private void HandleMouseWheel(MouseWheelEvent wheelEvent)
	{
		var scroll = new Vector2(wheelEvent.PreciseX, wheelEvent.PreciseY);
		if (scroll == Vector2.Zero)
		{
			scroll = new Vector2(wheelEvent.X, wheelEvent.Y);
		}

		var direction = (MouseWheelDirection)wheelEvent.Direction;
		if (direction == MouseWheelDirection.Flipped)
		{
			scroll = -scroll;
		}

		_inputSystem.SetAxis2D(InputActionBinding.MouseScroll, scroll);
		_imguiInputSink.AddMouseScroll(scroll);
	}

	private static bool TryMapMouseButton(byte button, out InputActionBinding binding)
	{
		binding = button switch
		{
			Sdl.ButtonLeft => InputActionBinding.MouseButtonLeft,
			Sdl.ButtonRight => InputActionBinding.MouseButtonRight,
			Sdl.ButtonMiddle => InputActionBinding.MouseButtonMiddle,
			Sdl.ButtonX1 => InputActionBinding.MouseButton4,
			Sdl.ButtonX2 => InputActionBinding.MouseButton5,
			_ => InputActionBinding.None
		};

		return binding != InputActionBinding.None;
	}

	private static bool TryMapKey(Scancode scancode, out InputActionBinding binding)
	{
		binding = scancode switch
		{
			Scancode.ScancodeA => InputActionBinding.KeyA,
			Scancode.ScancodeB => InputActionBinding.KeyB,
			Scancode.ScancodeC => InputActionBinding.KeyC,
			Scancode.ScancodeD => InputActionBinding.KeyD,
			Scancode.ScancodeE => InputActionBinding.KeyE,
			Scancode.ScancodeF => InputActionBinding.KeyF,
			Scancode.ScancodeG => InputActionBinding.KeyG,
			Scancode.ScancodeH => InputActionBinding.KeyH,
			Scancode.ScancodeI => InputActionBinding.KeyI,
			Scancode.ScancodeJ => InputActionBinding.KeyJ,
			Scancode.ScancodeK => InputActionBinding.KeyK,
			Scancode.ScancodeL => InputActionBinding.KeyL,
			Scancode.ScancodeM => InputActionBinding.KeyM,
			Scancode.ScancodeN => InputActionBinding.KeyN,
			Scancode.ScancodeO => InputActionBinding.KeyO,
			Scancode.ScancodeP => InputActionBinding.KeyP,
			Scancode.ScancodeQ => InputActionBinding.KeyQ,
			Scancode.ScancodeR => InputActionBinding.KeyR,
			Scancode.ScancodeS => InputActionBinding.KeyS,
			Scancode.ScancodeT => InputActionBinding.KeyT,
			Scancode.ScancodeU => InputActionBinding.KeyU,
			Scancode.ScancodeV => InputActionBinding.KeyV,
			Scancode.ScancodeW => InputActionBinding.KeyW,
			Scancode.ScancodeX => InputActionBinding.KeyX,
			Scancode.ScancodeY => InputActionBinding.KeyY,
			Scancode.ScancodeZ => InputActionBinding.KeyZ,
			Scancode.Scancode0 => InputActionBinding.Key0,
			Scancode.Scancode1 => InputActionBinding.Key1,
			Scancode.Scancode2 => InputActionBinding.Key2,
			Scancode.Scancode3 => InputActionBinding.Key3,
			Scancode.Scancode4 => InputActionBinding.Key4,
			Scancode.Scancode5 => InputActionBinding.Key5,
			Scancode.Scancode6 => InputActionBinding.Key6,
			Scancode.Scancode7 => InputActionBinding.Key7,
			Scancode.Scancode8 => InputActionBinding.Key8,
			Scancode.Scancode9 => InputActionBinding.Key9,
			Scancode.ScancodeF1 => InputActionBinding.KeyF1,
			Scancode.ScancodeF2 => InputActionBinding.KeyF2,
			Scancode.ScancodeF3 => InputActionBinding.KeyF3,
			Scancode.ScancodeF4 => InputActionBinding.KeyF4,
			Scancode.ScancodeF5 => InputActionBinding.KeyF5,
			Scancode.ScancodeF6 => InputActionBinding.KeyF6,
			Scancode.ScancodeF7 => InputActionBinding.KeyF7,
			Scancode.ScancodeF8 => InputActionBinding.KeyF8,
			Scancode.ScancodeF9 => InputActionBinding.KeyF9,
			Scancode.ScancodeF10 => InputActionBinding.KeyF10,
			Scancode.ScancodeF11 => InputActionBinding.KeyF11,
			Scancode.ScancodeF12 => InputActionBinding.KeyF12,
			Scancode.ScancodeEscape => InputActionBinding.KeyEscape,
			Scancode.ScancodeTab => InputActionBinding.KeyTab,
			Scancode.ScancodeCapslock => InputActionBinding.KeyCapsLock,
			Scancode.ScancodeLshift => InputActionBinding.KeyLeftShift,
			Scancode.ScancodeRshift => InputActionBinding.KeyRightShift,
			Scancode.ScancodeLctrl => InputActionBinding.KeyLeftControl,
			Scancode.ScancodeRctrl => InputActionBinding.KeyRightControl,
			Scancode.ScancodeLalt => InputActionBinding.KeyLeftAlt,
			Scancode.ScancodeRalt => InputActionBinding.KeyRightAlt,
			Scancode.ScancodeLgui => InputActionBinding.KeyLeftSuper,
			Scancode.ScancodeRgui => InputActionBinding.KeyRightSuper,
			Scancode.ScancodeMenu => InputActionBinding.KeyMenu,
			Scancode.ScancodeSpace => InputActionBinding.KeySpace,
			Scancode.ScancodeReturn => InputActionBinding.KeyEnter,
			Scancode.ScancodeBackspace => InputActionBinding.KeyBackspace,
			Scancode.ScancodeInsert => InputActionBinding.KeyInsert,
			Scancode.ScancodeDelete => InputActionBinding.KeyDelete,
			Scancode.ScancodeHome => InputActionBinding.KeyHome,
			Scancode.ScancodeEnd => InputActionBinding.KeyEnd,
			Scancode.ScancodePageup => InputActionBinding.KeyPageUp,
			Scancode.ScancodePagedown => InputActionBinding.KeyPageDown,
			Scancode.ScancodeUp => InputActionBinding.KeyArrowUp,
			Scancode.ScancodeDown => InputActionBinding.KeyArrowDown,
			Scancode.ScancodeLeft => InputActionBinding.KeyArrowLeft,
			Scancode.ScancodeRight => InputActionBinding.KeyArrowRight,
			Scancode.ScancodeMinus => InputActionBinding.KeyMinus,
			Scancode.ScancodeEquals => InputActionBinding.KeyEquals,
			Scancode.ScancodeLeftbracket => InputActionBinding.KeyLeftBracket,
			Scancode.ScancodeRightbracket => InputActionBinding.KeyRightBracket,
			Scancode.ScancodeBackslash => InputActionBinding.KeyBackslash,
			Scancode.ScancodeSemicolon => InputActionBinding.KeySemicolon,
			Scancode.ScancodeApostrophe => InputActionBinding.KeyApostrophe,
			Scancode.ScancodeGrave => InputActionBinding.KeyGrave,
			Scancode.ScancodeComma => InputActionBinding.KeyComma,
			Scancode.ScancodePeriod => InputActionBinding.KeyPeriod,
			Scancode.ScancodeSlash => InputActionBinding.KeySlash,
			Scancode.ScancodePrintscreen => InputActionBinding.KeyPrintScreen,
			Scancode.ScancodeScrolllock => InputActionBinding.KeyScrollLock,
			Scancode.ScancodePause => InputActionBinding.KeyPause,
			Scancode.ScancodeNumlockclear => InputActionBinding.KeyNumLock,
			Scancode.ScancodeKP0 => InputActionBinding.KeyNumpad0,
			Scancode.ScancodeKP1 => InputActionBinding.KeyNumpad1,
			Scancode.ScancodeKP2 => InputActionBinding.KeyNumpad2,
			Scancode.ScancodeKP3 => InputActionBinding.KeyNumpad3,
			Scancode.ScancodeKP4 => InputActionBinding.KeyNumpad4,
			Scancode.ScancodeKP5 => InputActionBinding.KeyNumpad5,
			Scancode.ScancodeKP6 => InputActionBinding.KeyNumpad6,
			Scancode.ScancodeKP7 => InputActionBinding.KeyNumpad7,
			Scancode.ScancodeKP8 => InputActionBinding.KeyNumpad8,
			Scancode.ScancodeKP9 => InputActionBinding.KeyNumpad9,
			Scancode.ScancodeKPDivide => InputActionBinding.KeyNumpadDivide,
			Scancode.ScancodeKPMultiply => InputActionBinding.KeyNumpadMultiply,
			Scancode.ScancodeKPMinus => InputActionBinding.KeyNumpadSubtract,
			Scancode.ScancodeKPPlus => InputActionBinding.KeyNumpadAdd,
			Scancode.ScancodeKPPeriod => InputActionBinding.KeyNumpadDecimal,
			Scancode.ScancodeKPEnter => InputActionBinding.KeyNumpadEnter,
			_ => InputActionBinding.None
		};

		return binding != InputActionBinding.None;
	}

	private static bool TryConvertKey(Scancode scancode, out ImGuiKey imguiKey)
	{
		imguiKey = scancode switch
		{
			Scancode.ScancodeTab => ImGuiKey.Tab,
			Scancode.ScancodeLshift => ImGuiKey.LeftShift,
			Scancode.ScancodeRshift => ImGuiKey.RightShift,
			Scancode.ScancodeLctrl => ImGuiKey.LeftCtrl,
			Scancode.ScancodeRctrl => ImGuiKey.RightCtrl,
			Scancode.ScancodeLalt => ImGuiKey.LeftAlt,
			Scancode.ScancodeRalt => ImGuiKey.RightAlt,
			Scancode.ScancodeLgui => ImGuiKey.LeftSuper,
			Scancode.ScancodeRgui => ImGuiKey.RightSuper,
			Scancode.ScancodeMenu => ImGuiKey.Menu,
			Scancode.ScancodeUp => ImGuiKey.UpArrow,
			Scancode.ScancodeDown => ImGuiKey.DownArrow,
			Scancode.ScancodeLeft => ImGuiKey.LeftArrow,
			Scancode.ScancodeRight => ImGuiKey.RightArrow,
			Scancode.ScancodeEscape => ImGuiKey.Escape,
			Scancode.ScancodeReturn => ImGuiKey.Enter,
			Scancode.ScancodeSpace => ImGuiKey.Space,
			Scancode.ScancodeBackspace => ImGuiKey.Backspace,
			Scancode.ScancodeInsert => ImGuiKey.Insert,
			Scancode.ScancodeDelete => ImGuiKey.Delete,
			Scancode.ScancodeHome => ImGuiKey.Home,
			Scancode.ScancodeEnd => ImGuiKey.End,
			Scancode.ScancodePageup => ImGuiKey.PageUp,
			Scancode.ScancodePagedown => ImGuiKey.PageDown,
			Scancode.ScancodeA => ImGuiKey.A,
			Scancode.ScancodeC => ImGuiKey.C,
			Scancode.ScancodeV => ImGuiKey.V,
			Scancode.ScancodeX => ImGuiKey.X,
			Scancode.ScancodeY => ImGuiKey.Y,
			Scancode.ScancodeZ => ImGuiKey.Z,
			Scancode.Scancode0 => ImGuiKey._0,
			Scancode.Scancode1 => ImGuiKey._1,
			Scancode.Scancode2 => ImGuiKey._2,
			Scancode.Scancode3 => ImGuiKey._3,
			Scancode.Scancode4 => ImGuiKey._4,
			Scancode.Scancode5 => ImGuiKey._5,
			Scancode.Scancode6 => ImGuiKey._6,
			Scancode.Scancode7 => ImGuiKey._7,
			Scancode.Scancode8 => ImGuiKey._8,
			Scancode.Scancode9 => ImGuiKey._9,
			Scancode.ScancodeF1 => ImGuiKey.F1,
			Scancode.ScancodeF2 => ImGuiKey.F2,
			Scancode.ScancodeF3 => ImGuiKey.F3,
			Scancode.ScancodeF4 => ImGuiKey.F4,
			Scancode.ScancodeF5 => ImGuiKey.F5,
			Scancode.ScancodeF6 => ImGuiKey.F6,
			Scancode.ScancodeF7 => ImGuiKey.F7,
			Scancode.ScancodeF8 => ImGuiKey.F8,
			Scancode.ScancodeF9 => ImGuiKey.F9,
			Scancode.ScancodeF10 => ImGuiKey.F10,
			Scancode.ScancodeF11 => ImGuiKey.F11,
			Scancode.ScancodeF12 => ImGuiKey.F12,
			Scancode.ScancodeGrave => ImGuiKey.GraveAccent,
			Scancode.ScancodeMinus => ImGuiKey.Minus,
			Scancode.ScancodeEquals => ImGuiKey.Equal,
			Scancode.ScancodeLeftbracket => ImGuiKey.LeftBracket,
			Scancode.ScancodeRightbracket => ImGuiKey.RightBracket,
			Scancode.ScancodeSemicolon => ImGuiKey.Semicolon,
			Scancode.ScancodeApostrophe => ImGuiKey.Apostrophe,
			Scancode.ScancodeBackslash => ImGuiKey.Backslash,
			Scancode.ScancodeComma => ImGuiKey.Comma,
			Scancode.ScancodePeriod => ImGuiKey.Period,
			Scancode.ScancodeSlash => ImGuiKey.Slash,
			Scancode.ScancodeKP0 => ImGuiKey.Keypad0,
			Scancode.ScancodeKP1 => ImGuiKey.Keypad1,
			Scancode.ScancodeKP2 => ImGuiKey.Keypad2,
			Scancode.ScancodeKP3 => ImGuiKey.Keypad3,
			Scancode.ScancodeKP4 => ImGuiKey.Keypad4,
			Scancode.ScancodeKP5 => ImGuiKey.Keypad5,
			Scancode.ScancodeKP6 => ImGuiKey.Keypad6,
			Scancode.ScancodeKP7 => ImGuiKey.Keypad7,
			Scancode.ScancodeKP8 => ImGuiKey.Keypad8,
			Scancode.ScancodeKP9 => ImGuiKey.Keypad9,
			Scancode.ScancodeKPPeriod => ImGuiKey.KeypadDecimal,
			Scancode.ScancodeKPDivide => ImGuiKey.KeypadDivide,
			Scancode.ScancodeKPMultiply => ImGuiKey.KeypadMultiply,
			Scancode.ScancodeKPMinus => ImGuiKey.KeypadSubtract,
			Scancode.ScancodeKPPlus => ImGuiKey.KeypadAdd,
			Scancode.ScancodeKPEnter => ImGuiKey.KeypadEnter,
			_ => ImGuiKey.None
		};

		return imguiKey != ImGuiKey.None;
	}
}