using System.Numerics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Atmosphere;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// Draws each view's sky environment (Procedural and Gradient mode) and derives its image-based lighting from
/// whichever texture the sky's radiance lives in, plus the BRDF LUT that every view shares.
/// </summary>
public sealed class SkyboxPass
{
	private const int EnvironmentWidth = 2048;
	private const int EnvironmentHeight = 1024;

	/// <summary>
	/// The Procedural environment at Low quality. Only image-based lighting and DDGI read it, and the atmosphere is
	/// low-frequency, so it does not need the size of an environment the visible sky is drawn from.
	/// </summary>
	private const int LowQualityEnvironmentWidth = 256;
	private const int LowQualityEnvironmentHeight = 128;

	/// <summary>
	/// The prefilter atlas at Low quality in Procedural mode: half the size, since its low-resolution environment
	/// has no detail for a larger atlas to keep. Consumers address the atlas by UV, so any size works.
	/// </summary>
	private const int LowQualityPrefilterWidth = 128;
	private const int LowQualityPrefilterSliceHeight = 32;

	private const uint HighQualityIrradianceSamples = 1024;
	private const uint LowQualityIrradianceSamples = 128;
	private const uint HighQualityPrefilterSamples = 128;
	private const uint LowQualityPrefilterSamples = 64;
	private const int IrradianceSize = 64;
	private const int PrefilterWidth = 256;
	private const int PrefilterSliceHeight = 64;
	private const int PrefilterSlices = 6;
	private const int BrdfSize = 256;
	private const float SunDirectionEpsilonSquared = 1e-6f;
	private const float SunIlluminanceEpsilon = 1e-4f;

	/// <summary>The Procedural environment is rebuilt when the camera climbs or descends by more than this, in km...</summary>
	private const float MinAltitudeChangeKm = 0.01f;

	/// <summary>...or by more than this fraction of its altitude, whichever is larger.</summary>
	private const float RelativeAltitudeChange = 0.02f;

	private readonly IRenderer _renderer;
	private readonly ComputeKernel _gradientSky;
	private readonly ComputeKernel _iblIrradiance;
	private readonly ComputeKernel _iblPrefilter;
	private readonly ComputeKernel _iblBrdfLut;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private DescriptorHandle _skyboxSamplerHandle = DescriptorHandle.Invalid;
	// The BRDF LUT depends on nothing about the sky, so every view shares this one.
	private IGfxTexture? _brdfLut;
	private bool _brdfLutValid;
	private bool _hasGeneratedBrdfLut;
	private readonly Action<RenderGraphContext> _environmentExecute;
	private readonly Action<RenderGraphContext> _irradianceExecute;
	private readonly Action<RenderGraphContext> _prefilterExecute;
	private readonly Action<RenderGraphContext> _brdfLutExecute;

	public SkyboxPass(IRenderer renderer, ShaderSource shaderSource, BindlessResourceRegistry bindlessRegistry)
	{
		_renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
		ArgumentNullException.ThrowIfNull(shaderSource);

		_gradientSky = new ComputeKernel(shaderSource, EngineShaderPrograms.GradientSky, "GradientSkyCSMain");
		_iblIrradiance = new ComputeKernel(shaderSource, EngineShaderPrograms.IblIrradiance, "IblIrradianceCSMain");
		_iblPrefilter = new ComputeKernel(shaderSource, EngineShaderPrograms.IblPrefilter, "IblPrefilterCSMain");
		_iblBrdfLut = new ComputeKernel(shaderSource, EngineShaderPrograms.IblBrdfLut, "IblBrdfCSMain");
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
		Atmosphere = new AtmospherePass(renderer, shaderSource, bindlessRegistry);
		_environmentExecute = context => RecordEnvironment(context, context.ViewState.Sky);
		_irradianceExecute = context => RecordIrradiance(context, context.ViewState.Sky, context.ViewState.FrameResources.SkyEnvironment);
		_prefilterExecute = context => RecordPrefilter(context, context.ViewState.Sky, context.ViewState.FrameResources.SkyEnvironment);
		_brdfLutExecute = RecordBrdfLut;
	}

