using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

/// <summary>FSR3 temporal luma-instability classification pass.</summary>
public sealed class Fsr3LumaInstabilityPass
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

	public Fsr3LumaInstabilityPass(IShaderProvider shaderProvider, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderProvider = shaderProvider ?? throw new ArgumentNullException(nameof(shaderProvider));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public Fsr3LumaInstabilityPassConfig BuildConfig(RenderGraphContext context, IGfxDevice device,
		RenderGraphResourceHandle exposure, RenderGraphResourceHandle dilatedReactiveMasks,
		RenderGraphResourceHandle dilatedMotionVectors, RenderGraphResourceHandle lumaHistoryRead,
		RenderGraphResourceHandle lumaHistoryWrite,
		RenderGraphResourceHandle farthestDepthMip1, RenderGraphResourceHandle currentLuma,
		RenderGraphResourceHandle lumaInstability, in Fsr3ConstantValues constants)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);
		var pipeline = EnsurePipeline(device);
		_bindlessRegistry.EnsureInitialized(device);
		EnsureSamplers();
		var historyRead = context.GetTexture(lumaHistoryRead);
		var historyWrite = context.GetTexture(lumaHistoryWrite);
		return new Fsr3LumaInstabilityPassConfig
		{
			Pipeline = pipeline,
			ExposureHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(exposure)),
			DilatedReactiveMasksHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(dilatedReactiveMasks)),
			DilatedMotionVectorsHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(dilatedMotionVectors)),
			LumaHistoryReadHandle = _bindlessRegistry.GetTextureHandle(historyRead),
			LumaHistoryWriteHandle = _bindlessRegistry.RegisterRwTexture(historyWrite),
			FarthestDepthMip1Handle = _bindlessRegistry.GetTextureHandle(context.GetTexture(farthestDepthMip1)),
			CurrentLumaHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(currentLuma)),
			LumaInstabilityHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(lumaInstability)),
			PointSampler = _pointSampler,
			LinearSampler = _linearSampler,
			Constants = constants
		};
	}

	public void Record(RenderGraphContext context, in Fsr3LumaInstabilityPassConfig config)
	{
		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);
		var handles = _bindlessWriter ?? throw new InvalidOperationException("FSR3 luma-instability handles were not initialized.");
		handles.Clear();
		handles.SetUInt("exposureHandle", config.ExposureHandle.Value);
		handles.SetUInt("dilatedReactiveMasksHandle", config.DilatedReactiveMasksHandle.Value);
		handles.SetUInt("dilatedMotionVectorsReadHandle", config.DilatedMotionVectorsHandle.Value);
		handles.SetUInt("lumaHistoryReadHandle", config.LumaHistoryReadHandle.Value);
		handles.SetUInt("lumaHistoryWriteHandle", config.LumaHistoryWriteHandle.Value);
		handles.SetUInt("farthestDepthMip1Handle", config.FarthestDepthMip1Handle.Value);
		handles.SetUInt("currentLumaReadHandle", config.CurrentLumaHandle.Value);
		handles.SetUInt("lumaInstabilityHandle", config.LumaInstabilityHandle.Value);
		handles.SetUInt("pointSamplerHandle", config.PointSampler.Value);
		handles.SetUInt("linearSamplerHandle", config.LinearSampler.Value);
		commandList.SetComputeConstants(handles.RegisterIndex, handles.AsBytes());

		var constants = _constantsWriter ?? throw new InvalidOperationException("FSR3 constants were not initialized.");
		Fsr3Constants.Write(constants, config.Constants);
		commandList.SetComputeConstants(constants.RegisterIndex, constants.AsBytes());
		commandList.Dispatch((uint)Math.Max((config.Constants.RenderSize.X + 7) / ThreadGroupDim, 1),
			(uint)Math.Max((config.Constants.RenderSize.Y + 7) / ThreadGroupDim, 1), 1);
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
			if (_backend != device.BackendKind) throw new InvalidOperationException("FSR3 luma-instability backend changed.");
			return _pipeline;
		}
		var compiled = _shaderProvider.GetComputeShaderWithReflection(
			EngineShaderPrograms.Fsr3LumaInstability, "Fsr3LumaInstabilityCS", device.BackendKind);
		_shader = compiled.Bytecode;
		_threadGroupSize = compiled.ThreadGroupSize;
		_bindlessWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("Fsr3BindlessHandles"));
		_constantsWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("cbFSR3Upscaler"));
		_backend = device.BackendKind;
		var key = new PipelineKey(
			PassKind.Compute,
			vertexEntryPoint: null,
			pixelEntryPoint: null,
			computeEntryPoint: "Fsr3LumaInstabilityCS",
			renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: default,
			shaderVariant: "fsr3_luma_instability.compute.slang");
		_pipeline = device.GetOrCreatePipeline(key,
			new ShaderBytecodeSet(compute: _shader, computeThreadGroupSize: _threadGroupSize));
		return _pipeline;
	}
}
