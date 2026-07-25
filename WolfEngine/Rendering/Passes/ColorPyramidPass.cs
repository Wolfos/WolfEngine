using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// Builds the mip chain that screen-space tracing samples for rough reflections. The chain is
/// persistent across frames so reflections can read last frame's shaded color while they run
/// ahead of deferred lighting.
/// </summary>
public sealed class ColorPyramidPass
{
	public enum Stage { Copy, Downsample }

	private readonly IShaderProvider _shaderProvider;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private readonly IGfxPipeline?[] _pipelines = new IGfxPipeline[2];
	private readonly ReadOnlyMemory<byte>[] _shaders = new ReadOnlyMemory<byte>[2];
	private readonly ComputeThreadGroupSize?[] _threadGroupSizes = new ComputeThreadGroupSize?[2];
	private readonly ShaderPropertyWriter?[] _bindlessWriters = new ShaderPropertyWriter?[2];
	private readonly ShaderPropertyWriter?[] _settingsWriters = new ShaderPropertyWriter?[2];
	private GraphicsBackendKind? _compiledBackendKind;
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;

	public ColorPyramidPass(IShaderProvider shaderProvider, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderProvider = shaderProvider ?? throw new ArgumentNullException(nameof(shaderProvider));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public ColorPyramidPassConfig BuildConfig(
		RenderGraphContext context,
		IGfxDevice device,
		Stage stage,
		RenderGraphResourceHandle sourceHandle,
		RenderGraphResourceHandle outputHandle)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);

		var pipeline = EnsurePipeline(device, stage);
		_bindlessRegistry.EnsureInitialized(device);
		if (_linearSampler.IsValid == false)
		{
			_linearSampler = _bindlessRegistry.GetSamplerHandle(new SamplerDescriptor(
				FilterMode.Bilinear, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp));
		}

		var source = context.GetTexture(sourceHandle);
		var output = context.GetTexture(outputHandle);
		return new ColorPyramidPassConfig
		{
			Pipeline = pipeline,
			SourceHandle = _bindlessRegistry.GetTextureHandle(source),
			OutputHandle = _bindlessRegistry.RegisterRwTexture(output),
			LinearSampler = _linearSampler,
			SourceSize = new Int2(source.Descriptor.Width, source.Descriptor.Height),
			OutputSize = new Int2(output.Descriptor.Width, output.Descriptor.Height)
		};
	}

	public void Record(RenderGraphContext context, Stage stage, in ColorPyramidPassConfig config)
	{
		ArgumentNullException.ThrowIfNull(context);

		var index = (int)stage;
		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);

		var bindlessWriter = _bindlessWriters[index]
			?? throw new InvalidOperationException("Color pyramid bindless writer was not initialized.");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("sourceHandle", config.SourceHandle.Value);
		bindlessWriter.SetUInt("outputHandle", config.OutputHandle.Value);
		bindlessWriter.SetUInt("samplerHandle", config.LinearSampler.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		var settingsWriter = _settingsWriters[index]
			?? throw new InvalidOperationException("Color pyramid settings writer was not initialized.");
		settingsWriter.Clear();
		settingsWriter.SetUInt("sourceSizeX", (uint)Math.Max(config.SourceSize.X, 1));
		settingsWriter.SetUInt("sourceSizeY", (uint)Math.Max(config.SourceSize.Y, 1));
		settingsWriter.SetUInt("outputSizeX", (uint)Math.Max(config.OutputSize.X, 1));
		settingsWriter.SetUInt("outputSizeY", (uint)Math.Max(config.OutputSize.Y, 1));
		commandList.SetComputeConstants(settingsWriter.RegisterIndex, settingsWriter.AsBytes());

		var threadGroupSize = _threadGroupSizes[index]
			?? throw new InvalidOperationException("Color pyramid threadgroup size was not initialized.");
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(
			(uint)Math.Max(config.OutputSize.X, 1),
			(uint)Math.Max(config.OutputSize.Y, 1));
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}

	private IGfxPipeline EnsurePipeline(IGfxDevice device, Stage stage)
	{
		var index = (int)stage;
		if (_pipelines[index] is not null)
		{
			if (_compiledBackendKind != device.BackendKind)
			{
				throw new InvalidOperationException("ColorPyramidPass cannot be shared across graphics backends.");
			}

			return _pipelines[index]!;
		}

		var entryPoint = stage == Stage.Copy ? "ColorPyramidCopyCS" : "ColorPyramidDownsampleCS";
		var compiled = _shaderProvider.GetComputeShaderWithReflection(
			EngineShaderPrograms.ColorPyramid,
			entryPoint,
			device.BackendKind);
		_shaders[index] = compiled.Bytecode;
		_threadGroupSizes[index] = compiled.ThreadGroupSize;
		_bindlessWriters[index] = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("BindlessHandles"));
		_settingsWriters[index] = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("ColorPyramidSettings"));
		_compiledBackendKind = device.BackendKind;
		_pipelines[index] = device.GetOrCreatePipeline(
			new PipelineKey(
				PassKind.Compute,
				vertexEntryPoint: null,
				pixelEntryPoint: null,
				computeEntryPoint: entryPoint,
				renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
				depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
				renderState: default,
				shaderVariant: "color_pyramid.compute.slang"),
			new ShaderBytecodeSet(compute: _shaders[index], computeThreadGroupSize: _threadGroupSizes[index]));
		return _pipelines[index]!;
	}
}
