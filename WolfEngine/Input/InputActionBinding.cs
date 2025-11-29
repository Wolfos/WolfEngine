namespace WolfEngine.Input;

public enum InputActionBinding
{
	None = 0,

	// Mouse axes (Axis2D bindings)
	MousePosition,
	MouseDelta,
	MouseScroll, // Y = vertical wheel, X = horizontal tilt if available

	// Mouse buttons (Button bindings)
	MouseButtonLeft,
	MouseButtonRight,
	MouseButtonMiddle,
	MouseButton4,
	MouseButton5,

	// Gamepad axes (Axis1D/Axis2D bindings)
	GamepadLeftStick,
	GamepadRightStick,
	GamepadLeftTrigger,
	GamepadRightTrigger,
	GamepadDpad, // Axis2D representing hat; also exposed as individual buttons below

	// Gamepad buttons (Button bindings)
	GamepadFaceSouth, // Bottom button (A / Cross)
	GamepadFaceEast,  // Right button (B / Circle)
	GamepadFaceWest,  // Left button (X / Square)
	GamepadFaceNorth, // Top button (Y / Triangle)
	GamepadLeftBumper,
	GamepadRightBumper,
	GamepadLeftStickButton,
	GamepadRightStickButton,
	GamepadDpadUp,
	GamepadDpadDown,
	GamepadDpadLeft,
	GamepadDpadRight,
	GamepadBack,  // View / Select
	GamepadStart, // Menu / Options
	GamepadGuide, // Home / PS

	// Keyboard keys (Button bindings)
	KeyA,
	KeyB,
	KeyC,
	KeyD,
	KeyE,
	KeyF,
	KeyG,
	KeyH,
	KeyI,
	KeyJ,
	KeyK,
	KeyL,
	KeyM,
	KeyN,
	KeyO,
	KeyP,
	KeyQ,
	KeyR,
	KeyS,
	KeyT,
	KeyU,
	KeyV,
	KeyW,
	KeyX,
	KeyY,
	KeyZ,

	Key0,
	Key1,
	Key2,
	Key3,
	Key4,
	Key5,
	Key6,
	Key7,
	Key8,
	Key9,

	KeyF1,
	KeyF2,
	KeyF3,
	KeyF4,
	KeyF5,
	KeyF6,
	KeyF7,
	KeyF8,
	KeyF9,
	KeyF10,
	KeyF11,
	KeyF12,

	KeyEscape,
	KeyTab,
	KeyCapsLock,
	KeyLeftShift,
	KeyRightShift,
	KeyLeftControl,
	KeyRightControl,
	KeyLeftAlt,
	KeyRightAlt,
	KeyLeftSuper,  // Windows / Command
	KeyRightSuper, // Windows / Command
	KeyMenu,

	KeySpace,
	KeyEnter,
	KeyBackspace,

	KeyInsert,
	KeyDelete,
	KeyHome,
	KeyEnd,
	KeyPageUp,
	KeyPageDown,
	KeyArrowUp,
	KeyArrowDown,
	KeyArrowLeft,
	KeyArrowRight,

	KeyMinus,
	KeyEquals,
	KeyLeftBracket,
	KeyRightBracket,
	KeyBackslash,
	KeySemicolon,
	KeyApostrophe,
	KeyGrave,
	KeyComma,
	KeyPeriod,
	KeySlash,

	KeyPrintScreen,
	KeyScrollLock,
	KeyPause,
	KeyNumLock,

	KeyNumpad0,
	KeyNumpad1,
	KeyNumpad2,
	KeyNumpad3,
	KeyNumpad4,
	KeyNumpad5,
	KeyNumpad6,
	KeyNumpad7,
	KeyNumpad8,
	KeyNumpad9,
	KeyNumpadDivide,
	KeyNumpadMultiply,
	KeyNumpadSubtract,
	KeyNumpadAdd,
	KeyNumpadDecimal,
	KeyNumpadEnter
}
