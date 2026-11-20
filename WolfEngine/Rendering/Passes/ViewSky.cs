using System.Numerics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Atmosphere;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// One view's sky: the environment it draws, when its mode draws one, and the image-based lighting derived from
/// its sky. Each view owns one, so a preview world lit by its own sun or sky settings does not show the scene
/// view's sky. <see cref="SkyboxPass"/> regenerates it only when its settings, its sun and altitude (Procedural
/// mode) or its HDRI texture (HDRI mode) change. In Procedural mode it also owns the atmosphere's LUTs: the
/// transmittance and multiple-scattering LUTs, rebuilt when the atmosphere changes, and the Sky View LUT and
/// aerial perspective volume, rebuilt every frame.
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

	public IGfxTexture? TransmittanceLut { get; set; }
	public IGfxTexture? MultipleScatteringLut { get; set; }
	public IGfxTexture? SkyViewLut { get; set; }
	public IGfxTexture? AerialPerspective { get; set; }

	public Vector3 SunDirection { get; set; } = Vector3.UnitY;
	public SkyConfig Config { get; set; }

	/// <summary>This frame's atmosphere, in Procedural mode.</summary>
	public AtmosphereFrame Atmosphere { get; set; }

	/// <summary>The HDRI to read in HDRI mode, once uploaded; null otherwise, which draws black.</summary>
	public Texture? Hdri { get; set; }
	public IGfxTexture? HdriGpuTexture { get; set; }

	public Vector3 GeneratedSunDirection { get; set; } = Vector3.UnitY;
	public Vector3 GeneratedSunIlluminance { get; set; }
	public float GeneratedCameraAltitudeKm { get; set; }
	public SkyConfig GeneratedConfig { get; set; }
	public IGfxTexture? GeneratedHdriGpuTexture { get; set; }

	/// <summary><see cref="AtmosphereModel.GetLutHash"/> of the atmosphere the transmittance and multiple-scattering LUTs hold.</summary>
	public int GeneratedAtmosphereLutHash { get; set; }

	/// <summary>Whether the transmittance and multiple-scattering LUTs hold this atmosphere, which leaves them shader resources.</summary>
	public bool HasGeneratedAtmosphereLuts { get; set; }

	/// <summary>Whether the Sky View LUT and aerial perspective volume were written, which leaves them shader resources.</summary>
	public bool HasGeneratedAtmosphereViewLuts { get; set; }

	/// <summary>Whether this frame must rebuild the transmittance and multiple-scattering LUTs.</summary>
	public bool ShouldRecordAtmosphereLuts { get; set; }

	/// <summary>Whether <see cref="Environment"/> holds a drawn sky, which leaves it in the shader-resource state.</summary>
	public bool HasGeneratedEnvironment { get; set; }

	/// <summary>Whether the irradiance and prefilter maps hold lighting, which leaves them in the shader-resource state.</summary>
	public bool HasGeneratedLighting { get; set; }

	/// <summary>Whether this frame must regenerate the sky and its lighting before the view reads them.</summary>
	public bool ShouldRecordLightingUpdate { get; set; }

	public bool DrawsEnvironment => Config.Mode != SkyConfigMode.Hdri;

	public bool UsesAtmosphere => Config.Mode == SkyConfigMode.Procedural;

	public ResourceState AtmosphereLutsInitialState => HasGeneratedAtmosphereLuts
		? ResourceState.ShaderResource
		: ResourceState.UnorderedAccess;

	public ResourceState AtmosphereViewLutsInitialState => HasGeneratedAtmosphereViewLuts
		? ResourceState.ShaderResource
		: ResourceState.UnorderedAccess;

	public bool HasAtmosphereTextures => TransmittanceLut is not null &&
	                                     MultipleScatteringLut is not null &&
	                                     SkyViewLut is not null &&
	                                     AerialPerspective is not null;

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
	/// Retires the atmosphere's LUTs, for a mode without an atmosphere. A frame in flight may still read them, so
	/// they go through the device's retirement queue.
	/// </summary>
	public void ReleaseAtmosphere()
	{
		RetireTexture(TransmittanceLut, HasGeneratedAtmosphereLuts);
		RetireTexture(MultipleScatteringLut, HasGeneratedAtmosphereLuts);
		RetireTexture(SkyViewLut, HasGeneratedAtmosphereViewLuts);
		RetireTexture(AerialPerspective, HasGeneratedAtmosphereViewLuts);
		TransmittanceLut = null;
		MultipleScatteringLut = null;
		SkyViewLut = null;
		AerialPerspective = null;
		HasGeneratedAtmosphereLuts = false;
		HasGeneratedAtmosphereViewLuts = false;
		ShouldRecordAtmosphereLuts = false;
	}

	/// <summary>
	/// Retires every texture of the sky through the device's retirement queue, since a frame in flight may still
	/// read them. The next use creates and regenerates new ones.
	/// </summary>
	public void Release()
	{
		ReleaseEnvironment();
		ReleaseAtmosphere();
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
