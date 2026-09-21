namespace WolfEngine.Rendering;

/// <summary>
/// Where a view's image ends up. This is a property of the view, not of the process: the editor's viewports
/// render into textures the UI samples, while a standalone game is one view that renders straight to the
/// backbuffer, and both can be described the same way.
/// </summary>
public enum RenderViewOutput
{
	/// <summary>Into the view's own texture, for something else to sample.</summary>
	Texture,

	/// <summary>Straight to the window's backbuffer, filling it.</summary>
	Backbuffer
}
