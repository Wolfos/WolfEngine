using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// FSR3 upscaler shading change pyramid pass.
/// </summary>
/// <remarks>
/// Measures how much the shading changed between frames, independently of geometry motion,
/// and reduces that into a pyramid so <see cref="Fsr3ShadingChangePass"/> can look at it
/// across scales.
///
/// The same two ordering requirements as <see cref="Fsr3LumaPyramidPass"/> apply: the SPD
/// counter must be zero before dispatch, and the mip chain must be cleared.
///
/// Level 0 of this pyramid is at half render resolution, not render resolution, because SPD's
/// first reduction is already a 2x downsample of the render-resolution difference.
///
/// Not yet wired into the render graph.
/// </remarks>
public sealed class Fsr3ShadingChangePyramidPass
{
	/// <summary>Pyramid levels the shader declares.</summary>
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

	public Fsr3ShadingChangePyramidPass(IShaderProvider shaderCompiler, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public Fsr3ShadingChangePyramidPassConfig BuildConfig(
		RenderGraphContext context,
		IGfxDevice device,
		RenderGraphResourceHandle currentLuma,
		RenderGraphResourceHandle previousLuma,
		RenderGraphResourceHandle dilatedMotionVectors,
		RenderGraphResourceHandle exposure,
		RenderGraphResourceHandle spdGlobalAtomic,
		ReadOnlySpan<RenderGraphResourceHandle> spdMips,
		in Fsr3ConstantValues constants)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);
		if (spdMips.Length != SpdMipCount)
		{
			throw new ArgumentException(
				$"The shading change pyramid needs exactly {SpdMipCount} mip targets, got {spdMips.Length}.",
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

		return new Fsr3ShadingChangePyramidPassConfig
		{
			Pipeline = pipeline,
			CurrentLumaHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(currentLuma)),
			PreviousLumaHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(previousLuma)),
			DilatedMotionVectorsHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(dilatedMotionVectors)),
			ExposureHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(exposure)),
			SpdGlobalAtomicHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(spdGlobalAtomic)),
			SpdMipHandles = mipHandles,
			PointSampler = _pointSampler,
			LinearSampler = _linearSampler,
			Constants = constants,
			SpdSetup = Fsr3SpdSetup.Create(constants.RenderSize)
		};
	}

	public void Record(RenderGraphContext context, in Fsr3ShadingChangePyramidPassConfig config)
	{
		ArgumentNullException.ThrowIfNull(context);

		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);

		var bindlessWriter = _bindlessWriter
			?? throw new InvalidOperationException("FSR3 shading change pyramid bindless writer was not initialized.");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("currentLumaReadHandle", config.CurrentLumaHandle.Value);
		bindlessWriter.SetUInt("previousLumaHandle", config.PreviousLumaHandle.Value);
		bindlessWriter.SetUInt("dilatedMotionVectorsReadHandle", config.DilatedMotionVectorsHandle.Value);
		bindlessWriter.SetUInt("exposureHandle", config.ExposureHandle.Value);
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
				FilterMode.Point, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp));
		}

		if (_linearSampler.IsValid == false)
		{
			_linearSampler = _bindlessRegistry.GetSamplerHandle(new SamplerDescriptor(
				FilterMode.Bilinear, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp));
		}
	}

	private IGfxPipeline EnsurePipeline(IGfxDevice device)
	{
		if (_pipeline is not null)
		{
			if (_compiledBackendKind.HasValue && _compiledBackendKind.Value != device.BackendKind)
			{
				throw new InvalidOperationException(
					$"Fsr3ShadingChangePyramidPass is already compiled for backend '{_compiledBackendKind.Value}', but was requested for '{device.BackendKind}'.");
			}

			return _pipeline;
		}

		EnsureReflectionWriters(device.BackendKind);
		var pipelineKey = new PipelineKey(
			PassKind.Compute,
			vertexEntryPoint: null,
			pixelEntryPoint: null,
			computeEntryPoint: "Fsr3ShadingChangePyramidCS",
			renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: default,
			shaderVariant: "fsr3_shading_change_pyramid.compute.slang");
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
			EngineShaderPrograms.Fsr3ShadingChangePyramid,
			"Fsr3ShadingChangePyramidCS",
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
