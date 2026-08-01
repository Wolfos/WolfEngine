using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

/// <summary>Optional FSR3 diagnostic mosaic for the packed temporal intermediates.</summary>
public sealed class Fsr3DebugViewPass
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

	public Fsr3DebugViewPass(IShaderProvider shaderProvider, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderProvider = shaderProvider ?? throw new ArgumentNullException(nameof(shaderProvider));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public Fsr3DebugViewPassConfig BuildConfig(RenderGraphContext context, IGfxDevice device,
		RenderGraphResourceHandle dilatedReactiveMasks, RenderGraphResourceHandle dilatedMotionVectors,
		RenderGraphResourceHandle dilatedDepth, RenderGraphResourceHandle internalUpscaledColor,
		RenderGraphResourceHandle currentLuma, RenderGraphResourceHandle previousLuma,
		RenderGraphResourceHandle output, RenderGraphResourceHandle exposure, in Fsr3ConstantValues constants)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);
		var pipeline = EnsurePipeline(device);
		_bindlessRegistry.EnsureInitialized(device);
		EnsureSamplers();
		return new Fsr3DebugViewPassConfig
		{
			Pipeline = pipeline,
			DilatedReactiveMasksHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(dilatedReactiveMasks)),
			DilatedMotionVectorsHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(dilatedMotionVectors)),
			DilatedDepthHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(dilatedDepth)),
			InternalUpscaledColorHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(internalUpscaledColor)),
			CurrentLumaHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(currentLuma)),
			PreviousLumaHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(previousLuma)),
			OutputHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(output)),
			ExposureHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(exposure)),
			PointSampler = _pointSampler,
			LinearSampler = _linearSampler,
			Constants = constants
		};
	}

	public void Record(RenderGraphContext context, in Fsr3DebugViewPassConfig config)
	{
		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);
		var handles = _bindlessWriter ?? throw new InvalidOperationException("FSR3 debug handles were not initialized.");
		handles.Clear();
		handles.SetUInt("dilatedReactiveMasksHandle", config.DilatedReactiveMasksHandle.Value);
		handles.SetUInt("dilatedMotionVectorsReadHandle", config.DilatedMotionVectorsHandle.Value);
		handles.SetUInt("dilatedDepthHandle", config.DilatedDepthHandle.Value);
		handles.SetUInt("internalUpscaledColorHandle", config.InternalUpscaledColorHandle.Value);
		handles.SetUInt("currentLumaReadHandle", config.CurrentLumaHandle.Value);
		handles.SetUInt("previousLumaHandle", config.PreviousLumaHandle.Value);
		handles.SetUInt("upscaledOutputHandle", config.OutputHandle.Value);
		handles.SetUInt("exposureHandle", config.ExposureHandle.Value);
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
			if (_backend != device.BackendKind) throw new InvalidOperationException("FSR3 debug pass backend changed.");
			return _pipeline;
		}
		var compiled = _shaderProvider.GetComputeShaderWithReflection(
			EngineShaderPrograms.Fsr3DebugView, "Fsr3DebugViewCS", device.BackendKind);
		_shader = compiled.Bytecode;
		_threadGroupSize = compiled.ThreadGroupSize;
		_bindlessWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("Fsr3BindlessHandles"));
		_constantsWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("cbFSR3Upscaler"));
		_backend = device.BackendKind;
		var key = new PipelineKey(
			PassKind.Compute,
			vertexEntryPoint: null,
			pixelEntryPoint: null,
			computeEntryPoint: "Fsr3DebugViewCS",
			renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: default,
			shaderVariant: "fsr3_debug_view.compute.slang");
		_pipeline = device.GetOrCreatePipeline(key,
			new ShaderBytecodeSet(compute: _shader, computeThreadGroupSize: _threadGroupSize));
		return _pipeline;
	}
}
