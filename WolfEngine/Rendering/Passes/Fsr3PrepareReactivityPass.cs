using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// FSR3 prepare-reactivity pass: classifies history confidence at render resolution and
/// seeds display-resolution locks for thin features.
/// </summary>
/// <remarks>
/// The reconstructed-depth input remains bound through the R32Uint UAV alias so its stored
/// float bits can be read exactly; the engine's regular bindless SRV view is float-typed.
/// The accumulation texture is intentionally bound as both SRV and UAV because this pass
/// reprojects its previous value and writes the current value in-place, matching upstream.
///
/// Not yet wired into the render graph.
/// </remarks>
public sealed class Fsr3PrepareReactivityPass
{
	private const int ThreadGroupWorkRegionDim = 8;

	private readonly IShaderProvider _shaderCompiler;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private IGfxPipeline? _pipeline;
	private ReadOnlyMemory<byte> _computeShader;
	private ComputeThreadGroupSize? _threadGroupSize;
	private GraphicsBackendKind? _compiledBackendKind;
	private ShaderPropertyWriter? _bindlessWriter;
	private ShaderPropertyWriter? _constantsWriter;
	private ShaderPropertyWriter? _settingsWriter;
	private DescriptorHandle _pointSampler = DescriptorHandle.Invalid;
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;

	public Fsr3PrepareReactivityPass(IShaderProvider shaderCompiler, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public Fsr3PrepareReactivityPassConfig BuildConfig(
		RenderGraphContext context,
		IGfxDevice device,
		RenderGraphResourceHandle reconstructedPrevNearestDepth,
		RenderGraphResourceHandle dilatedMotionVectors,
		RenderGraphResourceHandle dilatedDepth,
		RenderGraphResourceHandle reactiveMask,
		RenderGraphResourceHandle transparencyAndCompositionMask,
		RenderGraphResourceHandle accumulationRead,
		RenderGraphResourceHandle accumulationWrite,
		RenderGraphResourceHandle shadingChange,
		RenderGraphResourceHandle currentLuma,
		RenderGraphResourceHandle exposure,
		RenderGraphResourceHandle dilatedReactiveMasks,
		RenderGraphResourceHandle newLocks,
		in Fsr3ConstantValues constants,
		float alphaTestReactiveScale,
		float transparencyAndCompositionMaskScale)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);

		var pipeline = EnsurePipeline(device);
		_bindlessRegistry.EnsureInitialized(device);
		EnsureSamplers();