	/// <summary>Adds the Skybox BRDF LUT node, shared by every view because the LUT depends on nothing about the sky.</summary>
	internal void AddBrdfLutNodes(RenderGraph graph, in RenderFrameSharedResources sharedResources)
	{
		graph.AddPass("Skybox BRDF LUT", PassKind.Compute)
			.WriteTexture(sharedResources.SkyboxBrdfLut, ResourceState.UnorderedAccess)
			.SetExecute(_brdfLutExecute);
	}

	/// <summary>Adds the Skybox Environment node, which draws the view's sky into its environment texture.</summary>
	internal void AddEnvironmentNodes(RenderGraph graph, in RenderViewResources resources)
	{
		var builder = graph.AddPass("Skybox Environment", PassKind.Compute);
		if (resources.Atmosphere.SkyView.IsValid)
		{
			builder.ReadTexture(resources.Atmosphere.SkyView, ResourceState.ShaderResource);
		}

		builder
			.WriteTexture(resources.SkyEnvironment.ViewEnvironment, ResourceState.UnorderedAccess)
			.SetExecute(_environmentExecute);
	}

	/// <summary>Adds the image-based lighting built from the view's environment: irradiance and the prefiltered radiance.</summary>
	internal void AddImageLightingNodes(RenderGraph graph, in RenderViewResources resources)
	{
		var environment = resources.SkyEnvironment.ViewEnvironment;
		var irradianceBuilder = graph.AddPass("Skybox Irradiance", PassKind.Compute);
		if (environment.IsValid)
		{
			irradianceBuilder.ReadTexture(environment, ResourceState.ShaderResource);
		}

		irradianceBuilder
			.WriteTexture(resources.SkyboxIrradiance, ResourceState.UnorderedAccess)
			.SetExecute(_irradianceExecute);

		var prefilterBuilder = graph.AddPass("Skybox Prefilter", PassKind.Compute);
		if (environment.IsValid)
		{
			prefilterBuilder.ReadTexture(environment, ResourceState.ShaderResource);
		}

		prefilterBuilder
			.WriteTexture(resources.SkyboxPrefilter, ResourceState.UnorderedAccess)
			.SetExecute(_prefilterExecute);
	}

	/// <summary>The Procedural sky's atmosphere LUTs and environment.</summary>
	internal AtmospherePass Atmosphere { get; }

	public bool ShouldRecordBrdfLutUpdate { get; private set; }

	public ResourceState BrdfLutInitialState => _hasGeneratedBrdfLut
		? ResourceState.ShaderResource
		: ResourceState.UnorderedAccess;

	public IGfxTexture BrdfLut => _brdfLut
		?? throw new InvalidOperationException("BRDF LUT texture was not created.");

	/// <summary>Creates the shared BRDF LUT on first use and reports whether this frame must generate it.</summary>
	public void PrepareBrdfLut(IGfxDevice gfxDevice)
	{
		ArgumentNullException.ThrowIfNull(gfxDevice);

		if (_brdfLut is null)
		{
			_brdfLut = gfxDevice.CreateTexture(new TextureDescriptor(
				BrdfSize,
				BrdfSize,
				TextureFormat.Rgba16Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess));
			_brdfLutValid = false;
			_hasGeneratedBrdfLut = false;
		}

		ShouldRecordBrdfLutUpdate = _brdfLutValid == false;
	}

	/// <summary>
	/// Creates <paramref name="sky"/>'s textures for its mode and records this frame's sun, camera, settings and
	/// HDRI, setting <see cref="ViewSky.ShouldRecordLightingUpdate"/> when they differ from what it was last
	/// generated with, and <see cref="ViewSky.ShouldRecordAtmosphereLuts"/> when the atmosphere itself changed.
	/// </summary>
	internal void PrepareSky(
		IGfxDevice gfxDevice,
		ViewSky sky,
		Vector3 sunDirection,
		Vector3 sunIlluminance,
		Vector3 cameraPosition,
		SkyConfig config,
		Texture? hdri)
	{
		ArgumentNullException.ThrowIfNull(gfxDevice);
		ArgumentNullException.ThrowIfNull(sky);

		sky.Config = config;
		sky.SunDirection = sunDirection == Vector3.Zero
			? Vector3.UnitY
			: Vector3.Normalize(sunDirection);
		sky.Hdri = config.Mode == SkyConfigMode.Hdri
			? GetUploadedHdri(hdri)
			: null;
		sky.HdriGpuTexture = sky.Hdri?.Resources?.Texture;

		var createdResources = EnsureSkyTextures(gfxDevice, sky);
		PrepareAtmosphere(gfxDevice, sky, sunIlluminance, cameraPosition);

		sky.ShouldRecordLightingUpdate = createdResources ||
			sky.HasGeneratedLighting == false ||
			SkyChanged(sky);
	}

