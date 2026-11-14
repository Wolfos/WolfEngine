using WolfEngine.Mathematics;

namespace WolfEngine.Rendering;

/// <summary>
/// The player-facing graphics settings. Change them with a <c>with</c> expression and pass the result to
/// <see cref="IGraphicsSettings.Apply"/>, for example
/// <c>graphics.Apply(graphics.Current with { WindowMode = WindowMode.Windowed })</c>.
/// </summary>
/// <remarks>
/// Effect quality belongs here as well once it exists, as a member that overrides the cost-related fields of a scene's
/// <see cref="RenderConfig"/>, so a settings menu changes everything through one record.
/// </remarks>
public sealed record GraphicsSettings
{
	public WindowMode WindowMode { get; init; } = WindowMode.Fullscreen;

	/// <summary>
	/// For <see cref="WindowMode.Fullscreen"/>, the size in pixels to render at, where zero means the display's
	/// native resolution; choose from <see cref="DisplayInfo.SupportedResolutions"/>. For
	/// <see cref="WindowMode.Windowed"/>, the window's size in points, where zero keeps its current size.
	/// </summary>
	public Int2 Resolution { get; init; }

	/// <summary>Whether frames wait for the display to refresh. Some platforms, such as iOS, always do.</summary>
	public bool VSync { get; init; } = true;
}

/// <summary>Reads and changes the game's graphics settings from gameplay code, on any thread.</summary>
public interface IGraphicsSettings
{
	/// <summary>The settings last applied. The window catches up with them over the next frames.</summary>
	GraphicsSettings Current { get; }

	/// <summary>
	/// Whether <see cref="Apply"/> changes the window mode and resolution. It does not in the editor, where Play mode
	/// renders into a panel, or on platforms that are always fullscreen; those settings are still recorded in
	/// <see cref="Current"/>, so a settings menu behaves the same.
	/// </summary>
	bool CanChangeWindow { get; }

	/// <summary>What the window is showing and the display it is on, as of the last rendered frame.</summary>
	DisplayInfo Display { get; }

	/// <summary>
	/// Applies new settings. The window changes before the next frame renders; switching to or from fullscreen can
	/// take several frames on platforms that animate it.
	/// </summary>
	void Apply(GraphicsSettings settings);
}

/// <summary>
/// Forwards graphics settings to the renderer. The window is only changed when the game owns it, which is when the
/// scene presents to the full window rather than to an editor viewport.
/// </summary>
public sealed class GraphicsSettingsService : IGraphicsSettings
{
	private readonly IRenderer _renderer;
	private readonly object _sync = new();
	private GraphicsSettings _current;

	public GraphicsSettingsService(IRenderer renderer, RenderPresentationOptions presentationOptions)
	{
		ArgumentNullException.ThrowIfNull(renderer);
		ArgumentNullException.ThrowIfNull(presentationOptions);

		_renderer = renderer;
		var ownsWindow = presentationOptions.OutputMode == RenderOutputMode.FullWindow;
		CanChangeWindow = ownsWindow && renderer.CanConfigureWindow;
		_current = new GraphicsSettings
		{
			WindowMode = ownsWindow ? WindowMode.Fullscreen : WindowMode.Windowed,
			VSync = renderer.VSyncEnabled
		};
	}

	public GraphicsSettings Current
	{
		get
		{
			lock (_sync)
			{
				return _current;
			}
		}
	}

	public bool CanChangeWindow { get; }

	public DisplayInfo Display => _renderer.GetDisplayInfo();

	public void Apply(GraphicsSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);
		if (settings.Resolution.X < 0 || settings.Resolution.Y < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(settings), "Resolution cannot be negative.");
		}

		lock (_sync)
		{
			_current = settings;
			_renderer.VSyncEnabled = settings.VSync;
			if (CanChangeWindow)
			{
				_renderer.RequestWindowConfiguration(new WindowConfiguration(settings.WindowMode, settings.Resolution));
			}
		}
	}
}
