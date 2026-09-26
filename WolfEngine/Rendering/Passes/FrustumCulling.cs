using System.Numerics;

namespace WolfEngine.Rendering.Passes;

internal static class FrustumCulling
{
	// Six shadow-map planes, up to six receiver faces and twelve silhouette-edge planes.
	// Mirrors WOLF_CULL_PLANE_COUNT in gpu_draw_cull.compute.slang.
	public const int MaxPlaneCount = 24;

	/// <summary>
	/// Intersects the shadow map with the camera receiver frustum extruded towards the light.
	/// The extrusion is unbounded; the map's existing depth planes limit caster reach.
	/// All planes are normalized and expressed relative to the camera origin.
	/// </summary>
	public static int BuildShadowCasterPlanes(SceneDrawData scene, Matrix4x4 shadowViewProjection,
		Vector3 lightDirection, float receiverDepth, float filterPadding, Span<Vector4> destination)
	{
		if (destination.Length < MaxPlaneCount)
			throw new ArgumentException("Shadow culling requires space for the maximum plane count.", nameof(destination));
		ExtractPlanes(shadowViewProjection, destination);
		const int mapPlaneCount = 6;
		if (!Matrix4x4.Invert(scene.UnjitteredViewProjection, out var inverseViewProjection) ||
		    !float.IsFinite(receiverDepth) || receiverDepth <= 0 ||
		    !float.IsFinite(lightDirection.LengthSquared()) || lightDirection.LengthSquared() < 1e-10f)
			return mapPlaneCount;

		var towardsLight = -Vector3.Normalize(lightDirection);
		Span<Vector3> corners = stackalloc Vector3[8];
		// Include a pixel beyond the viewport and the actual projection jitter. Fog uses the
		// unjittered frustum; surface receivers use the jittered one.
		var marginX = MathF.Abs(scene.JitterNdc.X) + 2.0f / Math.Max(scene.SceneFramebufferSize.X, 1);
		var marginY = MathF.Abs(scene.JitterNdc.Y) + 2.0f / Math.Max(scene.SceneFramebufferSize.Y, 1);
		for (var corner = 0; corner < 4; corner++)
		{
			var x = (corner % 2 == 0 ? -1 : 1) * (1 + marginX);
			var y = (corner < 2 ? -1 : 1) * (1 + marginY);
			// Use two finite depths rather than the far plane, which may be at infinity.
			var nearH = Vector4.Transform(new Vector4(x, y, 0, 1), inverseViewProjection);
			var middleH = Vector4.Transform(new Vector4(x, y, 0.5f, 1), inverseViewProjection);
			if (MathF.Abs(nearH.W) < 1e-8f || MathF.Abs(middleH.W) < 1e-8f)
				return mapPlaneCount;
			var near = new Vector3(nearH.X, nearH.Y, nearH.Z) / nearH.W;
			var middle = new Vector3(middleH.X, middleH.Y, middleH.Z) / middleH.W;
			var nearDepth = Vector3.Transform(near, scene.ViewMatrix).Z;
			var depthDelta = Vector3.Transform(middle, scene.ViewMatrix).Z - nearDepth;
			if (!float.IsFinite(depthDelta) || MathF.Abs(depthDelta) < 1e-8f)
				return mapPlaneCount;
			var rayPerDepth = (middle - near) / depthDelta;
			// Depth zero includes the eye, remains conservative near the camera and works
			// for orthographic cameras too. Do not remove inner cascade receivers: fog
			// selects by view depth while surface shading selects by radial distance.
			corners[corner] = near - rayPerDepth * nearDepth;
			corners[corner + 4] = near + rayPerDepth * (receiverDepth - nearDepth);
			if (!IsFinite(corners[corner]) || !IsFinite(corners[corner + 4]))
				return mapPlaneCount;
		}

		var count = mapPlaneCount;
		Span<Vector4> receiverPlanes = stackalloc Vector4[6];
		ExtractPlanes(scene.UnjitteredViewProjection, receiverPlanes);
		foreach (var plane in receiverPlanes)
		{
			var normal = new Vector3(plane.X, plane.Y, plane.Z);
			// A retained face must admit every point on the ray towards the light.
			if (Vector3.Dot(normal, towardsLight) >= 0)
				AddSupportPlane(normal, corners, filterPadding, destination, ref count);
		}

		// Only silhouette edges of the receiver hull become faces of its extrusion.
		ReadOnlySpan<int> edges = [0, 1, 1, 3, 3, 2, 2, 0, 4, 5, 5, 7, 7, 6, 6, 4, 0, 4, 1, 5, 2, 6, 3, 7];
		for (var edge = 0; edge < edges.Length; edge += 2)
		{
			var start = corners[edges[edge]];
			var normal = Vector3.Cross(corners[edges[edge + 1]] - start, towardsLight);
			var length = normal.Length();
			if (length < 1e-6f) continue;
			normal /= length;
			var minimum = float.PositiveInfinity;
			var maximum = float.NegativeInfinity;
			foreach (var point in corners)
			{
				var distance = Vector3.Dot(normal, point - start);
				minimum = MathF.Min(minimum, distance);
				maximum = MathF.Max(maximum, distance);
			}
			if (minimum >= -1e-4f)
				AddSupportPlane(normal, corners, filterPadding, destination, ref count);
			else if (maximum <= 1e-4f)
				AddSupportPlane(-normal, corners, filterPadding, destination, ref count);
		}
		return count;
	}

	private static bool IsFinite(Vector3 point) =>
		float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);

	private static void AddSupportPlane(Vector3 normal, ReadOnlySpan<Vector3> corners, float padding,
		Span<Vector4> destination, ref int count)
	{
		var length = normal.Length();
		if (!float.IsFinite(length) || length < 1e-6f) return;
		normal /= length;
		for (var index = 6; index < count; index++)
		{
			var existing = destination[index];
			if (Vector3.Dot(normal, new Vector3(existing.X, existing.Y, existing.Z)) > 0.99999f)
				return;
		}
		var minimum = float.PositiveInfinity;
		foreach (var corner in corners)
			minimum = MathF.Min(minimum, Vector3.Dot(normal, corner));
		destination[count++] = new Vector4(normal, -minimum + padding);
	}

	public static void ExtractPlanes(Matrix4x4 viewProjection, Span<Vector4> planes)
	{
		var col1 = new Vector4(viewProjection.M11, viewProjection.M21, viewProjection.M31, viewProjection.M41);
		var col2 = new Vector4(viewProjection.M12, viewProjection.M22, viewProjection.M32, viewProjection.M42);
		var col3 = new Vector4(viewProjection.M13, viewProjection.M23, viewProjection.M33, viewProjection.M43);
		var col4 = new Vector4(viewProjection.M14, viewProjection.M24, viewProjection.M34, viewProjection.M44);

		planes[0] = NormalizePlane(col4 + col1);
		planes[1] = NormalizePlane(col4 - col1);
		planes[2] = NormalizePlane(col4 + col2);
		planes[3] = NormalizePlane(col4 - col2);
		planes[4] = NormalizePlane(col3);
		planes[5] = NormalizePlane(col4 - col3);
	}

	private static Vector4 NormalizePlane(Vector4 plane)
	{
		var normal = new Vector3(plane.X, plane.Y, plane.Z);
		var length = normal.Length();
		if (length <= 0.0f)
		{
			return plane;
		}

		var invLength = 1.0f / length;
		return plane * invLength;
	}
}