	/// <summary>Where <paramref name="sky"/>'s radiance is read from, given its environment's graph handle.</summary>
	internal static SkyEnvironmentSource GetEnvironmentSource(ViewSky sky, RenderGraphResourceHandle environment)
	{
		ArgumentNullException.ThrowIfNull(sky);

		if (sky.DrawsEnvironment)
		{
			return new SkyEnvironmentSource
			{
				ViewEnvironment = environment,
				IsCube = false,
				MipCount = 1,
				BaseSize = sky.Environment?.Descriptor.Width ?? EnvironmentWidth,
				RotationRadians = 0.0f,
				Intensity = 1.0f
			};
		}

		var hdriConfig = sky.Config.Hdri;
		var hdri = sky.Hdri;
		if (hdri is null || sky.HdriGpuTexture is null)
		{
			return default;
		}

		return new SkyEnvironmentSource
		{
			HdriTexture = sky.HdriGpuTexture,
			IsCube = hdri.Dimension == TextureDimension.Cube,
			MipCount = hdri.MipCount,
			BaseSize = hdri.Width,
			RotationRadians = hdriConfig.RotationDegrees * (MathF.PI / 180.0f),
			Intensity = MathF.Max(hdriConfig.Intensity, 0.0f)
		};
	}

	/// <summary>Draws the Procedural or Gradient sky into the view's environment.</summary>
	internal void RecordEnvironment(RenderGraphContext context, ViewSky sky)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(sky);

		var environment = sky.Environment ?? throw new InvalidOperationException("Sky environment texture was not created.");
		if (sky.Config.Mode == SkyConfigMode.Gradient)
		{
			RecordGradientEnvironment(context.CommandList, environment, sky.Config.Gradient);
		}
		else
		{
			Atmosphere.RecordEnvironment(context.CommandList, sky, environment);
		}

