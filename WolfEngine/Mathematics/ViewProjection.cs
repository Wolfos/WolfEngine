using System.Numerics;

namespace WolfEngine.Mathematics;

/// <summary>
/// An immutable projection snapshot for one rendered view. It stores the inverse world view-projection
/// matrix so screen rays use the same camera and projection that produced the image.
/// </summary>
public readonly struct ViewProjection
{
	private readonly Matrix4x4 _inverseViewProjection;
	private readonly Matrix4x4 _worldToClip;
	private readonly Vector3 _cameraOrigin;
	private readonly bool _isValid;

	private ViewProjection(Matrix4x4 inverseViewProjection, Matrix4x4 worldToClip, Vector3 cameraOrigin)
	{
		_inverseViewProjection = inverseViewProjection;
		_worldToClip = worldToClip;
		_cameraOrigin = cameraOrigin;
		_isValid = true;
	}

	public bool IsValid => _isValid;
	public Matrix4x4 WorldToClip => _worldToClip;
	public Vector3 CameraOrigin => _cameraOrigin;

	/// <summary>
	/// Creates a snapshot from a world-to-view matrix and projection; callers should supply the unjittered
	/// projection used to draw the view. The engine uses a 0..1 clip-space depth range, so unprojection
	/// samples the near and far planes at z=0 and z=1.
	/// </summary>
	public static bool TryCreate(Matrix4x4 view, Matrix4x4 projection, out ViewProjection viewProjection)
	{
		viewProjection = default;
		var viewProjectionMatrix = view * projection;
		if (IsFinite(view) == false || IsFinite(projection) == false || IsFinite(viewProjectionMatrix) == false ||
		    Matrix4x4.Invert(viewProjectionMatrix, out var inverse) == false || IsFinite(inverse) == false ||
		    Matrix4x4.Invert(view, out var cameraWorld) == false || IsFinite(cameraWorld) == false ||
		    IsFinite(cameraWorld.Translation) == false)
		{
			return false;
		}

		viewProjection = new ViewProjection(inverse, viewProjectionMatrix, cameraWorld.Translation);
		return true;
	}

	/// <summary>
	/// Builds a world-space ray from a screen point. The screen point and rectangle use the same units;
	/// Y increases downward. Points outside the rectangle extrapolate beyond the viewport.
	/// </summary>
	public bool TryScreenPointToRay(Vector2 screenPoint, Vector2 screenMin, Vector2 screenMax, out Ray ray)
	{
		ray = default;
		var width = screenMax.X - screenMin.X;
		var height = screenMax.Y - screenMin.Y;
		if (_isValid == false || IsFinite(screenPoint) == false || IsFinite(screenMin) == false ||
		    IsFinite(screenMax) == false || float.IsFinite(width) == false || float.IsFinite(height) == false ||
		    width <= 1e-5f || height <= 1e-5f)
		{
			return false;
		}

		var viewportPoint = new Vector2((screenPoint.X - screenMin.X) / width, (screenPoint.Y - screenMin.Y) / height);
		return TryViewportPointToRay(viewportPoint, out ray);
	}

	/// <summary>
	/// Builds a world-space ray from normalized viewport coordinates: (0,0) is top-left and (1,1) is
	/// bottom-right. Coordinates outside that range extrapolate beyond the viewport.
	/// </summary>
	public bool TryViewportPointToRay(Vector2 viewportPoint, out Ray ray)
	{
		ray = default;
		if (_isValid == false || IsFinite(viewportPoint) == false)
		{
			return false;
		}

		var ndcX = viewportPoint.X * 2.0f - 1.0f;
		var ndcY = 1.0f - viewportPoint.Y * 2.0f;
		if (TryUnproject(new Vector3(ndcX, ndcY, 0.0f), out var nearPoint) == false ||
		    TryUnproject(new Vector3(ndcX, ndcY, 1.0f), out var farPoint) == false)
		{
			return false;
		}

		var direction = farPoint - nearPoint;
		if (IsFinite(direction) == false || direction.LengthSquared() <= 1e-12f)
		{
			return false;
		}

		ray = new Ray(nearPoint, direction);
		return true;
	}

	private bool TryUnproject(Vector3 ndc, out Vector3 point)
	{
		point = default;
		var world = Vector4.Transform(new Vector4(ndc, 1.0f), _inverseViewProjection);
		if (IsFinite(world) == false || MathF.Abs(world.W) <= 1e-6f)
		{
			return false;
		}

		point = new Vector3(world.X / world.W, world.Y / world.W, world.Z / world.W);
		return IsFinite(point);
	}

	private static bool IsFinite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
	private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
	private static bool IsFinite(Vector4 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
	private static bool IsFinite(Matrix4x4 value) =>
		float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
		float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
		float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
		float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}
