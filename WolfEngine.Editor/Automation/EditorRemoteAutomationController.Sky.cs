using System.Numerics;
using WolfEngine.AssetPipeline;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Editor.Automation;

public sealed partial class EditorRemoteAutomationController
{
	/// <summary>
	/// Switches the active scene's sky in memory, without saving its render config. Settings left null keep their
	/// current values.
	/// </summary>
	public Task<string> SetSkyAsync(
		string mode,
		string? hdriAssetPath,
		bool clearHdri,
		float? hdriRotationDegrees,
		float? hdriIntensity,
		float? gradientIntensity,
		ProceduralSkyOverrides procedural,
		string? lightingQuality,
		CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			if (Enum.TryParse<SkyConfigMode>(mode, true, out var selectedMode) == false ||
			    Enum.IsDefined(selectedMode) == false)
			{
				throw new ArgumentException("Sky mode must be Procedural, Hdri or Gradient.", nameof(mode));
			}

			var config = GetActiveRenderConfig();
			var sky = config.Sky;
			sky.Mode = selectedMode;
			if (string.IsNullOrWhiteSpace(lightingQuality) == false)
			{
				if (Enum.TryParse<SkyLightingQuality>(lightingQuality, true, out var quality) == false ||
				    Enum.IsDefined(quality) == false)
				{
					throw new ArgumentException("Sky lighting quality must be High or Low.", nameof(lightingQuality));
				}

				sky.LightingQuality = quality;
			}

			var hdri = sky.Hdri;
			if (clearHdri)
			{
				hdri.Texture = default;
			}
			else if (string.IsNullOrWhiteSpace(hdriAssetPath) == false)
			{
				hdri.Texture = new AssetRef<Texture> { NodeId = ResolveTextureAsset(hdriAssetPath).Id };
			}

			if (hdriRotationDegrees.HasValue)
			{
				hdri.RotationDegrees = hdriRotationDegrees.Value;
			}

			if (hdriIntensity.HasValue)
			{
				hdri.Intensity = hdriIntensity.Value;
			}

			sky.Hdri = hdri;

			if (gradientIntensity.HasValue)
			{
				var gradient = sky.Gradient;
				gradient.Intensity = gradientIntensity.Value;
				sky.Gradient = gradient;
			}

			sky.Procedural = procedural.ApplyTo(sky.Procedural);
			config.Sky = sky;

			return $"Sky: {sky.Mode}, lighting quality: {sky.LightingQuality}, HDRI: {(sky.Hdri.Texture.IsValid ? sky.Hdri.Texture.NodeId.ToString("D") : "none")}, " +
			       $"rotation: {sky.Hdri.RotationDegrees}, HDRI intensity: {sky.Hdri.Intensity}, " +
			       $"gradient intensity: {sky.Gradient.Intensity}. Changed in memory only.";
		}, cancellationToken);

	/// <summary>
	/// Points the active scene's first directional light at the sun's elevation and azimuth, in memory. Azimuth 0
	/// puts the sun toward +Z and 90 toward +X. A light left without intensity keeps its own.
	/// </summary>
	public Task<string> SetSunAsync(float elevationDegrees, float azimuthDegrees, float? intensity, CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			var scene = _playSession.RuntimeScene ?? _sceneWorkspace.CurrentScene;
			foreach (var entry in scene.World.View<Light>())
			{
				ref var light = ref entry.First;
				if (light.Type != LightType.Directional)
				{
					continue;
				}

				if (intensity.HasValue)
				{
					light.Intensity = intensity.Value;
				}

				var elevation = elevationDegrees * (MathF.PI / 180.0f);
				var azimuth = azimuthDegrees * (MathF.PI / 180.0f);
				var towardSun = new Vector3(
					MathF.Cos(elevation) * MathF.Sin(azimuth),
					MathF.Sin(elevation),
					MathF.Cos(elevation) * MathF.Cos(azimuth));
				var forward = -towardSun;

				// The light shines along its local +Z: pitch tips +Z down by -forward.Y, then yaw turns it around +Y.
				var pitch = -MathF.Asin(Math.Clamp(forward.Y, -1.0f, 1.0f));
				var yaw = MathF.Atan2(forward.X, forward.Z);
				scene.World.SetLocalRotation(entry.Entity, Quaternion.CreateFromYawPitchRoll(yaw, pitch, 0.0f));

				return $"Sun at elevation {elevationDegrees}, azimuth {azimuthDegrees}, intensity {light.Intensity}. Changed in memory only.";
			}

			throw new InvalidOperationException("The active scene has no directional light.");
		}, cancellationToken);

	/// <summary>
	/// Changes the active scene's volumetric clouds in memory, without saving its render config. Settings left null
	/// keep their current values.
	/// </summary>
	public Task<string> SetCloudsAsync(CloudOverrides overrides, CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			var config = GetActiveRenderConfig();
			var clouds = overrides.ApplyTo(config.Clouds);
			config.Clouds = clouds;

			return $"Clouds: enabled {clouds.Enabled}, coverage {clouds.Coverage}, density {clouds.Density}, " +
			       $"base {clouds.CloudBaseAltitude} m, thickness {clouds.CloudThickness} m, shape scale {clouds.ShapeScale}, " +
			       $"detail {clouds.DetailStrength}, wind {clouds.WindSpeed} m/s toward {clouds.WindDirection}, " +
			       $"ambient {clouds.AmbientStrength}, multiple scattering {clouds.MultipleScattering}, " +
			       $"quality {clouds.Quality}, shadows {clouds.CastShadows}. Changed in memory only.";
		}, cancellationToken);

	/// <summary>Sets the active scene's tonemapping exposure in memory, without saving its render config.</summary>
	public Task<string> SetTonemappingExposureAsync(float exposure, CancellationToken cancellationToken) =>
		Enqueue(() =>
		{
			var config = GetActiveRenderConfig();
			var tonemapping = config.Tonemapping;
			tonemapping.Exposure = exposure;
			config.Tonemapping = tonemapping;

			return $"Tonemapping exposure: {exposure}. Changed in memory only.";
		}, cancellationToken);

	private RenderConfig GetActiveRenderConfig()
	{
		var scene = _playSession.RuntimeScene ?? _sceneWorkspace.CurrentScene;
		RenderConfig? config = null;
		foreach (var entry in scene.World.View<WorldSettings>())
		{
			config = entry.First.RenderConfigAsset.Asset;
		}

		return config ?? _sceneWorkspace.DefaultRenderConfig;
	}
}

