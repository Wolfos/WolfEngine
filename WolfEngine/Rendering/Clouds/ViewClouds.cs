using System.Numerics;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering.Clouds;

/// <summary>
/// A view's cloud state that outlives a frame: how far the wind has carried its clouds, its jitter sequence, and
/// what its sky environment was last drawn with. Render thread only.
/// </summary>
internal sealed class ViewClouds
{
	/// <summary>The longest step the wind advances in one frame, so a hitch does not make the clouds jump.</summary>
	public const float MaxFrameSeconds = 0.25f;

	/// <summary>How far the clouds may drift past the camera before the sky environment redraws them, in metres.</summary>
	public const double EnvironmentRefreshDistanceMetres = 500.0;

	/// <summary>World XZ distance the wind has carried the clouds, in metres.</summary>
	public double WindDisplacementX { get; private set; }
	public double WindDisplacementZ { get; private set; }

	/// <summary>How far the wind carried the clouds on the latest frame, in world XZ metres.</summary>
	public Vector2 LastWindDelta { get; private set; }

	/// <summary>Counts this view's cloud frames, for the ray-march jitter sequence.</summary>
	public uint FrameIndex { get; private set; }

	/// <summary>Whether the view's sky environment currently has clouds drawn into it.</summary>
	public bool EnvironmentHasClouds { get; private set; }

	private CloudConfig _environmentSettings;
	private double _environmentOriginX;
	private double _environmentOriginZ;

	/// <summary>Moves the clouds with the wind by one frame of <paramref name="deltaSeconds"/>.</summary>
	public void Advance(in CloudConfig settings, float deltaSeconds)
	{
		var seconds = float.IsFinite(deltaSeconds)
			? Math.Clamp(deltaSeconds, 0.0f, MaxFrameSeconds)
			: 0.0f;
		var velocity = CloudFrame.GetWindDirection(settings.WindDirection) * settings.WindSpeed;
		var delta = velocity * seconds;
		WindDisplacementX += delta.X;
		WindDisplacementZ += delta.Y;
		LastWindDelta = delta;
		FrameIndex++;
	}

	/// <summary>
	/// Whether the sky environment must be redrawn for the clouds as they are now: the settings changed, or the
	/// clouds moved relative to the camera, by wind or by the camera, further than a refresh allows.
	/// </summary>
	public bool EnvironmentChanged(in CloudConfig settings, Vector3 cameraOrigin)
	{
		if (EnvironmentHasClouds == false)
		{
			return true;
		}

		if (SettingsDiffer(settings, _environmentSettings))
		{
			return true;
		}

		var originX = cameraOrigin.X - WindDisplacementX;
		var originZ = cameraOrigin.Z - WindDisplacementZ;
		var dx = originX - _environmentOriginX;
		var dz = originZ - _environmentOriginZ;

		return dx * dx + dz * dz > EnvironmentRefreshDistanceMetres * EnvironmentRefreshDistanceMetres;
	}

	/// <summary>Records that the sky environment is being redrawn with the clouds as they are now.</summary>
	public void MarkEnvironmentDrawn(in CloudConfig settings, Vector3 cameraOrigin)
	{
		EnvironmentHasClouds = true;
		_environmentSettings = settings;
		_environmentOriginX = cameraOrigin.X - WindDisplacementX;
		_environmentOriginZ = cameraOrigin.Z - WindDisplacementZ;
	}

	/// <summary>Records that the sky environment is being redrawn without clouds.</summary>
	public void MarkEnvironmentCleared()
	{
		EnvironmentHasClouds = false;
	}

	internal static bool SettingsDiffer(in CloudConfig a, in CloudConfig b)
	{
		return a.Coverage != b.Coverage ||
		       a.Density != b.Density ||
		       a.CloudBaseAltitude != b.CloudBaseAltitude ||
		       a.CloudThickness != b.CloudThickness ||
		       a.ShapeScale != b.ShapeScale ||
		       a.DetailStrength != b.DetailStrength ||
		       a.WindDirection != b.WindDirection ||
		       a.WindSpeed != b.WindSpeed ||
		       a.AmbientStrength != b.AmbientStrength ||
		       a.MultipleScattering != b.MultipleScattering ||
		       a.Quality != b.Quality;
	}
}