		var accumulationReadTexture = context.GetTexture(accumulationRead);
		var accumulationWriteTexture = context.GetTexture(accumulationWrite);
		return new Fsr3PrepareReactivityPassConfig
		{
			Pipeline = pipeline,
			ReconstructedPrevNearestDepthHandle = _bindlessRegistry.RegisterRwTexture(
				context.GetTexture(reconstructedPrevNearestDepth)),
			DilatedMotionVectorsHandle = _bindlessRegistry.GetTextureHandle(
				context.GetTexture(dilatedMotionVectors)),
			DilatedDepthHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(dilatedDepth)),
			ReactiveMaskHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(reactiveMask)),
			TransparencyAndCompositionMaskHandle = _bindlessRegistry.GetTextureHandle(
				context.GetTexture(transparencyAndCompositionMask)),
			AccumulationReadHandle = _bindlessRegistry.GetTextureHandle(accumulationReadTexture),
			ShadingChangeHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(shadingChange)),
			CurrentLumaHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(currentLuma)),
			ExposureHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(exposure)),
			DilatedReactiveMasksHandle = _bindlessRegistry.RegisterRwTexture(
				context.GetTexture(dilatedReactiveMasks)),
			NewLocksHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(newLocks)),
			AccumulationWriteHandle = _bindlessRegistry.RegisterRwTexture(accumulationWriteTexture),
			PointSampler = _pointSampler,
			LinearSampler = _linearSampler,
			Constants = constants,
			AlphaTestReactiveScale = Math.Clamp(alphaTestReactiveScale, 0.0f, 1.0f),
			TransparencyAndCompositionMaskScale = Math.Clamp(
				transparencyAndCompositionMaskScale, 0.0f, 1.0f)
		};
	}

	public void Record(RenderGraphContext context, in Fsr3PrepareReactivityPassConfig config)
	{
		ArgumentNullException.ThrowIfNull(context);

		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);

		var bindlessWriter = _bindlessWriter
			?? throw new InvalidOperationException("FSR3 prepare-reactivity bindless writer was not initialized.");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("reconstructedPrevNearestDepthHandle", config.ReconstructedPrevNearestDepthHandle.Value);
		bindlessWriter.SetUInt("dilatedMotionVectorsReadHandle", config.DilatedMotionVectorsHandle.Value);
		bindlessWriter.SetUInt("dilatedDepthHandle", config.DilatedDepthHandle.Value);
		bindlessWriter.SetUInt("reactiveMaskHandle", config.ReactiveMaskHandle.Value);
		bindlessWriter.SetUInt(
			"transparencyAndCompositionMaskHandle", config.TransparencyAndCompositionMaskHandle.Value);
		bindlessWriter.SetUInt("accumulationReadHandle", config.AccumulationReadHandle.Value);
		bindlessWriter.SetUInt("shadingChangeHandle", config.ShadingChangeHandle.Value);
		bindlessWriter.SetUInt("currentLumaReadHandle", config.CurrentLumaHandle.Value);
		bindlessWriter.SetUInt("exposureHandle", config.ExposureHandle.Value);
		bindlessWriter.SetUInt("dilatedReactiveMasksHandle", config.DilatedReactiveMasksHandle.Value);
		bindlessWriter.SetUInt("newLocksHandle", config.NewLocksHandle.Value);
		bindlessWriter.SetUInt("accumulationWriteHandle", config.AccumulationWriteHandle.Value);
		bindlessWriter.SetUInt("pointSamplerHandle", config.PointSampler.Value);
		bindlessWriter.SetUInt("linearSamplerHandle", config.LinearSampler.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		var constantsWriter = _constantsWriter
			?? throw new InvalidOperationException("FSR3 constants writer was not initialized.");
		Fsr3Constants.Write(constantsWriter, config.Constants);
		commandList.SetComputeConstants(constantsWriter.RegisterIndex, constantsWriter.AsBytes());

		var settingsWriter = _settingsWriter
			?? throw new InvalidOperationException("FSR3 prepare-reactivity settings writer was not initialized.");
		settingsWriter.Clear();
		settingsWriter.SetFloat("alphaTestReactiveScale", config.AlphaTestReactiveScale);
		settingsWriter.SetFloat(
			"transparencyAndCompositionMaskScale",
			config.TransparencyAndCompositionMaskScale);
		commandList.SetComputeConstants(settingsWriter.RegisterIndex, settingsWriter.AsBytes());

		var renderSize = config.Constants.RenderSize;
		commandList.Dispatch(
			(uint)Math.Max((renderSize.X + ThreadGroupWorkRegionDim - 1) / ThreadGroupWorkRegionDim, 1),
			(uint)Math.Max((renderSize.Y + ThreadGroupWorkRegionDim - 1) / ThreadGroupWorkRegionDim, 1),
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
					$"Fsr3PrepareReactivityPass is already compiled for backend '{_compiledBackendKind.Value}', but was requested for '{device.BackendKind}'.");
			}

			return _pipeline;
		}

		EnsureReflectionWriters(device.BackendKind);
		var pipelineKey = new PipelineKey(
			PassKind.Compute,
			vertexEntryPoint: null,
			pixelEntryPoint: null,
			computeEntryPoint: "Fsr3PrepareReactivityCS",
			renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: default,
			shaderVariant: "fsr3_prepare_reactivity.compute.slang");
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
		    _settingsWriter is not null &&
		    _computeShader.IsEmpty == false &&
		    _threadGroupSize.HasValue)
		{
			return;
		}

		var compiled = _shaderCompiler.GetComputeShaderWithReflection(
			EngineShaderPrograms.Fsr3PrepareReactivity,
			"Fsr3PrepareReactivityCS",
			backendKind);
		_computeShader = compiled.Bytecode;
		_threadGroupSize = compiled.ThreadGroupSize;
		var reflection = compiled.ReflectionLayout;
		_bindlessWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("Fsr3BindlessHandles"));
		_constantsWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("cbFSR3Upscaler"));
		_settingsWriter = new ShaderPropertyWriter(reflection.GetConstantBuffer("cbPrepareReactivity"));
		_compiledBackendKind = backendKind;
	}
}
