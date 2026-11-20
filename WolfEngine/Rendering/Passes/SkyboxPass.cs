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
	private readonly IShaderProvider _shaderCompiler;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private DescriptorHandle _skyboxSamplerHandle = DescriptorHandle.Invalid;
	private IGfxPipeline? _iblIrradiancePipeline;
	private IGfxPipeline? _iblPrefilterPipeline;
	private IGfxPipeline? _iblBrdfLutPipeline;
	private IGfxPipeline? _gradientSkyPipeline;
	private GraphicsBackendKind? _reflectionBackendKind;
	private ComputeThreadGroupSize? _iblIrradianceThreadGroupSize;
	private ComputeThreadGroupSize? _iblPrefilterThreadGroupSize;
	private ComputeThreadGroupSize? _iblBrdfThreadGroupSize;
	private ComputeThreadGroupSize? _gradientSkyThreadGroupSize;
	private ShaderPropertyWriter? _iblIrradianceWriter;
	private ShaderPropertyWriter? _iblPrefilterWriter;
	private ShaderPropertyWriter? _iblBrdfWriter;
	private ShaderPropertyWriter? _gradientBindlessWriter;
	private ShaderPropertyWriter? _gradientParamsWriter;
	// The BRDF LUT depends on nothing about the sky, so every view shares this one.
	private IGfxTexture? _brdfLut;
	private bool _brdfLutValid;
	private bool _hasGeneratedBrdfLut;

	public SkyboxPass(IRenderer renderer, IShaderProvider shaderCompiler, BindlessResourceRegistry bindlessRegistry)
	{
		_renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
		Atmosphere = new AtmospherePass(renderer, shaderCompiler, bindlessRegistry);
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
				BaseSize = EnvironmentWidth,
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
		RecordIrradiance(context.CommandList, gfxDevice, binding, irradiance, GetSkyboxSamplerHandle(gfxDevice));
	}

	internal void RecordPrefilter(RenderGraphContext context, ViewSky sky, in SkyEnvironmentSource source)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(sky);

		var prefilter = sky.Prefilter ?? throw new InvalidOperationException("Sky prefilter texture was not created.");
		var gfxDevice = GetGfxDevice();
		var binding = source.Resolve(context, _bindlessRegistry);
		RecordPrefilter(context.CommandList, gfxDevice, binding, prefilter, GetSkyboxSamplerHandle(gfxDevice));

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
		var pipeline = GetGradientSkyPipeline(GetGfxDevice());
		commandList.BindPipeline(pipeline);

		var bindlessWriter = _gradientBindlessWriter
			?? throw new InvalidOperationException("Gradient sky bindless writer was not initialized.");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("environmentHandle", environment.UnorderedAccessView.Value);
		bindlessWriter.SetUInt("width", (uint)EnvironmentWidth);
		bindlessWriter.SetUInt("height", (uint)EnvironmentHeight);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		var paramsWriter = _gradientParamsWriter
			?? throw new InvalidOperationException("Gradient sky parameter writer was not initialized.");
		paramsWriter.Clear();
		paramsWriter.SetColorRGBA("zenithColor", config.ZenithColor);
		paramsWriter.SetColorRGBA("horizonColor", config.HorizonColor);
		paramsWriter.SetColorRGBA("groundColor", config.GroundColor);
		paramsWriter.SetVector4("shape", new Vector4(config.SkyExponent, config.GroundExponent, config.Intensity, 0.0f));
		commandList.SetComputeConstants(paramsWriter.RegisterIndex, paramsWriter.AsBytes());

		var threadGroupSize = _gradientSkyThreadGroupSize
			?? throw new InvalidOperationException("Gradient sky threadgroup size was not initialized.");
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(EnvironmentWidth, EnvironmentHeight);
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}

	private void RecordIrradiance(
		IGfxCommandList commandList,
		IGfxDevice gfxDevice,
		in SkyEnvironmentBinding source,
		IGfxTexture irradianceTex,
		DescriptorHandle samplerHandle)
	{
		var pipeline = GetIblIrradiancePipeline(gfxDevice);
		commandList.BindPipeline(pipeline);
		var writer = _iblIrradianceWriter
			?? throw new InvalidOperationException("IBL irradiance reflection writer was not initialized.");
		writer.Clear();
		source.Write(writer);
		writer.SetUInt("irradianceHandle", irradianceTex.UnorderedAccessView.Value);
		writer.SetUInt("samplerHandle", samplerHandle.Value);
		writer.SetUInt("width", IrradianceSize);
		writer.SetUInt("height", IrradianceSize);
		commandList.SetComputeConstants(writer.RegisterIndex, writer.AsBytes());
		var threadGroupSize = _iblIrradianceThreadGroupSize
			?? throw new InvalidOperationException("IBL irradiance threadgroup size was not initialized.");
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(IrradianceSize, IrradianceSize);
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}

	private void RecordPrefilter(
		IGfxCommandList commandList,
		IGfxDevice gfxDevice,
		in SkyEnvironmentBinding source,
		IGfxTexture prefilterTex,
		DescriptorHandle samplerHandle)
	{
		var pipeline = GetIblPrefilterPipeline(gfxDevice);
		commandList.BindPipeline(pipeline);
		var writer = _iblPrefilterWriter
			?? throw new InvalidOperationException("IBL prefilter reflection writer was not initialized.");
		writer.Clear();
		source.Write(writer);
		writer.SetUInt("prefilterHandle", prefilterTex.UnorderedAccessView.Value);
		writer.SetUInt("samplerHandle", samplerHandle.Value);
		writer.SetUInt("width", PrefilterWidth);
		writer.SetUInt("height", PrefilterSliceHeight * PrefilterSlices);
		writer.SetUInt("sliceCount", PrefilterSlices);
		writer.SetUInt("sliceHeight", PrefilterSliceHeight);
		commandList.SetComputeConstants(writer.RegisterIndex, writer.AsBytes());
		var threadGroupSize = _iblPrefilterThreadGroupSize
			?? throw new InvalidOperationException("IBL prefilter threadgroup size was not initialized.");
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(
			PrefilterWidth,
			PrefilterSliceHeight * PrefilterSlices);
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}

	private void RecordBrdfLut(IGfxCommandList commandList, IGfxDevice gfxDevice, IGfxTexture brdfTex)
	{
		var pipeline = GetIblBrdfPipeline(gfxDevice);
		commandList.BindPipeline(pipeline);
		var writer = _iblBrdfWriter
			?? throw new InvalidOperationException("IBL BRDF reflection writer was not initialized.");
		writer.Clear();
		writer.SetUInt("brdfHandle", brdfTex.UnorderedAccessView.Value);
		commandList.SetComputeConstants(writer.RegisterIndex, writer.AsBytes());
		var threadGroupSize = _iblBrdfThreadGroupSize
			?? throw new InvalidOperationException("IBL BRDF threadgroup size was not initialized.");
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
		if (sky.HasLightingTextures == false)
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
				PrefilterWidth,
				PrefilterSliceHeight * PrefilterSlices,
				TextureFormat.Rgba16Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess));
			created = true;
		}

		if (sky.DrawsEnvironment && sky.Environment is null)
		{
			sky.Environment = gfxDevice.CreateTexture(new TextureDescriptor(
				EnvironmentWidth,
				EnvironmentHeight,
				TextureFormat.Rgba16Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess));
			created = true;
		}
		else if (sky.DrawsEnvironment == false && sky.Environment is not null)
		{
			sky.ReleaseEnvironment();
		}

		return created;
	}

	/// <summary>Whether what the sky would be generated from now differs from what it was last generated from.</summary>
	private static bool SkyChanged(ViewSky sky)
	{
		var current = sky.Config;
		var generated = sky.GeneratedConfig;
		if (current.Mode != generated.Mode)
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

	private IGfxPipeline GetGradientSkyPipeline(IGfxDevice gfxDevice)
	{
		if (_gradientSkyPipeline is not null)
		{
			return _gradientSkyPipeline;
		}

		var compiled = CompileComputeWithReflection(EngineShaderPrograms.GradientSky, "GradientSkyCSMain", gfxDevice.BackendKind);
		_gradientSkyThreadGroupSize = compiled.ThreadGroupSize;
		_gradientBindlessWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("BindlessHandles"));
		_gradientParamsWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("GradientParams"));
		var shaders = new ShaderBytecodeSet(compute: compiled.Bytecode, computeThreadGroupSize: _gradientSkyThreadGroupSize);

		var pipelineKey = new PipelineKey(
			PassKind.Compute,
			vertexEntryPoint: null,
			pixelEntryPoint: null,
			computeEntryPoint: "GradientSkyCSMain",
			renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: default,
			layout: GraphicsLayoutKind.Default);
		_gradientSkyPipeline = gfxDevice.GetOrCreatePipeline(pipelineKey, shaders);
		return _gradientSkyPipeline;
	}

	private IGfxPipeline GetIblIrradiancePipeline(IGfxDevice gfxDevice)
	{
		if (_iblIrradiancePipeline is not null)
		{
			return _iblIrradiancePipeline;
		}

		var compiled = CompileComputeWithReflection(EngineShaderPrograms.IblIrradiance, "IblIrradianceCSMain", gfxDevice.BackendKind);
		_iblIrradianceThreadGroupSize = compiled.ThreadGroupSize;
		_iblIrradianceWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("BindlessHandles"));
		var shaders = new ShaderBytecodeSet(compute: compiled.Bytecode, computeThreadGroupSize: _iblIrradianceThreadGroupSize);

		var pipelineKey = new PipelineKey(
			PassKind.Compute,
			vertexEntryPoint: null,
			pixelEntryPoint: null,
			computeEntryPoint: "IblIrradianceCSMain",
			renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: default,
			layout: GraphicsLayoutKind.Default);
		_iblIrradiancePipeline = gfxDevice.GetOrCreatePipeline(pipelineKey, shaders);
		return _iblIrradiancePipeline;
	}

	private IGfxPipeline GetIblPrefilterPipeline(IGfxDevice gfxDevice)
	{
		if (_iblPrefilterPipeline is not null)
		{
			return _iblPrefilterPipeline;
		}

		var compiled = CompileComputeWithReflection(EngineShaderPrograms.IblPrefilter, "IblPrefilterCSMain", gfxDevice.BackendKind);
		_iblPrefilterThreadGroupSize = compiled.ThreadGroupSize;
		_iblPrefilterWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("BindlessHandles"));
		var shaders = new ShaderBytecodeSet(compute: compiled.Bytecode, computeThreadGroupSize: _iblPrefilterThreadGroupSize);

		var pipelineKey = new PipelineKey(
			PassKind.Compute,
			vertexEntryPoint: null,
			pixelEntryPoint: null,
			computeEntryPoint: "IblPrefilterCSMain",
			renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: default,
			layout: GraphicsLayoutKind.Default);
		_iblPrefilterPipeline = gfxDevice.GetOrCreatePipeline(pipelineKey, shaders);
		return _iblPrefilterPipeline;
	}

	private IGfxPipeline GetIblBrdfPipeline(IGfxDevice gfxDevice)
	{
		if (_iblBrdfLutPipeline is not null)
		{
			return _iblBrdfLutPipeline;
		}

		var compiled = CompileComputeWithReflection(EngineShaderPrograms.IblBrdfLut, "IblBrdfCSMain", gfxDevice.BackendKind);
		_iblBrdfThreadGroupSize = compiled.ThreadGroupSize;
		_iblBrdfWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("BindlessHandles"));
		var shaders = new ShaderBytecodeSet(compute: compiled.Bytecode, computeThreadGroupSize: _iblBrdfThreadGroupSize);

		var pipelineKey = new PipelineKey(
			PassKind.Compute,
			vertexEntryPoint: null,
			pixelEntryPoint: null,
			computeEntryPoint: "IblBrdfCSMain",
			renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: default,
			layout: GraphicsLayoutKind.Default);
		_iblBrdfLutPipeline = gfxDevice.GetOrCreatePipeline(pipelineKey, shaders);
		return _iblBrdfLutPipeline;
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

	private CompiledComputeShaderWithReflection CompileComputeWithReflection(
		ShaderProgramId shaderProgram,
		string entryPoint,
		GraphicsBackendKind backendKind)
	{
		if (_reflectionBackendKind.HasValue && _reflectionBackendKind.Value != backendKind)
		{
			throw new InvalidOperationException(
				$"SkyboxPass was already compiled for backend '{_reflectionBackendKind.Value}', but was requested for '{backendKind}'.");
		}

		_reflectionBackendKind = backendKind;
		return _shaderCompiler.GetComputeShaderWithReflection(shaderProgram, entryPoint, backendKind);
	}
}
