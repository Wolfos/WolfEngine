using System.Numerics;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

/// <summary>Editor adapter that maps the displayed image rectangle to the shared projection API.</summary>
public static class SceneViewportRayUtility
{
	public static bool TryBuildWorldRay(
		in SceneViewportUiState viewportState,
		Vector2 screenPoint,
		in ViewProjection viewProjection,
		out Ray ray)
	{
		// Eligibility is decided by the owning viewport tool. Keeping the math independent of image bounds
		// lets captured drags continue smoothly after the pointer leaves the image.
		return viewProjection.TryScreenPointToRay(screenPoint, viewportState.ImageMin, viewportState.ImageMax, out ray);
	}
}
