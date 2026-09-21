using System.Numerics;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

public static class EditorViewportProjection
{
	/// <summary>
	/// The projection a viewport's image was actually rendered with. Gizmos, picking and viewport rays must
	/// use this rather than <see cref="Camera.Perspective"/>: the camera component carries whatever aspect
	/// ratio was last baked into it, which is only right for one view, so anything derived from it would be
	/// computed against a different aspect than the image it is drawn over. Before the renderer has published
	/// a frame for the view there is nothing to use, and the baked projection stands in.
	/// </summary>
	public static Matrix4x4 Resolve(in SceneViewportRenderState renderState, in Camera camera)
	{
		return renderState.RenderSizePixels.X > 0 && renderState.RenderSizePixels.Y > 0
			? renderState.Projection
			: camera.Perspective;
	}
}
