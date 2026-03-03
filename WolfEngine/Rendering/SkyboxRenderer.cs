using System.Numerics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

public sealed class SkyboxRenderer
{
	private const int ProceduralEnvWidth = 512;
	private const int ProceduralEnvHeight = 256;
	private const int IrradianceSize = 64;
	private const int PrefilterWidth = 256;
	private const int PrefilterSliceHeight = 64;
	private const int PrefilterSlices = 6;
	private const int BrdfSize = 256;

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
	private bool _brdfLutInitialized;

	public SkyboxRenderer(IRenderer renderer, IShaderCompiler shaderCompiler, BindlessResourceRegistry bindlessRegistry)
	{
		_renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public SkyboxResources CreateSkyboxResources(Texture environmentTexture)
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

	public SkyboxResources UpdateProceduralSkybox(Vector3 sunDirection)
	{
		var gfxDevice = GetGfxDevice();
		var samplerHandle = GetSkyboxSamplerHandle(gfxDevice);
		EnsureProceduralResources(gfxDevice);

		if (_proceduralEnvironment is null ||
		    _proceduralIrradiance is null ||
		    _proceduralPrefilter is null ||
		    _proceduralBrdfLut is null)
		{
			throw new InvalidOperationException("Procedural skybox resources were not created.");
		}

		var normalizedSun = sunDirection == Vector3.Zero
			? Vector3.UnitY
			: Vector3.Normalize(sunDirection);

		GenerateProceduralEnvironment(gfxDevice, _proceduralEnvironment, normalizedSun);
		UpdateIblMaps(
			gfxDevice,
			_proceduralEnvironment,
			_proceduralIrradiance,
			_proceduralPrefilter,
			_proceduralBrdfLut,
			samplerHandle,
			updateBrdf: _brdfLutInitialized == false);
		_brdfLutInitialized = true;

		_proceduralSkybox ??= new SkyboxResources
		{
			EnvironmentTexture = _proceduralEnvironment,
			IrradianceTexture = _proceduralIrradiance,
			PrefilteredEnvironment = _proceduralPrefilter,
			BrdfLut = _proceduralBrdfLut
		};

		return _proceduralSkybox;
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

		UpdateIblMaps(gfxDevice, envTexture, irradianceTex, prefilterTex, brdfTex, samplerHandle, updateBrdf: true);

		return (irradianceTex, prefilterTex, brdfTex);
	}

	private void UpdateIblMaps(
		IGfxDevice gfxDevice,
		IGfxTexture envTexture,
		IGfxTexture irradianceTex,
		IGfxTexture prefilterTex,
		IGfxTexture brdfTex,
		DescriptorHandle samplerHandle,
		bool updateBrdf)
	{
		// Irradiance
		{
			var pipeline = GetIblIrradiancePipeline(gfxDevice);
			var commandList = gfxDevice.BeginCompute();
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

			var dispatchX = (uint)((IrradianceSize + 7) / 8);
			var dispatchY = (uint)((IrradianceSize + 7) / 8);
			commandList.Dispatch(dispatchX, dispatchY, 1);
			gfxDevice.Submit(commandList);
		}

		// Prefilter (roughness slices stacked vertically)
		{
			var pipeline = GetIblPrefilterPipeline(gfxDevice);
			var commandList = gfxDevice.BeginCompute();
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

			var dispatchX = (uint)((PrefilterWidth + 7) / 8);
			var dispatchY = (uint)(((PrefilterSliceHeight * PrefilterSlices) + 7) / 8);
			commandList.Dispatch(dispatchX, dispatchY, 1);
			gfxDevice.Submit(commandList);
		}

		if (updateBrdf)
		{
			var pipeline = GetIblBrdfPipeline(gfxDevice);
			var commandList = gfxDevice.BeginCompute();
			commandList.BindPipeline(pipeline);
			var writer = _iblBrdfWriter
				?? throw new InvalidOperationException("IBL BRDF reflection writer was not initialized.");
			writer.Clear();
			writer.SetUInt("brdfHandle", brdfTex.UnorderedAccessView.Value);
			commandList.SetComputeConstants(writer.RegisterIndex, writer.AsBytes());

			var dispatchX = (uint)((BrdfSize + 7) / 8);
			var dispatchY = (uint)((BrdfSize + 7) / 8);
			commandList.Dispatch(dispatchX, dispatchY, 1);
			gfxDevice.Submit(commandList);
		}
	}

	private void EnsureProceduralResources(IGfxDevice gfxDevice)
	{
		if (_proceduralEnvironment is null)
		{
			var desc = new TextureDescriptor(
				ProceduralEnvWidth,
				ProceduralEnvHeight,
				TextureFormat.Rgba16Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess);
			_proceduralEnvironment = gfxDevice.CreateTexture(desc);
		}

		if (_proceduralIrradiance is null)
		{
			var desc = new TextureDescriptor(
				IrradianceSize,
				IrradianceSize,
				TextureFormat.Rgba16Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess);
			_proceduralIrradiance = gfxDevice.CreateTexture(desc);
		}

		if (_proceduralPrefilter is null)
		{
			var desc = new TextureDescriptor(
				PrefilterWidth,
				PrefilterSliceHeight * PrefilterSlices,
				TextureFormat.Rgba16Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess);
			_proceduralPrefilter = gfxDevice.CreateTexture(desc);
		}

		if (_proceduralBrdfLut is null)
		{
			var desc = new TextureDescriptor(
				BrdfSize,
				BrdfSize,
				TextureFormat.Rgba16Float,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess);
			_proceduralBrdfLut = gfxDevice.CreateTexture(desc);
			_brdfLutInitialized = false;
		}
	}

	private void GenerateProceduralEnvironment(IGfxDevice gfxDevice, IGfxTexture environment, Vector3 sunDirection)
	{
		var pipeline = GetProceduralSkyboxPipeline(gfxDevice);
		var commandList = gfxDevice.BeginCompute();
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
		skyParamsWriter.SetVector4("sunDirectionIntensity", new Vector4(sunDirection, 25.0f));
		skyParamsWriter.SetVector4("sunColorSharpness", new Vector4(1.0f, 0.95f, 0.8f, 256.0f));
		skyParamsWriter.SetVector4("skyTop", new Vector4(0.2f, 0.45f, 0.85f, 1.4f));
		skyParamsWriter.SetVector4("skyHorizon", new Vector4(0.65f, 0.75f, 0.9f, 1.0f));
		skyParamsWriter.SetVector4("ground", new Vector4(0.15f, 0.1f, 0.07f, 0.0f));
		commandList.SetComputeConstants(skyParamsWriter.RegisterIndex, skyParamsWriter.AsBytes());

		var dispatchX = (uint)((ProceduralEnvWidth + 7) / 8);
		var dispatchY = (uint)((ProceduralEnvHeight + 7) / 8);
		commandList.Dispatch(dispatchX, dispatchY, 1);
		gfxDevice.Submit(commandList);
	}

	private DescriptorHandle GetSkyboxSamplerHandle(IGfxDevice gfxDevice)
	{
		if (_skyboxSamplerHandle.IsValid == false)
		{
			var sampler = new SamplerDescriptor(FilterMode.Bilinear, AddressMode.Clamp, AddressMode.Clamp,
				AddressMode.Clamp);
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

		var compiled = CompileComputeWithReflection(
			"procedural_skybox.compute.slang",
			"ProceduralSkyboxCSMain",
			gfxDevice.BackendKind);
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

		var compiled = CompileComputeWithReflection(
			"ibl_irradiance.compute.slang",
			"IblIrradianceCSMain",
			gfxDevice.BackendKind);
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

		var compiled = CompileComputeWithReflection(
			"ibl_prefilter.compute.slang",
			"IblPrefilterCSMain",
			gfxDevice.BackendKind);
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

		var compiled = CompileComputeWithReflection(
			"ibl_brdf_lut.compute.slang",
			"IblBrdfCSMain",
			gfxDevice.BackendKind);
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
				$"SkyboxRenderer was already compiled for backend '{_reflectionBackendKind.Value}', " +
				$"but was requested for '{backendKind}'.");
		}

		_reflectionBackendKind = backendKind;
		return _shaderCompiler.GetComputeShaderWithReflection(shaderFile, entryPoint, backendKind);
	}
}
