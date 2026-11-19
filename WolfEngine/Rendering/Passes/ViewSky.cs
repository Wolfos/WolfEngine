using System.Numerics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// One view's sky: the environment it draws, when its mode draws one, and the image-based lighting derived from
/// its sky. Each view owns one, so a preview world lit by its own sun or sky settings does not show the scene
/// view's sky. <see cref="SkyboxPass"/> regenerates it only when its settings, its sun (Procedural mode) or its
/// HDRI texture (HDRI mode) change.
/// </summary>
internal sealed class ViewSky
{
	private IGfxDevice? _device;

	/// <summary>
	/// Equirectangular environment that Procedural and Gradient mode draw into. HDRI mode reads its texture
	/// directly, so it has none.
	/// </summary>
	public IGfxTexture? Environment { get; set; }
	public IGfxTexture? Irradiance { get; set; }
	public IGfxTexture? Prefilter { get; set; }

	public Vector3 SunDirection { get; set; } = Vector3.UnitY;
	public float SunIntensityScale { get; set; } = 1.0f;
	public SkyConfig Config { get; set; }

	/// <summary>The HDRI to read in HDRI mode, once uploaded; null otherwise, which draws black.</summary>
	public Texture? Hdri { get; set; }
	public IGfxTexture? HdriGpuTexture { get; set; }

	public Vector3 GeneratedSunDirection { get; set; } = Vector3.UnitY;
	public float GeneratedSunIntensityScale { get; set; } = 1.0f;
	public SkyConfig GeneratedConfig { get; set; }
	public IGfxTexture? GeneratedHdriGpuTexture { get; set; }

	/// <summary>Whether <see cref="Environment"/> holds a drawn sky, which leaves it in the shader-resource state.</summary>
	public bool HasGeneratedEnvironment { get; set; }

	/// <summary>Whether the irradiance and prefilter maps hold lighting, which leaves them in the shader-resource state.</summary>
	public bool HasGeneratedLighting { get; set; }

	/// <summary>Whether this frame must regenerate the sky and its lighting before the view reads them.</summary>
	public bool ShouldRecordLightingUpdate { get; set; }

	public bool DrawsEnvironment => Config.Mode != SkyConfigMode.Hdri;

	public ResourceState EnvironmentInitialState => HasGeneratedEnvironment
		? ResourceState.ShaderResource
		: ResourceState.UnorderedAccess;

	public ResourceState LightingInitialState => HasGeneratedLighting
		? ResourceState.ShaderResource
		: ResourceState.UnorderedAccess;

	public bool HasLightingTextures => Irradiance is not null && Prefilter is not null;

	public void SetDevice(IGfxDevice device)
	{
		_device = device;
	}

	/// <summary>
	/// Retires the environment texture alone, for a mode that reads its sky from elsewhere. A frame in flight may
	/// still read it, so it goes through the device's retirement queue.
	/// </summary>
	public void ReleaseEnvironment()
	{
		RetireTexture(Environment, HasGeneratedEnvironment);
		Environment = null;
		HasGeneratedEnvironment = false;
	}

	/// <summary>
	/// Retires every texture of the sky through the device's retirement queue, since a frame in flight may still
	/// read them. The next use creates and regenerates new ones.
	/// </summary>
	public void Release()
	{
		ReleaseEnvironment();
		RetireTexture(Irradiance, HasGeneratedLighting);
		RetireTexture(Prefilter, HasGeneratedLighting);
		Irradiance = null;
		Prefilter = null;
		HasGeneratedLighting = false;
		ShouldRecordLightingUpdate = false;
	}

	private void RetireTexture(IGfxTexture? texture, bool generated)
	{
		if (texture is null) return;

		var state = generated
			? ResourceState.ShaderResource
			: ResourceState.UnorderedAccess;
		TemporalResourceRetirement.RetireTexture(_device, texture, state);
	}
}
