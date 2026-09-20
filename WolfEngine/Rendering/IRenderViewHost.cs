using WolfEngine.ECS;
using WolfEngine.Mathematics;

namespace WolfEngine.Rendering;

/// <summary>
/// What a view is created from. Fixed for the life of the view.
/// </summary>
/// <param name="World">
/// The world this view renders. A world may back at most one view: the renderer's change tracking counts how
/// many draw databases have consumed each transform change, and a world gathered by more than one view's
/// databases would have those changes pruned before every database had seen them, dropping draws silently.
/// </param>
/// <param name="Name">
/// Short name for diagnostics. It is qualified into render-pass names, so a GPU crash log or a profiler scope
/// says which view a pass belonged to.
/// </param>
/// <param name="Output">Whether the view renders into its own texture or straight to the backbuffer.</param>
public readonly record struct RenderViewDescriptor(World World, string Name, RenderViewOutput Output);

/// <summary>
/// Creates and destroys rendered views. A view is a world plus a camera, and it yields a texture.
/// </summary>
/// <remarks>
/// Per-frame view settings — visible, size, resolution scale, debug view — travel on
/// <c>EditorViewportStateBus</c> instead of through this interface, because the panel that owns a viewport
/// republishes them every frame anyway and a second channel for the same values would be state to keep in
/// sync rather than state to read.
/// </remarks>
public interface IRenderViewHost
{
	/// <summary>
	/// Registers a view over <paramref name="descriptor"/>'s world.
	/// </summary>
	/// <exception cref="InvalidOperationException">
	/// That world already backs a view, or there is no free view slot.
	/// </exception>
	RenderViewId CreateView(in RenderViewDescriptor descriptor);

	/// <summary>
	/// Retires a view's GPU resources and frees its slot. Returns false when the view does not exist. The
	/// primary view cannot be destroyed; the renderer always records through one view.
	/// </summary>
	bool DestroyView(RenderViewId view);

	/// <summary>The texture a view last resolved to, for the UI to sample.</summary>
	bool TryGetViewTexture(RenderViewId view, out nint textureId, out Int2 size);

	/// <summary>The view rendering <paramref name="world"/>, if any.</summary>
	bool TryGetViewForWorld(World world, out RenderViewId view);

	/// <summary>Live views, lowest slot first.</summary>
	IReadOnlyList<RenderViewId> Views { get; }
}
