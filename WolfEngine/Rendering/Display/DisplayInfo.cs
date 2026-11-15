using WolfEngine.Mathematics;

namespace WolfEngine.Rendering;

/// <summary>What the window is showing and the display it is on, as of the last rendered frame.</summary>
/// <param name="WindowMode">The mode the window is actually in, which can differ from a request still in progress.</param>
/// <param name="WindowSize">The window's size in points.</param>
/// <param name="OutputResolution">The size in pixels the game presents at.</param>
/// <param name="NativeResolution">The display's resolution in pixels, which fullscreen uses by default.</param>
/// <param name="SupportedResolutions">
/// The fullscreen resolutions to offer the player, smallest first, ending with <paramref name="NativeResolution"/>.
/// </param>
public sealed record DisplayInfo(
	WindowMode WindowMode,
	Int2 WindowSize,
	Int2 OutputResolution,
	Int2 NativeResolution,
	IReadOnlyList<Int2> SupportedResolutions)
{
	/// <summary>Before the renderer has opened its window, nothing about the display is known.</summary>
	public static DisplayInfo Unknown { get; } = new(WindowMode.Windowed, Int2.Zero, Int2.Zero, Int2.Zero, []);

	/// <summary>Builds the information for a display, offering the standard fullscreen resolutions for it.</summary>
	public static DisplayInfo Create(WindowMode windowMode, Int2 windowSize, Int2 outputResolution, Int2 nativeResolution)
	{
		return new DisplayInfo(
			windowMode,
			windowSize,
			outputResolution,
			nativeResolution,
			DisplayResolutions.ForNativeResolution(nativeResolution));
	}
}

/// <summary>The fullscreen resolutions offered for a display.</summary>
public static class DisplayResolutions
{
	/// <summary>
	/// Common output heights. Fullscreen scales the image to cover the display, so every resolution offered keeps the
	/// display's aspect ratio rather than coming from a fixed list of 16:9 sizes, which would stretch on other panels.
	/// </summary>
	private static readonly int[] StandardHeights = [720, 768, 900, 1080, 1200, 1440, 1600, 1800, 2160, 2400, 2880];

	/// <summary>
	/// The resolutions at or below <paramref name="nativeResolution"/> with its aspect ratio, smallest first and
	/// ending with the native resolution. Empty when the native resolution is not known.
	/// </summary>
	public static IReadOnlyList<Int2> ForNativeResolution(Int2 nativeResolution)
	{
		if (nativeResolution.X <= 0 || nativeResolution.Y <= 0) return [];

		var aspect = (double)nativeResolution.X / nativeResolution.Y;
		var resolutions = new List<Int2>();
		foreach (var height in StandardHeights)
		{
			if (height >= nativeResolution.Y)
			{
				break;
			}

			var width = RoundToEven(height * aspect);
			resolutions.Add(new Int2(width, height));
		}

		resolutions.Add(nativeResolution);

		return resolutions;
	}

	/// <summary>
	/// Clamps a requested fullscreen resolution to the native one. Zero, or either side being zero, means native. A
	/// request larger than the display renders at native size rather than supersampling.
	/// </summary>
	public static Int2 ResolveFullscreen(Int2 requested, Int2 nativeResolution)
	{
		if (requested.X <= 0 || requested.Y <= 0)
		{
			return nativeResolution;
		}

		if (nativeResolution.X <= 0 || nativeResolution.Y <= 0)
		{
			return requested;
		}

		return new Int2(
			Math.Min(requested.X, nativeResolution.X),
			Math.Min(requested.Y, nativeResolution.Y));
	}

	private static int RoundToEven(double value)
	{
		return (int)Math.Round(value / 2.0) * 2;
	}
}
