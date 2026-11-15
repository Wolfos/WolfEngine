using System.Numerics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

public sealed class SkyboxPass
{
	public struct Config
	{
		public Config()
		{
		}

		public float Intensity { get; set; } = 25;
		public Vector3 SunColor { get; set; } = Vector3.One;
		public float SunSharpness { get; set; } = 256;
		public ColorRGBA TopColor { get; set; } = new(0.2f, 0.45f, 0.85f, 1.4f);
		public ColorRGBA HorizonColor { get; set; } = new(0.65f, 0.75f, 0.9f, 1.0f);
		public ColorRGBA GroundColor { get; set; } = new(0.15f, 0.1f, 0.07f, 0.0f);
	}
	
	private const int ProceduralEnvWidth = 2048;
	private const int ProceduralEnvHeight = 1024;
	private const int IrradianceSize = 64;
	private const int PrefilterWidth = 256;
	private const int PrefilterSliceHeight = 64;
	private const int PrefilterSlices = 6;
	private const int BrdfSize = 256;
	private const float SunDirectionEpsilonSquared = 1e-6f;
	private const float SunIntensityScaleEpsilon = 1e-4f;

	private readonly IRenderer _renderer;
	private readonly IShaderProvider _shaderCompiler;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private DescriptorHandle _skyboxSamplerHandle = DescriptorHandle.Invalid;
	private IGfxPipeline? _iblIrradiancePipeline;
	private IGfxPipeline? _iblPrefilterPipeline;
	private IGfxPipeline? _iblBrdfLutPipeline;
	private IGfxPipeline? _proceduralSkyboxPipeline;
	private GraphicsBackendKind? _reflectionBackendKind;
	private ComputeThreadGroupSize? _iblIrradianceThreadGroupSize;
	private ComputeThreadGroupSize? _iblPrefilterThreadGroupSize;
	private ComputeThreadGroupSize? _iblBrdfThreadGroupSize;
	private ComputeThreadGroupSize? _proceduralSkyboxThreadGroupSize;
	private ShaderPropertyWriter? _iblIrradianceWriter;
	private ShaderPropertyWriter? _iblPrefilterWriter;
	private ShaderPropertyWriter? _iblBrdfWriter;
	private ShaderPropertyWriter? _proceduralBindlessWriter;
	private ShaderPropertyWriter? _proceduralSkyParamsWriter;
	// The BRDF LUT depends on nothing about the sky, so every view's procedural sky shares this one.
	private IGfxTexture? _proceduralBrdfLut;
	private bool _proceduralBrdfValid;
	private bool _hasGeneratedBrdfLut;

