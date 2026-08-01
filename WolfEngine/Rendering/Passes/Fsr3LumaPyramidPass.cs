using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// FSR3 upscaler luma pyramid pass: single-pass downsample of the luma and farthest-depth
/// targets, plus the frame's auto exposure.
/// </summary>
/// <remarks>
/// Reduces prepare_inputs' outputs into a six-level mip chain in one dispatch. At the 1x1
/// level a single lane derives the exposure that RCAS and accumulate consume, so this pass is
/// what takes the effect off a placeholder exposure.
///
/// Two ordering requirements the render graph has to honour:
/// <list type="bullet">
/// <item>The global atomic counter must be zero before the dispatch. The shader resets it on
/// the way out, so steady state is self-sustaining, but the first frame after allocation - and
/// any frame after a dropped or partial dispatch - needs an explicit clear.</item>
/// <item>The mip chain must be cleared too. Upstream calls the mips aliasable but explicitly
/// excludes them from aliasing for this reason: the last thread group reads mip 5 texels that
/// may never have been written when the render size does not fill the final tile.</item>
/// </list>
///
/// Not yet wired into the render graph.
/// </remarks>
public sealed class Fsr3LumaPyramidPass
{
	/// <summary>Pyramid levels the shader declares. Fixed by the callbacks, not by render size.</summary>
	public const int SpdMipCount = 6;

	private readonly IShaderProvider _shaderCompiler;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private IGfxPipeline? _pipeline;
	private ReadOnlyMemory<byte> _computeShader;
	private ComputeThreadGroupSize? _threadGroupSize;
	private GraphicsBackendKind? _compiledBackendKind;
	private ShaderPropertyWriter? _bindlessWriter;
	private ShaderPropertyWriter? _constantsWriter;
	private ShaderPropertyWriter? _spdWriter;
	private DescriptorHandle _pointSampler = DescriptorHandle.Invalid;
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;

	public Fsr3LumaPyramidPass(IShaderProvider shaderCompiler, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public Fsr3LumaPyramidPassConfig BuildConfig(
		RenderGraphContext context,
		IGfxDevice device,
		RenderGraphResourceHandle currentLuma,
		RenderGraphResourceHandle farthestDepth,
		RenderGraphResourceHandle farthestDepthMip1,
		RenderGraphResourceHandle frameInfo,
		RenderGraphResourceHandle spdGlobalAtomic,
		ReadOnlySpan<RenderGraphResourceHandle> spdMips,
		in Fsr3ConstantValues constants)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);
		if (spdMips.Length != SpdMipCount)
		{
			throw new ArgumentException(
				$"The luma pyramid needs exactly {SpdMipCount} mip targets, got {spdMips.Length}.",
				nameof(spdMips));
		}

		var pipeline = EnsurePipeline(device);
		_bindlessRegistry.EnsureInitialized(device);
		EnsureSamplers();

		var mipHandles = new DescriptorHandle[SpdMipCount];
		for (var mip = 0; mip < SpdMipCount; mip++)
		{
			mipHandles[mip] = _bindlessRegistry.RegisterRwTexture(context.GetTexture(spdMips[mip]));
		}