/// <summary>Cloud settings an automation call changes; null leaves a setting as it is.</summary>
public readonly record struct CloudOverrides(
	bool? Enabled,
	float? Coverage,
	float? Density,
	float? CloudBaseAltitude,
	float? CloudThickness,
	float? ShapeScale,
	float? DetailStrength,
	float? WindDirection,
	float? WindSpeed,
	float? AmbientStrength,
	float? MultipleScattering,
	string? Quality,
	bool? CastShadows)
{
	public CloudConfig ApplyTo(CloudConfig clouds)
	{
		if (Enabled.HasValue)
		{
			clouds.Enabled = Enabled.Value;
		}

		if (Coverage.HasValue)
		{
			clouds.Coverage = Coverage.Value;
		}

		if (Density.HasValue)
		{
			clouds.Density = Density.Value;
		}

		if (CloudBaseAltitude.HasValue)
		{
			clouds.CloudBaseAltitude = CloudBaseAltitude.Value;
		}

		if (CloudThickness.HasValue)
		{
			clouds.CloudThickness = CloudThickness.Value;
		}

		if (ShapeScale.HasValue)
		{
			clouds.ShapeScale = ShapeScale.Value;
		}

		if (DetailStrength.HasValue)
		{
			clouds.DetailStrength = DetailStrength.Value;
		}

		if (WindDirection.HasValue)
		{
			clouds.WindDirection = WindDirection.Value;
		}

		if (WindSpeed.HasValue)
		{
			clouds.WindSpeed = WindSpeed.Value;
		}

		if (AmbientStrength.HasValue)
		{
			clouds.AmbientStrength = AmbientStrength.Value;
		}

		if (MultipleScattering.HasValue)
		{
			clouds.MultipleScattering = MultipleScattering.Value;
		}

		if (string.IsNullOrWhiteSpace(Quality) == false)
		{
			if (Enum.TryParse<CloudQuality>(Quality, true, out var quality) == false ||
			    Enum.IsDefined(quality) == false)
			{
				throw new ArgumentException("Cloud quality must be Low, Medium or High.", nameof(Quality));
			}

			clouds.Quality = quality;
		}

		if (CastShadows.HasValue)
		{
			clouds.CastShadows = CastShadows.Value;
		}

		return clouds;
	}
}

/// <summary>Procedural sky settings an automation call changes; null leaves a setting as it is.</summary>
public readonly record struct ProceduralSkyOverrides(
	float? MieScatteringScale,
	float? MieAbsorptionScale,
	float? MieAnisotropy,
	float? RayleighScatteringScale,
	float? GroundAlbedo)
{
	public ProceduralSkyConfig ApplyTo(ProceduralSkyConfig config)
	{
		if (MieScatteringScale.HasValue)
		{
			config.MieScatteringScale = MieScatteringScale.Value;
		}

		if (MieAbsorptionScale.HasValue)
		{
			config.MieAbsorptionScale = MieAbsorptionScale.Value;
		}

		if (MieAnisotropy.HasValue)
		{
			config.MieAnisotropy = MieAnisotropy.Value;
		}

		if (RayleighScatteringScale.HasValue)
		{
			config.RayleighScatteringScale = RayleighScatteringScale.Value;
		}

		if (GroundAlbedo.HasValue)
		{
			config.GroundAlbedo = new ColorRGBA(GroundAlbedo.Value, GroundAlbedo.Value, GroundAlbedo.Value, 1.0f);
		}

		return config;
	}
}