	public SkyboxPass(IRenderer renderer, IShaderProvider shaderCompiler, BindlessResourceRegistry bindlessRegistry)
	{
		_renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public bool ShouldRecordBrdfLutUpdate { get; private set; }

	public ResourceState BrdfLutInitialState => _hasGeneratedBrdfLut
		? ResourceState.ShaderResource
		: ResourceState.UnorderedAccess;

	public IGfxTexture ProceduralBrdfLut => _proceduralBrdfLut
		?? throw new InvalidOperationException("Procedural BRDF LUT texture was not created.");

	public SkyboxResources CreateSkyboxResourcesIbl(Texture environmentTexture)
	{
		if (environmentTexture is null)
		{
			throw new ArgumentNullException(nameof(environmentTexture));
		}

		if (environmentTexture.Resources is null)
		{
			throw new InvalidOperationException("Environment texture resources were not created.");
		}

		var gfxDevice = GetGfxDevice();
		var samplerHandle = GetSkyboxSamplerHandle(gfxDevice);
		var (irradiance, prefiltered, brdfLut) = GenerateIblMaps(gfxDevice, environmentTexture, samplerHandle);

		return new SkyboxResources
		{
			EnvironmentTexture = environmentTexture.Resources.Texture,
			IrradianceTexture = irradiance,
			PrefilteredEnvironment = prefiltered,
			BrdfLut = brdfLut
		};
	}

	/// <summary>Creates the shared BRDF LUT on first use and reports whether this frame must generate it.</summary>
	public void PrepareBrdfLut(IGfxDevice gfxDevice)
	{
		ArgumentNullException.ThrowIfNull(gfxDevice);

		if (_proceduralBrdfLut is null)
		{
			_proceduralBrdfLut = gfxDevice.CreateTexture(new TextureDescriptor(
				BrdfSize,
				BrdfSize,
				TextureFormat.Rgba16Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess));
			_proceduralBrdfValid = false;
			_hasGeneratedBrdfLut = false;
		}

		ShouldRecordBrdfLutUpdate = _proceduralBrdfValid == false;
	}

	/// <summary>
	/// Creates <paramref name="sky"/>'s textures on first use and records this frame's sun and settings, setting
	/// <see cref="ProceduralSky.ShouldRecordLightingUpdate"/> when they differ from what it was last generated with.
	/// </summary>
	internal void PrepareSky(IGfxDevice gfxDevice, ProceduralSky sky, Vector3 sunDirection, float sunIntensityScale, Config config)
	{
		ArgumentNullException.ThrowIfNull(gfxDevice);
		ArgumentNullException.ThrowIfNull(sky);

		var createdResources = EnsureSkyTextures(gfxDevice, sky);
		sky.SunDirection = sunDirection == Vector3.Zero
			? Vector3.UnitY
			: Vector3.Normalize(sunDirection);
		sky.SunIntensityScale = Math.Clamp(sunIntensityScale, 0.0f, 1.0f);
		sky.Config = config;

		var sunChanged = Vector3.DistanceSquared(sky.GeneratedSunDirection, sky.SunDirection) > SunDirectionEpsilonSquared ||
		                 MathF.Abs(sky.GeneratedSunIntensityScale - sky.SunIntensityScale) > SunIntensityScaleEpsilon;
		var skyboxSettingsChanged = ConfigsDiffer(sky.GeneratedConfig, sky.Config);

		sky.ShouldRecordLightingUpdate = createdResources ||
			sky.HasGeneratedContent == false ||
			sunChanged ||
			skyboxSettingsChanged;
	}

	internal void RecordEnvironment(RenderGraphContext context, ProceduralSky sky)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(sky);

		var config = sky.Config;
		var environment = sky.Environment ?? throw new InvalidOperationException("Procedural environment texture was not created.");
		var pipeline = GetProceduralSkyboxPipeline(GetGfxDevice());
		var commandList = context.CommandList;
		commandList.BindPipeline(pipeline);

		var bindlessWriter = _proceduralBindlessWriter
			?? throw new InvalidOperationException("Procedural skybox bindless writer was not initialized.");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("environmentHandle", environment.UnorderedAccessView.Value);
		bindlessWriter.SetUInt("width", (uint)ProceduralEnvWidth);
		bindlessWriter.SetUInt("height", (uint)ProceduralEnvHeight);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());
		
		var skyParamsWriter = _proceduralSkyParamsWriter
			?? throw new InvalidOperationException("Procedural skybox parameter writer was not initialized.");
		skyParamsWriter.Clear();
		skyParamsWriter.SetVector4("sunDirectionIntensity", new Vector4(sky.SunDirection, config.Intensity * sky.SunIntensityScale));
		skyParamsWriter.SetVector4("sunColorSharpness", new Vector4(config.SunColor.X, config.SunColor.Y, config.SunColor.Z, config.SunSharpness));
		skyParamsWriter.SetColorRGBA("skyTop", config.TopColor);
		skyParamsWriter.SetColorRGBA("skyHorizon", config.HorizonColor);
		skyParamsWriter.SetColorRGBA("ground", config.GroundColor);
		commandList.SetComputeConstants(skyParamsWriter.RegisterIndex, skyParamsWriter.AsBytes());