		return new Fsr3LumaPyramidPassConfig
		{
			Pipeline = pipeline,
			CurrentLumaHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(currentLuma)),
			FarthestDepthHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(farthestDepth)),
			FarthestDepthMip1Handle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(farthestDepthMip1)),
			FrameInfoHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(frameInfo)),
			SpdGlobalAtomicHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(spdGlobalAtomic)),
			SpdMipHandles = mipHandles,
			PointSampler = _pointSampler,
			LinearSampler = _linearSampler,
			Constants = constants,
			SpdSetup = Fsr3SpdSetup.Create(constants.RenderSize)
		};
	}

	public void Record(RenderGraphContext context, in Fsr3LumaPyramidPassConfig config)
	{
		ArgumentNullException.ThrowIfNull(context);

		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);

		var bindlessWriter = _bindlessWriter
			?? throw new InvalidOperationException("FSR3 luma pyramid bindless writer was not initialized.");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("currentLumaReadHandle", config.CurrentLumaHandle.Value);
		bindlessWriter.SetUInt("farthestDepthReadHandle", config.FarthestDepthHandle.Value);
		bindlessWriter.SetUInt("farthestDepthMip1Handle", config.FarthestDepthMip1Handle.Value);
		bindlessWriter.SetUInt("frameInfoHandle", config.FrameInfoHandle.Value);
		bindlessWriter.SetUInt("spdGlobalAtomicHandle", config.SpdGlobalAtomicHandle.Value);
		bindlessWriter.SetUInt("spdMip0Handle", config.SpdMipHandles[0].Value);
		bindlessWriter.SetUInt("spdMip1Handle", config.SpdMipHandles[1].Value);
		bindlessWriter.SetUInt("spdMip2Handle", config.SpdMipHandles[2].Value);
		bindlessWriter.SetUInt("spdMip3Handle", config.SpdMipHandles[3].Value);
		bindlessWriter.SetUInt("spdMip4Handle", config.SpdMipHandles[4].Value);
		bindlessWriter.SetUInt("spdMip5Handle", config.SpdMipHandles[5].Value);
		bindlessWriter.SetUInt("pointSamplerHandle", config.PointSampler.Value);
		bindlessWriter.SetUInt("linearSamplerHandle", config.LinearSampler.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		var constantsWriter = _constantsWriter
			?? throw new InvalidOperationException("FSR3 constants writer was not initialized.");
		Fsr3Constants.Write(constantsWriter, config.Constants);
		commandList.SetComputeConstants(constantsWriter.RegisterIndex, constantsWriter.AsBytes());

		var spdWriter = _spdWriter
			?? throw new InvalidOperationException("FSR3 SPD constants writer was not initialized.");
		var setup = config.SpdSetup;
		spdWriter.Clear();
		spdWriter.SetUInt("spdMips", setup.MipCount);
		spdWriter.SetUInt("spdNumWorkGroups", setup.NumWorkGroups);
		spdWriter.SetUInt("spdWorkGroupOffsetX", (uint)Math.Max(setup.WorkGroupOffset.X, 0));
		spdWriter.SetUInt("spdWorkGroupOffsetY", (uint)Math.Max(setup.WorkGroupOffset.Y, 0));
		spdWriter.SetUInt("spdRenderSizeX", (uint)Math.Max(config.Constants.RenderSize.X, 1));
		spdWriter.SetUInt("spdRenderSizeY", (uint)Math.Max(config.Constants.RenderSize.Y, 1));
		commandList.SetComputeConstants(spdWriter.RegisterIndex, spdWriter.AsBytes());

		commandList.Dispatch(
			(uint)Math.Max(setup.DispatchThreadGroupCount.X, 1),
			(uint)Math.Max(setup.DispatchThreadGroupCount.Y, 1),
			1);
	}

	private void EnsureSamplers()
	{
		if (_pointSampler.IsValid == false)
		{
			_pointSampler = _bindlessRegistry.GetSamplerHandle(new SamplerDescriptor(
				FilterMode.Point,
				AddressMode.Clamp,
				AddressMode.Clamp,
				AddressMode.Clamp));
		}

		if (_linearSampler.IsValid == false)
		{
			_linearSampler = _bindlessRegistry.GetSamplerHandle(new SamplerDescriptor(
				FilterMode.Bilinear,
				AddressMode.Clamp,
				AddressMode.Clamp,
				AddressMode.Clamp));
		}
	}

	private IGfxPipeline EnsurePipeline(IGfxDevice device)
	{
		if (_pipeline is not null)
		{
			if (_compiledBackendKind.HasValue && _compiledBackendKind.Value != device.BackendKind)
			{
				throw new InvalidOperationException(
					$"Fsr3LumaPyramidPass is already compiled for backend '{_compiledBackendKind.Value}', but was requested for '{device.BackendKind}'.");
			}

			return _pipeline;
		}

		EnsureReflectionWriters(device.BackendKind);
		var pipelineKey = new PipelineKey(
			PassKind.Compute,
			vertexEntryPoint: null,
			pixelEntryPoint: null,
			computeEntryPoint: "Fsr3LumaPyramidCS",
			renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: default,
			shaderVariant: "fsr3_luma_pyramid.compute.slang");
		_pipeline = device.GetOrCreatePipeline(
			pipelineKey,
			new ShaderBytecodeSet(compute: _computeShader, computeThreadGroupSize: _threadGroupSize));
		return _pipeline;
	}

	private void EnsureReflectionWriters(GraphicsBackendKind backendKind)
	{
		if (_compiledBackendKind.HasValue &&
		    _compiledBackendKind.Value == backendKind &&
		    _bindlessWriter is not null &&
		    _constantsWriter is not null &&
		    _spdWriter is not null &&
		    _computeShader.IsEmpty == false &&
		    _threadGroupSize.HasValue)
		{
			return;
		}

		var compiled = _shaderCompiler.GetComputeShaderWithReflection(
			EngineShaderPrograms.Fsr3LumaPyramid,
			"Fsr3LumaPyramidCS",
			backendKind);
		_computeShader = compiled.Bytecode;
		_threadGroupSize = compiled.ThreadGroupSize;
		var reflection = compiled.ReflectionLayout;
		_bindlessWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("Fsr3BindlessHandles"));
		_constantsWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("cbFSR3Upscaler"));
		_spdWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("cbSPD"));
		_compiledBackendKind = backendKind;
	}
}
