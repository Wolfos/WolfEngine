using WolfEngine.Mathematics;

namespace WolfEngine.Rendering;

/// <summary>How the game's window is shown.</summary>
public enum WindowMode
{
	/// <summary>A movable, resizable window on the desktop.</summary>
	Windowed,

	/// <summary>
	/// A borderless window covering the whole display. The display keeps its own mode; a lower resolution renders
	/// smaller and is scaled up to fill it.
	/// </summary>
	Fullscreen,
}

/// <summary>The window mode and size a renderer is asked to show its window in.</summary>
/// <param name="Mode">The window mode.</param>
/// <param name="Resolution">
/// For <see cref="WindowMode.Fullscreen"/>, the size in pixels to render at, where zero means the display's native
/// resolution. For <see cref="WindowMode.Windowed"/>, the window's size in points, where zero keeps its current size.
/// </param>
public readonly record struct WindowConfiguration(WindowMode Mode, Int2 Resolution);
