using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

public sealed class TonemappingPass
{
	private const int ModeCount = 3;

	private readonly IShaderProvider _shaderCompiler;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private readonly IGfxPipeline?[] _pipelines = new IGfxPipeline?[ModeCount];
	private readonly ReadOnlyMemory<byte>[] _computeShaders = new ReadOnlyMemory<byte>[ModeCount];
	private readonly ComputeThreadGroupSize?[] _threadGroupSizes = new ComputeThreadGroupSize?[ModeCount];
	private readonly ShaderPropertyWriter?[] _bindlessWriters = new ShaderPropertyWriter?[ModeCount];
	private readonly ShaderPropertyWriter?[] _settingsWriters = new ShaderPropertyWriter?[ModeCount];
	private GraphicsBackendKind? _compiledBackendKind;
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;

	public TonemappingPass(IShaderProvider shaderCompiler, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public static string GetEntryPoint(TonemappingMode mode) => mode switch
	{
		TonemappingMode.Aces => "TonemappingAces",
		TonemappingMode.AgX => "TonemappingAgX",
		TonemappingMode.KhronosPbrNeutral => "TonemappingPbrNeutral",
		_ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown tonemapping mode.")
	};

	public TonemappingPassConfig BuildConfig(
		RenderGraphContext context,
		RenderGraphFrameResources resources,
		IGfxDevice device)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);

		var settings = resources.Config.Tonemapping;
		var pipeline = EnsurePipeline(device, settings.Mode);
		_bindlessRegistry.EnsureInitialized(device);
		if (_linearSampler.IsValid == false)
		{
			_linearSampler = _bindlessRegistry.GetSamplerHandle(new SamplerDescriptor(
				FilterMode.Bilinear,
				AddressMode.Clamp,
				AddressMode.Clamp,
				AddressMode.Clamp));
		}

		var input = context.GetTexture(resources.BloomCompositeSceneColor.IsValid
			? resources.BloomCompositeSceneColor
			: resources.ResolvedSceneColor);
		var output = context.GetTexture(resources.TonemappedLinearSceneColor);

		return new TonemappingPassConfig
		{
			Pipeline = pipeline,
			InputHandle = _bindlessRegistry.GetTextureHandle(input),
			OutputHandle = _bindlessRegistry.RegisterRwTexture(output),
			LinearSampler = _linearSampler,
			RenderSize = resources.FramebufferSize,
			Settings = settings
		};
	}

	public void Record(RenderGraphContext context, in TonemappingPassConfig config)
	{
		ArgumentNullException.ThrowIfNull(context);

		var index = GetModeIndex(config.Settings.Mode);
		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);

		var bindlessWriter = _bindlessWriters[index]
		                     ?? throw new InvalidOperationException("Tonemapping bindless writer was not initialized.");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("inputHandle", config.InputHandle.Value);
		bindlessWriter.SetUInt("outputHandle", config.OutputHandle.Value);
		bindlessWriter.SetUInt("samplerHandle", config.LinearSampler.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		var settingsWriter = _settingsWriters[index]
		                     ?? throw new InvalidOperationException("Tonemapping settings writer was not initialized.");
		settingsWriter.Clear();
		settingsWriter.SetUInt("renderSizeX", (uint)Math.Max(config.RenderSize.X, 1));
		settingsWriter.SetUInt("renderSizeY", (uint)Math.Max(config.RenderSize.Y, 1));
		settingsWriter.SetFloat("exposure", MathF.Max(config.Settings.Exposure, 0.0f));
		commandList.SetComputeConstants(settingsWriter.RegisterIndex, settingsWriter.AsBytes());

		var threadGroupSize = _threadGroupSizes[index]
		                      ?? throw new InvalidOperationException(
			                      "Tonemapping threadgroup size was not initialized.");
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(
			(uint)Math.Max(config.RenderSize.X, 1),
			(uint)Math.Max(config.RenderSize.Y, 1));
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}

	private static int GetModeIndex(TonemappingMode mode)
	{
		var index = (int)mode;
		if (index < 0 || index >= ModeCount)
		{
			throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown tonemapping mode.");
		}

		return index;
	}

	private IGfxPipeline EnsurePipeline(IGfxDevice device, TonemappingMode mode)
	{
		if (_compiledBackendKind.HasValue && _compiledBackendKind.Value != device.BackendKind)
		{
			throw new InvalidOperationException(
				$"TonemappingPass is already compiled for backend '{_compiledBackendKind.Value}', but was requested for '{device.BackendKind}'.");
		}

		var index = GetModeIndex(mode);
		if (_pipelines[index] is { } existing)
		{
			return existing;
		}

		var entryPoint = GetEntryPoint(mode);
		var compiled = _shaderCompiler.GetComputeShaderWithReflection(
			EngineShaderPrograms.Tonemapping,
			entryPoint,
			device.BackendKind);
		_computeShaders[index] = compiled.Bytecode;
		_threadGroupSizes[index] = compiled.ThreadGroupSize;
		var reflection = compiled.ReflectionLayout;
		_bindlessWriters[index] = new ShaderPropertyWriter(reflection.GetConstantBuffer("BindlessHandles"));
		_settingsWriters[index] = new ShaderPropertyWriter(reflection.GetConstantBuffer("TonemappingSettings"));
		_compiledBackendKind = device.BackendKind;

		var pipelineKey = new PipelineKey(
			PassKind.Compute,
			vertexEntryPoint: null,
			pixelEntryPoint: null,
			computeEntryPoint: entryPoint,
			renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: default,
			shaderVariant: "tonemapping.compute.slang");
		var pipeline = device.GetOrCreatePipeline(
			pipelineKey,
			new ShaderBytecodeSet(compute: _computeShaders[index], computeThreadGroupSize: _threadGroupSizes[index]));
		_pipelines[index] = pipeline;
		return pipeline;
	}
}
