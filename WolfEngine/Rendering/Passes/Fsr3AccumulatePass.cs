using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

/// <summary>FSR3 temporal accumulation and native-resolution reconstruction pass.</summary>
public sealed class Fsr3AccumulatePass
{
	private const int ThreadGroupDim = 8;
	private readonly IShaderProvider _shaderProvider;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private IGfxPipeline? _pipeline;
	private ReadOnlyMemory<byte> _shader;
	private ComputeThreadGroupSize? _threadGroupSize;
	private GraphicsBackendKind? _backend;
	private ShaderPropertyWriter? _bindlessWriter;
	private ShaderPropertyWriter? _constantsWriter;
	private DescriptorHandle _pointSampler = DescriptorHandle.Invalid;
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;

	public Fsr3AccumulatePass(IShaderProvider shaderProvider, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderProvider = shaderProvider ?? throw new ArgumentNullException(nameof(shaderProvider));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public Fsr3AccumulatePassConfig BuildConfig(RenderGraphContext context, IGfxDevice device,
		RenderGraphResourceHandle exposure, RenderGraphResourceHandle inputColor,
		RenderGraphResourceHandle dilatedMotionVectors, RenderGraphResourceHandle dilatedReactiveMasks,
		RenderGraphResourceHandle farthestDepthMip1, RenderGraphResourceHandle lumaInstability,
		RenderGraphResourceHandle newLocks, RenderGraphResourceHandle historyRead,
		RenderGraphResourceHandle historyWrite, in Fsr3ConstantValues constants)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);
		var pipeline = EnsurePipeline(device);
		_bindlessRegistry.EnsureInitialized(device);
		EnsureSamplers();
		return new Fsr3AccumulatePassConfig
		{
			Pipeline = pipeline,
			ExposureHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(exposure)),
			InputColorHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(inputColor)),
			DilatedMotionVectorsHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(dilatedMotionVectors)),
			DilatedReactiveMasksHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(dilatedReactiveMasks)),
			FarthestDepthMip1Handle = _bindlessRegistry.GetTextureHandle(context.GetTexture(farthestDepthMip1)),
			LumaInstabilityHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(lumaInstability)),
			NewLocksHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(newLocks)),
			HistoryReadHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(historyRead)),
			HistoryWriteHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(historyWrite)),
			PointSampler = _pointSampler,
			LinearSampler = _linearSampler,
			Constants = constants
		};
	}

	public void Record(RenderGraphContext context, in Fsr3AccumulatePassConfig config)
	{
		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);
		var handles = _bindlessWriter ?? throw new InvalidOperationException("FSR3 accumulate handles were not initialized.");
		handles.Clear();
		handles.SetUInt("exposureHandle", config.ExposureHandle.Value);
		handles.SetUInt("inputColorHandle", config.InputColorHandle.Value);
		handles.SetUInt("dilatedMotionVectorsReadHandle", config.DilatedMotionVectorsHandle.Value);
		handles.SetUInt("dilatedReactiveMasksHandle", config.DilatedReactiveMasksHandle.Value);
		handles.SetUInt("farthestDepthMip1Handle", config.FarthestDepthMip1Handle.Value);
		handles.SetUInt("lumaInstabilityHandle", config.LumaInstabilityHandle.Value);
		handles.SetUInt("newLocksHandle", config.NewLocksHandle.Value);
		handles.SetUInt("internalUpscaledColorHandle", config.HistoryReadHandle.Value);
		handles.SetUInt("internalUpscaledColorWriteHandle", config.HistoryWriteHandle.Value);
		handles.SetUInt("pointSamplerHandle", config.PointSampler.Value);
		handles.SetUInt("linearSamplerHandle", config.LinearSampler.Value);
		commandList.SetComputeConstants(handles.RegisterIndex, handles.AsBytes());
		var constants = _constantsWriter ?? throw new InvalidOperationException("FSR3 constants were not initialized.");
		Fsr3Constants.Write(constants, config.Constants);
		commandList.SetComputeConstants(constants.RegisterIndex, constants.AsBytes());
		commandList.Dispatch((uint)Math.Max((config.Constants.UpscaleSize.X + 7) / ThreadGroupDim, 1),
			(uint)Math.Max((config.Constants.UpscaleSize.Y + 7) / ThreadGroupDim, 1), 1);
	}

	private void EnsureSamplers()
	{
		if (!_pointSampler.IsValid) _pointSampler = _bindlessRegistry.GetSamplerHandle(new SamplerDescriptor(
			FilterMode.Point, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp));
		if (!_linearSampler.IsValid) _linearSampler = _bindlessRegistry.GetSamplerHandle(new SamplerDescriptor(
			FilterMode.Bilinear, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp));
	}

	private IGfxPipeline EnsurePipeline(IGfxDevice device)
	{
		if (_pipeline is not null)
		{
			if (_backend != device.BackendKind) throw new InvalidOperationException("FSR3 accumulate backend changed.");
			return _pipeline;
		}
		var compiled = _shaderProvider.GetComputeShaderWithReflection(
			EngineShaderPrograms.Fsr3Accumulate, "Fsr3AccumulateCS", device.BackendKind);
		_shader = compiled.Bytecode;
		_threadGroupSize = compiled.ThreadGroupSize;
		_bindlessWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("Fsr3BindlessHandles"));
		_constantsWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("cbFSR3Upscaler"));
		_backend = device.BackendKind;
		var key = new PipelineKey(PassKind.Compute, vertexEntryPoint: null, pixelEntryPoint: null,
			computeEntryPoint: "Fsr3AccumulateCS", renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown), renderState: default,
			shaderVariant: "fsr3_accumulate.compute.slang");
		_pipeline = device.GetOrCreatePipeline(key,
			new ShaderBytecodeSet(compute: _shader, computeThreadGroupSize: _threadGroupSize));
		return _pipeline;
	}
}