		sky.HasGeneratedEnvironment = true;
	}

	internal void RecordIrradiance(RenderGraphContext context, ViewSky sky, in SkyEnvironmentSource source)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(sky);

		var irradiance = sky.Irradiance ?? throw new InvalidOperationException("Sky irradiance texture was not created.");
		var gfxDevice = GetGfxDevice();
		var binding = source.Resolve(context, _bindlessRegistry);
		var sampleCount = sky.Config.LightingQuality == SkyLightingQuality.Low
			? LowQualityIrradianceSamples
			: HighQualityIrradianceSamples;
		RecordIrradiance(context.CommandList, gfxDevice, binding, irradiance, GetSkyboxSamplerHandle(gfxDevice), sampleCount);
	}

	internal void RecordPrefilter(RenderGraphContext context, ViewSky sky, in SkyEnvironmentSource source)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(sky);

		var prefilter = sky.Prefilter ?? throw new InvalidOperationException("Sky prefilter texture was not created.");
		var gfxDevice = GetGfxDevice();
		var binding = source.Resolve(context, _bindlessRegistry);
		var sampleCount = sky.Config.LightingQuality == SkyLightingQuality.Low
			? LowQualityPrefilterSamples
			: HighQualityPrefilterSamples;
		RecordPrefilter(context.CommandList, gfxDevice, binding, prefilter, GetSkyboxSamplerHandle(gfxDevice), sampleCount);

		sky.HasGeneratedLighting = true;
		sky.GeneratedSunDirection = sky.SunDirection;
		sky.GeneratedSunIlluminance = sky.Atmosphere.SunIlluminance;
		sky.GeneratedCameraAltitudeKm = sky.Atmosphere.CameraAltitudeKm;
		sky.GeneratedConfig = sky.Config;
		sky.GeneratedHdriGpuTexture = sky.HdriGpuTexture;
	}

	public void RecordBrdfLut(RenderGraphContext context)
	{
		ArgumentNullException.ThrowIfNull(context);

		var brdfLut = _brdfLut ?? throw new InvalidOperationException("BRDF LUT texture was not created.");
		RecordBrdfLut(context.CommandList, GetGfxDevice(), brdfLut);
		_brdfLutValid = true;
		_hasGeneratedBrdfLut = true;
	}

	/// <summary>
	/// In Procedural mode, sets this frame's atmosphere, creates its LUTs and flags them for a rebuild when the
	/// atmosphere changed. Other modes release the LUTs.
	/// </summary>
	private static void PrepareAtmosphere(IGfxDevice gfxDevice, ViewSky sky, Vector3 sunIlluminance, Vector3 cameraPosition)
	{
		if (sky.UsesAtmosphere == false)
		{
			sky.ReleaseAtmosphere();
			return;
		}

		var model = new AtmosphereModel(sky.Config.Procedural);
		sky.Atmosphere = new AtmosphereFrame(model, cameraPosition.Y, sky.SunDirection, sunIlluminance);

		var created = AtmospherePass.EnsureTextures(gfxDevice, sky);
		sky.ShouldRecordAtmosphereLuts = created ||
			sky.HasGeneratedAtmosphereLuts == false ||
			sky.GeneratedAtmosphereLutHash != model.GetLutHash();
	}

	/// <summary>
	/// The HDRI once its upload has published a view, or null. A 3D texture cannot hold a sky and reads as black.
	/// </summary>
	private static Texture? GetUploadedHdri(Texture? hdri)
	{
		if (hdri is null) return null;
		if (hdri.Dimension == TextureDimension.Texture3D) return null;

		var resources = hdri.HasGpuResources
			? hdri.Resources
			: null;
		if (resources is null || resources.ShaderResourceView.IsValid == false)
		{
			return null;
		}

		return hdri;
	}

	private void RecordGradientEnvironment(IGfxCommandList commandList, IGfxTexture environment, in GradientSkyConfig config)
	{
		var pipeline = _gradientSky.GetPipeline(GetGfxDevice());
		commandList.BindPipeline(pipeline);

		var bindlessWriter = _gradientSky.Writer("BindlessHandles");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("environmentHandle", environment.UnorderedAccessView.Value);
		bindlessWriter.SetUInt("width", (uint)environment.Descriptor.Width);
		bindlessWriter.SetUInt("height", (uint)environment.Descriptor.Height);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		var paramsWriter = _gradientSky.Writer("GradientParams");
		paramsWriter.Clear();
		paramsWriter.SetColorRGBA("zenithColor", config.ZenithColor);
		paramsWriter.SetColorRGBA("horizonColor", config.HorizonColor);
		paramsWriter.SetColorRGBA("groundColor", config.GroundColor);
		paramsWriter.SetVector4("shape", new Vector4(config.SkyExponent, config.GroundExponent, config.Intensity, 0.0f));
		commandList.SetComputeConstants(paramsWriter.RegisterIndex, paramsWriter.AsBytes());

		var threadGroupSize = _gradientSky.ThreadGroupSize;
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(
			(uint)environment.Descriptor.Width,
			(uint)environment.Descriptor.Height);
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}

	private void RecordIrradiance(
		IGfxCommandList commandList,
		IGfxDevice gfxDevice,
		in SkyEnvironmentBinding source,
		IGfxTexture irradianceTex,
		DescriptorHandle samplerHandle,
		uint sampleCount)
	{
		var pipeline = _iblIrradiance.GetPipeline(gfxDevice);
		commandList.BindPipeline(pipeline);
		var writer = _iblIrradiance.Writer("BindlessHandles");
		writer.Clear();
		source.Write(writer);
		writer.SetUInt("irradianceHandle", irradianceTex.UnorderedAccessView.Value);
		writer.SetUInt("samplerHandle", samplerHandle.Value);
		writer.SetUInt("width", IrradianceSize);
		writer.SetUInt("height", IrradianceSize);
		writer.SetUInt("sampleCount", sampleCount);
		commandList.SetComputeConstants(writer.RegisterIndex, writer.AsBytes());
		var threadGroupSize = _iblIrradiance.ThreadGroupSize;
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(IrradianceSize, IrradianceSize);
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}

	private void RecordPrefilter(
		IGfxCommandList commandList,
		IGfxDevice gfxDevice,
		in SkyEnvironmentBinding source,
		IGfxTexture prefilterTex,
		DescriptorHandle samplerHandle,
		uint sampleCount)
	{
		var pipeline = _iblPrefilter.GetPipeline(gfxDevice);
		commandList.BindPipeline(pipeline);
		var writer = _iblPrefilter.Writer("BindlessHandles");
		writer.Clear();
		source.Write(writer);
		writer.SetUInt("prefilterHandle", prefilterTex.UnorderedAccessView.Value);
		writer.SetUInt("samplerHandle", samplerHandle.Value);
		var prefilterWidth = prefilterTex.Descriptor.Width;
		var prefilterHeight = prefilterTex.Descriptor.Height;
		writer.SetUInt("width", (uint)prefilterWidth);
		writer.SetUInt("height", (uint)prefilterHeight);
		writer.SetUInt("sliceCount", PrefilterSlices);
		writer.SetUInt("sliceHeight", (uint)(prefilterHeight / PrefilterSlices));
		writer.SetUInt("sampleCount", sampleCount);
		commandList.SetComputeConstants(writer.RegisterIndex, writer.AsBytes());
		var threadGroupSize = _iblPrefilter.ThreadGroupSize;
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(
			(uint)prefilterWidth,
			(uint)prefilterHeight);
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}

	private void RecordBrdfLut(IGfxCommandList commandList, IGfxDevice gfxDevice, IGfxTexture brdfTex)
	{
		var pipeline = _iblBrdfLut.GetPipeline(gfxDevice);
		commandList.BindPipeline(pipeline);
		var writer = _iblBrdfLut.Writer("BindlessHandles");
		writer.Clear();
		writer.SetUInt("brdfHandle", brdfTex.UnorderedAccessView.Value);
		commandList.SetComputeConstants(writer.RegisterIndex, writer.AsBytes());
		var threadGroupSize = _iblBrdfLut.ThreadGroupSize;
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(BrdfSize, BrdfSize);
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}

	/// <summary>
	/// Creates the textures <paramref name="sky"/>'s mode needs and retires an environment its mode no longer
	/// draws. Returns whether anything was created, which means the sky has to be generated.
	/// </summary>
	private static bool EnsureSkyTextures(IGfxDevice gfxDevice, ViewSky sky)
	{
		var created = false;
		var (prefilterWidth, prefilterSliceHeight) = GetPrefilterSize(sky.Config);
		var prefilterSizeChanged = sky.Prefilter is not null &&
		                           (sky.Prefilter.Descriptor.Width != prefilterWidth ||
		                            sky.Prefilter.Descriptor.Height != prefilterSliceHeight * PrefilterSlices);
		if (sky.HasLightingTextures == false || prefilterSizeChanged)
		{
			// A partly created sky is released whole, so its textures always come from one device.
			sky.Release();
			sky.SetDevice(gfxDevice);
			sky.Irradiance = gfxDevice.CreateTexture(new TextureDescriptor(
				IrradianceSize,
				IrradianceSize,
				TextureFormat.Rgba16Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess));
			sky.Prefilter = gfxDevice.CreateTexture(new TextureDescriptor(
				prefilterWidth,
				prefilterSliceHeight * PrefilterSlices,
				TextureFormat.Rgba16Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess));
			created = true;
		}

		var (environmentWidth, environmentHeight) = GetEnvironmentSize(sky.Config);
		if (sky.Environment is not null &&
		    (sky.DrawsEnvironment == false ||
		     sky.Environment.Descriptor.Width != environmentWidth ||
		     sky.Environment.Descriptor.Height != environmentHeight))
		{
			sky.ReleaseEnvironment();
		}

		if (sky.DrawsEnvironment && sky.Environment is null)
		{
			sky.Environment = gfxDevice.CreateTexture(new TextureDescriptor(
				environmentWidth,
				environmentHeight,
				TextureFormat.Rgba16Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess));
			created = true;
		}

		return created;
	}

	/// <summary>
	/// The environment a sky mode draws into. Gradient draws its visible sky from it, so it keeps full size at every
	/// quality; the Procedural sky's visible sky comes from its Sky View LUT, so its environment only feeds lighting.
	/// </summary>
	private static (int Width, int Height) GetEnvironmentSize(in SkyConfig config)
	{
		if (config.Mode == SkyConfigMode.Procedural && config.LightingQuality == SkyLightingQuality.Low)
		{
			return (LowQualityEnvironmentWidth, LowQualityEnvironmentHeight);
		}

		return (EnvironmentWidth, EnvironmentHeight);
	}

	/// <summary>The prefilter atlas's width and slice height, smaller where the environment is small.</summary>
	private static (int Width, int SliceHeight) GetPrefilterSize(in SkyConfig config)
	{
		if (config.Mode == SkyConfigMode.Procedural && config.LightingQuality == SkyLightingQuality.Low)
		{
			return (LowQualityPrefilterWidth, LowQualityPrefilterSliceHeight);
		}

		return (PrefilterWidth, PrefilterSliceHeight);
	}

	/// <summary>Whether what the sky would be generated from now differs from what it was last generated from.</summary>
	private static bool SkyChanged(ViewSky sky)
	{
		var current = sky.Config;
		var generated = sky.GeneratedConfig;
		if (current.Mode != generated.Mode || current.LightingQuality != generated.LightingQuality)
		{
			return true;
		}

		switch (current.Mode)
		{
			case SkyConfigMode.Procedural:
			{
				var atmosphere = sky.Atmosphere;
				var sunChanged = Vector3.DistanceSquared(sky.GeneratedSunDirection, sky.SunDirection) > SunDirectionEpsilonSquared ||
				                 Vector3.DistanceSquared(sky.GeneratedSunIlluminance, atmosphere.SunIlluminance) > SunIlluminanceEpsilon * SunIlluminanceEpsilon;
				var altitudeChange = MathF.Abs(atmosphere.CameraAltitudeKm - sky.GeneratedCameraAltitudeKm);
				var altitudeThreshold = MathF.Max(MinAltitudeChangeKm, RelativeAltitudeChange * sky.GeneratedCameraAltitudeKm);

				return sunChanged ||
				       altitudeChange > altitudeThreshold ||
				       atmosphere.Model.GetLutHash() != new AtmosphereModel(generated.Procedural).GetLutHash();
			}

			case SkyConfigMode.Hdri:
			{
				return ReferenceEquals(sky.HdriGpuTexture, sky.GeneratedHdriGpuTexture) == false ||
				       current.Hdri.Texture.NodeId != generated.Hdri.Texture.NodeId ||
				       current.Hdri.RotationDegrees != generated.Hdri.RotationDegrees ||
				       current.Hdri.Intensity != generated.Hdri.Intensity;
			}

			case SkyConfigMode.Gradient:
			{
				return GradientConfigsDiffer(current.Gradient, generated.Gradient);
			}

			default:
			{
				throw new InvalidOperationException($"Unsupported sky mode '{current.Mode}'.");
			}
		}
	}

	private static bool GradientConfigsDiffer(in GradientSkyConfig left, in GradientSkyConfig right)
	{
		return ColorsDiffer(left.ZenithColor, right.ZenithColor) ||
		       ColorsDiffer(left.HorizonColor, right.HorizonColor) ||
		       ColorsDiffer(left.GroundColor, right.GroundColor) ||
		       left.SkyExponent != right.SkyExponent ||
		       left.GroundExponent != right.GroundExponent ||
		       left.Intensity != right.Intensity;
	}

	private static bool ColorsDiffer(in ColorRGBA left, in ColorRGBA right)
	{
		return left.R != right.R || left.G != right.G || left.B != right.B || left.A != right.A;
	}

	private DescriptorHandle GetSkyboxSamplerHandle(IGfxDevice gfxDevice)
	{
		if (_skyboxSamplerHandle.IsValid == false)
		{
			var sampler = new SamplerDescriptor(FilterMode.Trilinear, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp);
			_bindlessRegistry.EnsureInitialized(gfxDevice);
			_skyboxSamplerHandle = _bindlessRegistry.GetSamplerHandle(sampler);
		}

		return _skyboxSamplerHandle;
	}

	private IGfxDevice GetGfxDevice()
	{
		var device = _renderer.GetGfxDevice();
		if (device is null)
		{
			throw new InvalidOperationException("Graphics device is not initialized.");
		}

		return device;
	}
}