		var threadGroupSize = _proceduralSkyboxThreadGroupSize
			?? throw new InvalidOperationException("Procedural skybox threadgroup size was not initialized.");
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(ProceduralEnvWidth, ProceduralEnvHeight);
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}

	internal void RecordIrradiance(RenderGraphContext context, ProceduralSky sky)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(sky);

		var environment = sky.Environment ?? throw new InvalidOperationException("Procedural environment texture was not created.");
		var irradiance = sky.Irradiance ?? throw new InvalidOperationException("Procedural irradiance texture was not created.");
		RecordIrradiance(context.CommandList, GetGfxDevice(), environment, irradiance, GetSkyboxSamplerHandle(GetGfxDevice()));
	}

	internal void RecordPrefilter(RenderGraphContext context, ProceduralSky sky)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(sky);

		var environment = sky.Environment ?? throw new InvalidOperationException("Procedural environment texture was not created.");
		var prefilter = sky.Prefilter ?? throw new InvalidOperationException("Procedural prefilter texture was not created.");
		RecordPrefilter(context.CommandList, GetGfxDevice(), environment, prefilter, GetSkyboxSamplerHandle(GetGfxDevice()));
		sky.HasGeneratedContent = true;
		sky.GeneratedSunDirection = sky.SunDirection;
		sky.GeneratedSunIntensityScale = sky.SunIntensityScale;
		sky.GeneratedConfig = sky.Config;
	}

	public void RecordBrdfLut(RenderGraphContext context)
	{
		ArgumentNullException.ThrowIfNull(context);

		var brdfLut = _proceduralBrdfLut ?? throw new InvalidOperationException("Procedural BRDF LUT texture was not created.");
		RecordBrdfLut(context.CommandList, GetGfxDevice(), brdfLut);
		_proceduralBrdfValid = true;
		_hasGeneratedBrdfLut = true;
	}

	private (IGfxTexture Irradiance, IGfxTexture Prefiltered, IGfxTexture BrdfLut) GenerateIblMaps(
		IGfxDevice gfxDevice,
		Texture environmentTexture,
		DescriptorHandle samplerHandle)
	{
		var envResources = environmentTexture.Resources
		                  ?? throw new InvalidOperationException("Environment texture resources were not created.");
		var envTexture = envResources.Texture;

		var irradianceDesc = new TextureDescriptor(
			IrradianceSize,
			IrradianceSize,
			TextureFormat.Rgba16Float,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess);
		var prefilterDesc = new TextureDescriptor(
			PrefilterWidth,
			PrefilterSliceHeight * PrefilterSlices,
			TextureFormat.Rgba16Float,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess);
		var brdfDesc = new TextureDescriptor(
			BrdfSize,
			BrdfSize,
			TextureFormat.Rgba16Float,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess);

		var irradianceTex = gfxDevice.CreateTexture(irradianceDesc);
		var prefilterTex = gfxDevice.CreateTexture(prefilterDesc);
		var brdfTex = gfxDevice.CreateTexture(brdfDesc);

		var irradianceCommandList = gfxDevice.BeginCompute();
		RecordIrradiance(irradianceCommandList, gfxDevice, envTexture, irradianceTex, samplerHandle);
		gfxDevice.Submit(irradianceCommandList);

		var prefilterCommandList = gfxDevice.BeginCompute();
		RecordPrefilter(prefilterCommandList, gfxDevice, envTexture, prefilterTex, samplerHandle);
		gfxDevice.Submit(prefilterCommandList);

		var brdfCommandList = gfxDevice.BeginCompute();
		RecordBrdfLut(brdfCommandList, gfxDevice, brdfTex);
		gfxDevice.Submit(brdfCommandList);

		return (irradianceTex, prefilterTex, brdfTex);
	}

	private void RecordIrradiance(
		IGfxCommandList commandList,
		IGfxDevice gfxDevice,
		IGfxTexture envTexture,
		IGfxTexture irradianceTex,
		DescriptorHandle samplerHandle)
	{
		var pipeline = GetIblIrradiancePipeline(gfxDevice);
		commandList.BindPipeline(pipeline);
		var writer = _iblIrradianceWriter
			?? throw new InvalidOperationException("IBL irradiance reflection writer was not initialized.");
		writer.Clear();
		writer.SetUInt("environmentHandle", envTexture.ShaderResourceView.Value);
		writer.SetUInt("irradianceHandle", irradianceTex.UnorderedAccessView.Value);
		writer.SetUInt("samplerHandle", samplerHandle.Value);
		writer.SetUInt("width", IrradianceSize);
		writer.SetUInt("height", IrradianceSize);
		writer.SetUInt("sliceCount", 1);
		writer.SetUInt("sliceHeight", IrradianceSize);
		commandList.SetComputeConstants(writer.RegisterIndex, writer.AsBytes());
		var threadGroupSize = _iblIrradianceThreadGroupSize
			?? throw new InvalidOperationException("IBL irradiance threadgroup size was not initialized.");
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(IrradianceSize, IrradianceSize);
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}

	private void RecordPrefilter(
		IGfxCommandList commandList,
		IGfxDevice gfxDevice,
		IGfxTexture envTexture,
		IGfxTexture prefilterTex,
		DescriptorHandle samplerHandle)
	{
		var pipeline = GetIblPrefilterPipeline(gfxDevice);
		commandList.BindPipeline(pipeline);
		var writer = _iblPrefilterWriter
			?? throw new InvalidOperationException("IBL prefilter reflection writer was not initialized.");
		writer.Clear();
		writer.SetUInt("environmentHandle", envTexture.ShaderResourceView.Value);
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

	private static bool EnsureSkyTextures(IGfxDevice gfxDevice, ProceduralSky sky)
	{
		if (sky.HasTextures)
		{
			return false;
		}

		// A partly created sky is released whole, so its three textures always come from one device.
		sky.Release();
		sky.SetDevice(gfxDevice);
		sky.Environment = gfxDevice.CreateTexture(new TextureDescriptor(
			ProceduralEnvWidth,
			ProceduralEnvHeight,
			TextureFormat.Rgba16Float,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess));
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

		return true;
	}

	private static bool ConfigsDiffer(in Config left, in Config right)
	{
		return left.Intensity != right.Intensity ||
		       left.SunColor != right.SunColor ||
		       left.SunSharpness != right.SunSharpness ||
		       ColorsDiffer(left.TopColor, right.TopColor) ||
		       ColorsDiffer(left.HorizonColor, right.HorizonColor) ||
		       ColorsDiffer(left.GroundColor, right.GroundColor);
	}

	private static bool ColorsDiffer(in ColorRGBA left, in ColorRGBA right)
	{
		return left.R != right.R || left.G != right.G || left.B != right.B || left.A != right.A;
	}

	private DescriptorHandle GetSkyboxSamplerHandle(IGfxDevice gfxDevice)
	{
		if (_skyboxSamplerHandle.IsValid == false)
		{
			var sampler = new SamplerDescriptor(FilterMode.Bilinear, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp);
			_bindlessRegistry.EnsureInitialized(gfxDevice);
			_skyboxSamplerHandle = _bindlessRegistry.GetSamplerHandle(sampler);
		}

		return _skyboxSamplerHandle;
	}

	private IGfxPipeline GetProceduralSkyboxPipeline(IGfxDevice gfxDevice)
	{
		if (_proceduralSkyboxPipeline is not null)
		{
			return _proceduralSkyboxPipeline;
		}

		var compiled = CompileComputeWithReflection(EngineShaderPrograms.ProceduralSkybox, "ProceduralSkyboxCSMain", gfxDevice.BackendKind);
		_proceduralSkyboxThreadGroupSize = compiled.ThreadGroupSize;
		_proceduralBindlessWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("BindlessHandles"));
		_proceduralSkyParamsWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("SkyParams"));
		var shaders = new ShaderBytecodeSet(compute: compiled.Bytecode, computeThreadGroupSize: _proceduralSkyboxThreadGroupSize);

		var pipelineKey = new PipelineKey(
			PassKind.Compute,
			vertexEntryPoint: null,
			pixelEntryPoint: null,
			computeEntryPoint: "ProceduralSkyboxCSMain",
			renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: default,
			layout: GraphicsLayoutKind.Default);
		_proceduralSkyboxPipeline = gfxDevice.GetOrCreatePipeline(pipelineKey, shaders);
		return _proceduralSkyboxPipeline;
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
