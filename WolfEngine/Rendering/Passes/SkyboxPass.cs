using System.Numerics;
using WolfEngine.Rendering.Abstraction;

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

	private readonly IRenderer _renderer;
	private readonly IShaderCompiler _shaderCompiler;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private DescriptorHandle _skyboxSamplerHandle = DescriptorHandle.Invalid;
	private IGfxPipeline _iblIrradiancePipeline;
	private IGfxPipeline _iblPrefilterPipeline;
	private IGfxPipeline _iblBrdfLutPipeline;
	private IGfxPipeline _proceduralSkyboxPipeline;
	private GraphicsBackendKind? _reflectionBackendKind;
	private ShaderPropertyWriter? _iblIrradianceWriter;
	private ShaderPropertyWriter? _iblPrefilterWriter;
	private ShaderPropertyWriter? _iblBrdfWriter;
	private ShaderPropertyWriter? _proceduralBindlessWriter;
	private ShaderPropertyWriter? _proceduralSkyParamsWriter;
	private SkyboxResources? _proceduralSkybox;
	private IGfxTexture? _proceduralEnvironment;
	private IGfxTexture? _proceduralIrradiance;
	private IGfxTexture? _proceduralPrefilter;
	private IGfxTexture? _proceduralBrdfLut;
	private Vector3 _currentSunDirection = Vector3.UnitY;
	private Vector3 _lastGeneratedSunDirection = Vector3.UnitY;
	private bool _hasGeneratedSunDirection;
	private bool _proceduralLightingValid;
	private bool _proceduralBrdfValid;
	private bool _hasGeneratedProceduralContent;

	public SkyboxPass(IRenderer renderer, IShaderCompiler shaderCompiler, BindlessResourceRegistry bindlessRegistry)
	{
		_renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public bool ShouldRecordProceduralLightingUpdate { get; private set; }

	public bool ShouldRecordBrdfLutUpdate { get; private set; }

	public ResourceState ProceduralResourcesInitialState =>
		_hasGeneratedProceduralContent ? ResourceState.ShaderResource : ResourceState.UnorderedAccess;

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

	public void PrepareFrame(IGfxDevice gfxDevice, Vector3 sunDirection)
	{
		ArgumentNullException.ThrowIfNull(gfxDevice);

		EnsureProceduralResources(gfxDevice, out var createdResources);

		_currentSunDirection = sunDirection == Vector3.Zero
			? Vector3.UnitY
			: Vector3.Normalize(sunDirection);

		var sunChanged = _hasGeneratedSunDirection == false ||
		                 Vector3.DistanceSquared(_lastGeneratedSunDirection, _currentSunDirection) > SunDirectionEpsilonSquared;

		ShouldRecordProceduralLightingUpdate = createdResources || _proceduralLightingValid == false || sunChanged;
		ShouldRecordBrdfLutUpdate = createdResources || _proceduralBrdfValid == false;

		_proceduralSkybox = new SkyboxResources
		{
			EnvironmentTexture = _proceduralEnvironment
			                     ?? throw new InvalidOperationException("Procedural environment texture was not created."),
			IrradianceTexture = _proceduralIrradiance
			                    ?? throw new InvalidOperationException("Procedural irradiance texture was not created."),
			PrefilteredEnvironment = _proceduralPrefilter
			                         ?? throw new InvalidOperationException("Procedural prefilter texture was not created."),
			BrdfLut = _proceduralBrdfLut
			          ?? throw new InvalidOperationException("Procedural BRDF LUT texture was not created.")
		};
	}

	public SkyboxResources GetProceduralResources()
	{
		return _proceduralSkybox ?? throw new InvalidOperationException("Procedural skybox resources are not prepared.");
	}

	public void RecordEnvironment(RenderGraphContext context, Config config)
	{
		ArgumentNullException.ThrowIfNull(context);

		var environment = _proceduralEnvironment ?? throw new InvalidOperationException("Procedural environment texture was not created.");
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
		skyParamsWriter.SetVector4("sunDirectionIntensity", new Vector4(_currentSunDirection, config.Intensity));
		skyParamsWriter.SetVector4("sunColorSharpness", new Vector4(config.SunColor.X, config.SunColor.Y, config.SunColor.Z, config.SunSharpness));
		skyParamsWriter.SetColorRGBA("skyTop", config.TopColor);
		skyParamsWriter.SetColorRGBA("skyHorizon", config.HorizonColor);
		skyParamsWriter.SetColorRGBA("ground", config.GroundColor);
		commandList.SetComputeConstants(skyParamsWriter.RegisterIndex, skyParamsWriter.AsBytes());

		commandList.Dispatch((uint)((ProceduralEnvWidth + 7) / 8), (uint)((ProceduralEnvHeight + 7) / 8), 1);
	}

	public void RecordIrradiance(RenderGraphContext context)
	{
		ArgumentNullException.ThrowIfNull(context);

		var environment = _proceduralEnvironment ?? throw new InvalidOperationException("Procedural environment texture was not created.");
		var irradiance = _proceduralIrradiance ?? throw new InvalidOperationException("Procedural irradiance texture was not created.");
		RecordIrradiance(context.CommandList, GetGfxDevice(), environment, irradiance, GetSkyboxSamplerHandle(GetGfxDevice()));
	}

	public void RecordPrefilter(RenderGraphContext context)
	{
		ArgumentNullException.ThrowIfNull(context);

		var environment = _proceduralEnvironment ?? throw new InvalidOperationException("Procedural environment texture was not created.");
		var prefilter = _proceduralPrefilter ?? throw new InvalidOperationException("Procedural prefilter texture was not created.");
		RecordPrefilter(context.CommandList, GetGfxDevice(), environment, prefilter, GetSkyboxSamplerHandle(GetGfxDevice()));
		_proceduralLightingValid = true;
		_hasGeneratedProceduralContent = true;
		_lastGeneratedSunDirection = _currentSunDirection;
		_hasGeneratedSunDirection = true;
	}

	public void RecordBrdfLut(RenderGraphContext context)
	{
		ArgumentNullException.ThrowIfNull(context);

		var brdfLut = _proceduralBrdfLut ?? throw new InvalidOperationException("Procedural BRDF LUT texture was not created.");
		RecordBrdfLut(context.CommandList, GetGfxDevice(), brdfLut);
		_proceduralBrdfValid = true;
		_hasGeneratedProceduralContent = true;
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
		commandList.Dispatch((uint)((IrradianceSize + 7) / 8), (uint)((IrradianceSize + 7) / 8), 1);
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
		commandList.Dispatch(
			(uint)((PrefilterWidth + 7) / 8),
			(uint)(((PrefilterSliceHeight * PrefilterSlices) + 7) / 8),
			1);
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
		commandList.Dispatch((uint)((BrdfSize + 7) / 8), (uint)((BrdfSize + 7) / 8), 1);
	}

	private void EnsureProceduralResources(IGfxDevice gfxDevice, out bool createdResources)
	{
		createdResources = false;

		if (_proceduralEnvironment is null)
		{
			_proceduralEnvironment = gfxDevice.CreateTexture(new TextureDescriptor(
				ProceduralEnvWidth,
				ProceduralEnvHeight,
				TextureFormat.Rgba16Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess));
			createdResources = true;
		}

		if (_proceduralIrradiance is null)
		{
			_proceduralIrradiance = gfxDevice.CreateTexture(new TextureDescriptor(
				IrradianceSize,
				IrradianceSize,
				TextureFormat.Rgba16Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess));
			createdResources = true;
		}

		if (_proceduralPrefilter is null)
		{
			_proceduralPrefilter = gfxDevice.CreateTexture(new TextureDescriptor(
				PrefilterWidth,
				PrefilterSliceHeight * PrefilterSlices,
				TextureFormat.Rgba16Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess));
			createdResources = true;
		}

		if (_proceduralBrdfLut is null)
		{
			_proceduralBrdfLut = gfxDevice.CreateTexture(new TextureDescriptor(
				BrdfSize,
				BrdfSize,
				TextureFormat.Rgba16Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess));
			createdResources = true;
		}

		if (createdResources)
		{
			_proceduralLightingValid = false;
			_proceduralBrdfValid = false;
			_hasGeneratedProceduralContent = false;
			_hasGeneratedSunDirection = false;
		}
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

		var compiled = CompileComputeWithReflection("procedural_skybox.compute.slang", "ProceduralSkyboxCSMain", gfxDevice.BackendKind);
		_proceduralBindlessWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("BindlessHandles"));
		_proceduralSkyParamsWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("SkyParams"));
		var shaders = new ShaderBytecodeSet(compute: compiled.Bytecode);

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

		var compiled = CompileComputeWithReflection("ibl_irradiance.compute.slang", "IblIrradianceCSMain", gfxDevice.BackendKind);
		_iblIrradianceWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("BindlessHandles"));
		var shaders = new ShaderBytecodeSet(compute: compiled.Bytecode);

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

		var compiled = CompileComputeWithReflection("ibl_prefilter.compute.slang", "IblPrefilterCSMain", gfxDevice.BackendKind);
		_iblPrefilterWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("BindlessHandles"));
		var shaders = new ShaderBytecodeSet(compute: compiled.Bytecode);

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

		var compiled = CompileComputeWithReflection("ibl_brdf_lut.compute.slang", "IblBrdfCSMain", gfxDevice.BackendKind);
		_iblBrdfWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("BindlessHandles"));
		var shaders = new ShaderBytecodeSet(compute: compiled.Bytecode);

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
		string shaderFile,
		string entryPoint,
		GraphicsBackendKind backendKind)
	{
		if (_reflectionBackendKind.HasValue && _reflectionBackendKind.Value != backendKind)
		{
			throw new InvalidOperationException(
				$"SkyboxPass was already compiled for backend '{_reflectionBackendKind.Value}', but was requested for '{backendKind}'.");
		}

		_reflectionBackendKind = backendKind;
		return _shaderCompiler.GetComputeShaderWithReflection(shaderFile, entryPoint, backendKind);
	}
}
