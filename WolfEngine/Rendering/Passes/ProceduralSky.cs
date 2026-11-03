using System.Numerics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// One view's procedural sky: the environment it draws and the image-based lighting derived from it. Each view
/// owns one, so a preview world lit by its own sun does not show the scene view's sky. <see cref="SkyboxPass"/>
/// regenerates it only when its sun or sky settings change.
/// </summary>
internal sealed class ProceduralSky
{
	private IGfxDevice? _device;

	public IGfxTexture? Environment { get; set; }
	public IGfxTexture? Irradiance { get; set; }
	public IGfxTexture? Prefilter { get; set; }

	public Vector3 SunDirection { get; set; } = Vector3.UnitY;
	public float SunIntensityScale { get; set; } = 1.0f;
	public SkyboxPass.Config Config { get; set; }

	public Vector3 GeneratedSunDirection { get; set; } = Vector3.UnitY;
	public float GeneratedSunIntensityScale { get; set; } = 1.0f;
	public SkyboxPass.Config GeneratedConfig { get; set; }
	public bool HasGeneratedContent { get; set; }

	/// <summary>Whether this frame must regenerate the environment and its lighting before the view reads them.</summary>
	public bool ShouldRecordLightingUpdate { get; set; }

	public ResourceState InitialState => HasGeneratedContent
		? ResourceState.ShaderResource
		: ResourceState.UnorderedAccess;

	public bool HasTextures => Environment is not null && Irradiance is not null && Prefilter is not null;

	public void SetDevice(IGfxDevice device)
	{
		_device = device;
	}

	/// <summary>
	/// Retires the sky's textures through the device's retirement queue, since a frame in flight may still read
	/// them. The next use creates and regenerates new ones.
	/// </summary>
	public void Release()
	{
		RetireTexture(Environment);
		RetireTexture(Irradiance);
		RetireTexture(Prefilter);
		Environment = null;
		Irradiance = null;
		Prefilter = null;
		HasGeneratedContent = false;
		ShouldRecordLightingUpdate = false;
	}

	private void RetireTexture(IGfxTexture? texture)
	{
		if (texture is null) return;

		var state = HasGeneratedContent
			? ResourceState.ShaderResource
			: ResourceState.UnorderedAccess;
		TemporalResourceRetirement.RetireTexture(_device, texture, state);
	}
}
