using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
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
	private DescriptorHandle _skyboxSamplerHandle = DescriptorHandle.Invalid;
	private IGfxPipeline _iblIrradiancePipeline;
	private IGfxPipeline _iblPrefilterPipeline;
	private IGfxPipeline _iblBrdfLutPipeline;
	private IGfxPipeline _proceduralSkyboxPipeline;
	private SkyboxResources? _proceduralSkybox;
	private IGfxTexture? _proceduralEnvironment;
	private IGfxTexture? _proceduralIrradiance;
	private IGfxTexture? _proceduralPrefilter;
	private IGfxTexture? _proceduralBrdfLut;
	private bool _brdfLutInitialized;

	public SkyboxRenderer(IRenderer renderer, IShaderCompiler shaderCompiler)
	{
		_renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
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

		var samplerHandle = IsMetalRenderer()
			? GetSkyboxSamplerHandle(gfxDevice)
			: DescriptorHandle.Invalid;

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
		if (IsMetalRenderer() == false)
		{
			throw new NotSupportedException("Procedural skybox requires bindless descriptor support.");
		}

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
			Span<uint> handles = stackalloc uint[7];
			handles[0] = envTexture.ShaderResourceView.Value;
			handles[1] = irradianceTex.UnorderedAccessView.Value;
			handles[2] = samplerHandle.Value;
			handles[3] = IrradianceSize;
			handles[4] = IrradianceSize;
			handles[5] = 1;
			handles[6] = IrradianceSize;
			commandList.SetComputeConstants(0, MemoryMarshal.AsBytes(handles));

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
			Span<uint> handles = stackalloc uint[7];
			handles[0] = envTexture.ShaderResourceView.Value;
			handles[1] = prefilterTex.UnorderedAccessView.Value;
			handles[2] = samplerHandle.Value;
			handles[3] = PrefilterWidth;
			handles[4] = PrefilterSliceHeight * PrefilterSlices;
			handles[5] = PrefilterSlices;
			handles[6] = PrefilterSliceHeight;
			commandList.SetComputeConstants(0, MemoryMarshal.AsBytes(handles));

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
			Span<uint> handles = stackalloc uint[1];
			handles[0] = brdfTex.UnorderedAccessView.Value;
			commandList.SetComputeConstants(0, MemoryMarshal.AsBytes(handles));

			Span<float> constants = stackalloc float[20];
			constants[0] = BrdfSize;
			constants[1] = BrdfSize;
			commandList.SetComputeConstants(1, MemoryMarshal.AsBytes(constants));

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

		Span<uint> handles = stackalloc uint[4];
		handles[0] = environment.UnorderedAccessView.Value;
		handles[1] = (uint)ProceduralEnvWidth;
		handles[2] = (uint)ProceduralEnvHeight;
		handles[3] = 0;
		commandList.SetComputeConstants(0, MemoryMarshal.AsBytes(handles));

		Span<float> parameters = stackalloc float[20];
		parameters[0] = sunDirection.X;
		parameters[1] = sunDirection.Y;
		parameters[2] = sunDirection.Z;
		parameters[3] = 25.0f;
		parameters[4] = 1.0f;
		parameters[5] = 0.95f;
		parameters[6] = 0.8f;
		parameters[7] = 256.0f;
		parameters[8] = 0.2f;
		parameters[9] = 0.45f;
		parameters[10] = 0.85f;
		parameters[11] = 1.4f;
		parameters[12] = 0.65f;
		parameters[13] = 0.75f;
		parameters[14] = 0.9f;
		parameters[15] = 1.0f;
		parameters[16] = 0.05f;
		parameters[17] = 0.06f;
		parameters[18] = 0.07f;
		parameters[19] = 0.0f;
		commandList.SetComputeConstants(1, MemoryMarshal.AsBytes(parameters));

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
			_skyboxSamplerHandle = gfxDevice.GlobalTable.AllocateSampler(sampler);
		}

		return _skyboxSamplerHandle;
	}

	private IGfxPipeline GetProceduralSkyboxPipeline(IGfxDevice gfxDevice)
	{
		if (_proceduralSkyboxPipeline is not null)
		{
			return _proceduralSkyboxPipeline;
		}

		ShaderBytecodeSet shaders;
		if (IsMetalRenderer())
		{
			var source = _shaderCompiler.GetMetalComputeSource("procedural_skybox.compute.slang", "ProceduralSkyboxCSMain");
			var shaderBytes = Encoding.UTF8.GetBytes(source);
			shaders = new ShaderBytecodeSet(compute: shaderBytes);
		}
		else
		{
			var shader = _shaderCompiler.GetComputeShader("procedural_skybox.compute.slang", "ProceduralSkyboxCSMain");
			shaders = new ShaderBytecodeSet(compute: shader);
		}

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

		ShaderBytecodeSet shaders;
		if (IsMetalRenderer())
		{
			var source = _shaderCompiler.GetMetalComputeSource("ibl_irradiance.compute.slang", "IblIrradianceCSMain");
			var shaderBytes = Encoding.UTF8.GetBytes(source);
			shaders = new ShaderBytecodeSet(compute: shaderBytes);
		}
		else
		{
			var shader = _shaderCompiler.GetComputeShader("ibl_irradiance.compute.slang", "IblIrradianceCSMain");
			shaders = new ShaderBytecodeSet(compute: shader);
		}

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

		ShaderBytecodeSet shaders;
		if (IsMetalRenderer())
		{
			var source = _shaderCompiler.GetMetalComputeSource("ibl_prefilter.compute.slang", "IblPrefilterCSMain");
			var shaderBytes = Encoding.UTF8.GetBytes(source);
			shaders = new ShaderBytecodeSet(compute: shaderBytes);
		}
		else
		{
			var shader = _shaderCompiler.GetComputeShader("ibl_prefilter.compute.slang", "IblPrefilterCSMain");
			shaders = new ShaderBytecodeSet(compute: shader);
		}

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

		ShaderBytecodeSet shaders;
		if (IsMetalRenderer())
		{
			var source = _shaderCompiler.GetMetalComputeSource("ibl_brdf_lut.compute.slang", "IblBrdfCSMain");
			var shaderBytes = Encoding.UTF8.GetBytes(source);
			shaders = new ShaderBytecodeSet(compute: shaderBytes);
		}
		else
		{
			var shader = _shaderCompiler.GetComputeShader("ibl_brdf_lut.compute.slang", "IblBrdfCSMain");
			shaders = new ShaderBytecodeSet(compute: shader);
		}

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

	private bool IsMetalRenderer()
	{
		return _renderer is WolfRendererMetal;
	}
}
