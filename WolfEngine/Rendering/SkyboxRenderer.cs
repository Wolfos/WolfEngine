using System.Runtime.InteropServices;
using System.Text;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

public sealed class SkyboxRenderer
{
	private readonly IRenderer _renderer;
	private readonly IShaderCompiler _shaderCompiler;
	private DescriptorHandle _skyboxSamplerHandle = DescriptorHandle.Invalid;
	private IGfxPipeline _iblIrradiancePipeline;
	private IGfxPipeline _iblPrefilterPipeline;
	private IGfxPipeline _iblBrdfLutPipeline;

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

		var samplerHandle = DescriptorHandle.Invalid;
		if (IsMetalRenderer())
		{
			if (_skyboxSamplerHandle.IsValid == false)
			{
				var sampler = new SamplerDescriptor(FilterMode.Bilinear, AddressMode.Clamp, AddressMode.Clamp,
					AddressMode.Clamp);
				_skyboxSamplerHandle = gfxDevice.GlobalTable.AllocateSampler(sampler);
			}

			samplerHandle = _skyboxSamplerHandle;
		}

		var (irradiance, prefiltered, brdfLut) = GenerateIblMaps(gfxDevice, environmentTexture, samplerHandle);

		return new SkyboxResources
		{
			EnvironmentTexture = environmentTexture.Resources.Texture,
			IrradianceTexture = irradiance,
			PrefilteredEnvironment = prefiltered,
			BrdfLut = brdfLut
		};
	}

	private (IGfxTexture Irradiance, IGfxTexture Prefiltered, IGfxTexture BrdfLut) GenerateIblMaps(
		IGfxDevice gfxDevice,
		Texture environmentTexture,
		DescriptorHandle samplerHandle)
	{
		var envResources = environmentTexture.Resources
		                  ?? throw new InvalidOperationException("Environment texture resources were not created.");
		var envTexture = envResources.Texture;

		const int irradianceSize = 64;
		const int prefilterWidth = 256;
		const int prefilterSliceHeight = 64;
		const int prefilterSlices = 6;
		const int brdfSize = 256;

		var irradianceDesc = new TextureDescriptor(
			irradianceSize,
			irradianceSize,
			TextureFormat.Rgba16Float,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess);
		var prefilterDesc = new TextureDescriptor(
			prefilterWidth,
			prefilterSliceHeight * prefilterSlices,
			TextureFormat.Rgba16Float,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess);
		var brdfDesc = new TextureDescriptor(
			brdfSize,
			brdfSize,
			TextureFormat.Rgba16Float,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess);

		var irradianceTex = gfxDevice.CreateTexture(irradianceDesc);
		var prefilterTex = gfxDevice.CreateTexture(prefilterDesc);
		var brdfTex = gfxDevice.CreateTexture(brdfDesc);

		// Irradiance
		{
			var pipeline = GetIblIrradiancePipeline(gfxDevice);
			var commandList = gfxDevice.BeginCompute();
			commandList.BindPipeline(pipeline);
			Span<uint> handles = stackalloc uint[7];
			handles[0] = envTexture.ShaderResourceView.Value;
			handles[1] = irradianceTex.UnorderedAccessView.Value;
			handles[2] = samplerHandle.Value;
			handles[3] = irradianceSize;
			handles[4] = irradianceSize;
			handles[5] = 1;
			handles[6] = irradianceSize;
			commandList.SetComputeConstants(0, MemoryMarshal.AsBytes(handles));

			var dispatchX = (uint)((irradianceSize + 7) / 8);
			var dispatchY = (uint)((irradianceSize + 7) / 8);
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
			handles[3] = prefilterWidth;
			handles[4] = prefilterSliceHeight * prefilterSlices;
			handles[5] = prefilterSlices;
			handles[6] = prefilterSliceHeight;
			commandList.SetComputeConstants(0, MemoryMarshal.AsBytes(handles));

			var dispatchX = (uint)((prefilterWidth + 7) / 8);
			var dispatchY = (uint)(((prefilterSliceHeight * prefilterSlices) + 7) / 8);
			commandList.Dispatch(dispatchX, dispatchY, 1);
			gfxDevice.Submit(commandList);
		}

		// BRDF LUT
		{
			var pipeline = GetIblBrdfPipeline(gfxDevice);
			var commandList = gfxDevice.BeginCompute();
			commandList.BindPipeline(pipeline);
			Span<uint> handles = stackalloc uint[1];
			handles[0] = brdfTex.UnorderedAccessView.Value;
			commandList.SetComputeConstants(0, MemoryMarshal.AsBytes(handles));

			Span<float> constants = stackalloc float[20];
			constants[0] = brdfSize;
			constants[1] = brdfSize;
			commandList.SetComputeConstants(1, MemoryMarshal.AsBytes(constants));

			var dispatchX = (uint)((brdfSize + 7) / 8);
			var dispatchY = (uint)((brdfSize + 7) / 8);
			commandList.Dispatch(dispatchX, dispatchY, 1);
			gfxDevice.Submit(commandList);
		}

		return (irradianceTex, prefilterTex, brdfTex);
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
