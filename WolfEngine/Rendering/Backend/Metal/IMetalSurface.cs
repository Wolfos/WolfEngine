using WolfEngine.Mathematics;

namespace WolfEngine.Rendering.Backend.Metal;

[Flags]
internal enum MetalSurfaceEvents
{
	None = 0,

	/// <summary>The drawable or window size may have changed.</summary>
	Resized = 1 << 0,

	/// <summary>The user asked to close the window.</summary>
	CloseRequested = 1 << 1,
}

/// <summary>
/// What the Metal renderer presents into: a desktop window it opens itself, or a view a host application owns.
/// The renderer configures the layer and owns everything drawn into it; the surface owns the window and its events.
/// </summary>
internal interface IMetalSurface
{
	/// <summary>
	/// Whether the layer can present without waiting for the display. Only macOS has
	/// <c>CAMetalLayer.displaySyncEnabled</c>; iOS always presents in step with the display.
	/// </summary>
	bool CanDisableVSync { get; }

	/// <summary>
	/// Opens the window or attaches to the host's view, on the thread that renders, and returns the
	/// <c>CAMetalLayer</c> to render into.
	/// </summary>
	/// <param name="initialSize">The window size to open with.</param>
	/// <param name="exactDrawableSize">
	/// Whether a host asked for <paramref name="initialSize"/> explicitly, in which case it is also the drawable size
	/// in pixels until the window is resized, so automation captures do not depend on the display scale.
	/// </param>
	IntPtr Initialize(Int2 initialSize, bool exactDrawableSize);

	/// <summary>The size the layer's drawables should have, in pixels, or false while it is not known yet.</summary>
	bool TryGetDrawableSize(out Int2 size);

	/// <summary>The window's size in points, or false while it is not known yet.</summary>
	bool TryGetWindowSize(out Int2 size);

	/// <summary>Processes the platform events that arrived since the previous frame.</summary>
	MetalSurfaceEvents PumpEvents();

	/// <summary>Closes the window, or lets go of the host's view.</summary>
	void Shutdown();
}
