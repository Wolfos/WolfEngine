using System.Numerics;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

public sealed class BloomPass
{
	public enum Stage { Prefilter, Downsample, Upsample, Composite }

	private readonly IShaderProvider _shaderCompiler;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private readonly IGfxPipeline?[] _pipelines = new IGfxPipeline[4];
	private readonly ReadOnlyMemory<byte>[] _shaders = new ReadOnlyMemory<byte>[4];
	private readonly ComputeThreadGroupSize?[] _threadGroupSizes = new ComputeThreadGroupSize?[4];
	private readonly ShaderPropertyWriter?[] _bindlessWriters = new ShaderPropertyWriter?[4];
	private readonly ShaderPropertyWriter?[] _settingsWriters = new ShaderPropertyWriter?[4];
	private GraphicsBackendKind? _compiledBackendKind;
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;

	public BloomPass(IShaderProvider shaderCompiler, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	public BloomPassConfig BuildConfig(RenderGraphContext context, IGfxDevice device, Stage stage,
		RenderGraphResourceHandle sourceHandle, RenderGraphResourceHandle outputHandle,
		RenderGraphResourceHandle secondaryHandle, BloomConfig settings)
	{
		var pipeline = EnsurePipeline(device, stage);
		_bindlessRegistry.EnsureInitialized(device);
		if (_linearSampler.IsValid == false)
		{
			_linearSampler = _bindlessRegistry.GetSamplerHandle(new SamplerDescriptor(
				FilterMode.Bilinear, AddressMode.Clamp, AddressMode.Clamp, AddressMode.Clamp));
		}

		var source = context.GetTexture(sourceHandle);
		var output = context.GetTexture(outputHandle);
		var secondary = secondaryHandle.IsValid ? context.GetTexture(secondaryHandle) : null;
		return new BloomPassConfig
		{
			Pipeline = pipeline,
			SourceHandle = _bindlessRegistry.GetTextureHandle(source),
			SecondaryHandle = secondary is null ? DescriptorHandle.Invalid : _bindlessRegistry.GetTextureHandle(secondary),
			OutputHandle = _bindlessRegistry.RegisterRwTexture(output),
			LinearSampler = _linearSampler,
			SourceSize = new Int2(source.Descriptor.Width, source.Descriptor.Height),
			SecondarySize = secondary is null ? Int2.Zero : new Int2(secondary.Descriptor.Width, secondary.Descriptor.Height),
			OutputSize = new Int2(output.Descriptor.Width, output.Descriptor.Height),
			Settings = settings
		};
	}

	public void Record(RenderGraphContext context, Stage stage, in BloomPassConfig config)
	{
		var index = (int)stage;
		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);
		var bindless = _bindlessWriters[index] ?? throw new InvalidOperationException("Bloom bindless writer was not initialized.");
		bindless.Clear();
		bindless.SetUInt("sourceHandle", config.SourceHandle.Value);
		bindless.SetUInt("secondaryHandle", config.SecondaryHandle.Value);
		bindless.SetUInt("outputHandle", config.OutputHandle.Value);
		bindless.SetUInt("samplerHandle", config.LinearSampler.Value);
		commandList.SetComputeConstants(bindless.RegisterIndex, bindless.AsBytes());

		var settings = _settingsWriters[index] ?? throw new InvalidOperationException("Bloom settings writer was not initialized.");
		settings.Clear();
		settings.SetUInt("sourceSizeX", (uint)Math.Max(config.SourceSize.X, 1));
		settings.SetUInt("sourceSizeY", (uint)Math.Max(config.SourceSize.Y, 1));
		settings.SetUInt("secondarySizeX", (uint)Math.Max(config.SecondarySize.X, 1));
		settings.SetUInt("secondarySizeY", (uint)Math.Max(config.SecondarySize.Y, 1));
		settings.SetUInt("outputSizeX", (uint)Math.Max(config.OutputSize.X, 1));
		settings.SetUInt("outputSizeY", (uint)Math.Max(config.OutputSize.Y, 1));
		settings.SetFloat("threshold", MathF.Max(config.Settings.Threshold, 0.0f));
		settings.SetFloat("softKnee", MathF.Max(config.Settings.SoftKnee, 0.0f));
		settings.SetFloat("scatter", Math.Clamp(config.Settings.Scatter, 0.0f, 1.0f));
		settings.SetFloat("intensity", MathF.Max(config.Settings.Intensity, 0.0f));
		settings.SetVector3("tint", Vector3.Max(config.Settings.Tint, Vector3.Zero));
		commandList.SetComputeConstants(settings.RegisterIndex, settings.AsBytes());

		var groups = _threadGroupSizes[index] ?? throw new InvalidOperationException("Bloom threadgroup size was not initialized.");
		var (x, y, z) = groups.GetDispatchGroupCount((uint)Math.Max(config.OutputSize.X, 1), (uint)Math.Max(config.OutputSize.Y, 1));
		commandList.Dispatch(x, y, z);
	}

	private IGfxPipeline EnsurePipeline(IGfxDevice device, Stage stage)
	{
		var index = (int)stage;
		if (_pipelines[index] is not null)
		{
			if (_compiledBackendKind != device.BackendKind) throw new InvalidOperationException("BloomPass cannot be shared across graphics backends.");
			return _pipelines[index]!;
		}
		var entryPoint = stage switch
		{
			Stage.Prefilter => "BloomPrefilterCS", Stage.Downsample => "BloomDownsampleCS",
			Stage.Upsample => "BloomUpsampleCS", _ => "BloomCompositeCS"
		};
		var compiled = _shaderCompiler.GetComputeShaderWithReflection(EngineShaderPrograms.Bloom, entryPoint, device.BackendKind);
		_shaders[index] = compiled.Bytecode;
		_threadGroupSizes[index] = compiled.ThreadGroupSize;
		_bindlessWriters[index] = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("BindlessHandles"));
		_settingsWriters[index] = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("BloomSettings"));
		_compiledBackendKind = device.BackendKind;
		_pipelines[index] = device.GetOrCreatePipeline(new PipelineKey(PassKind.Compute, null, null, entryPoint,
			new RenderTargetFormats(Array.Empty<TextureFormat>()), new DepthStencilFormat(TextureFormat.Unknown), default,
			shaderVariant: "bloom.compute.slang"),
			new ShaderBytecodeSet(compute: _shaders[index], computeThreadGroupSize: _threadGroupSizes[index]));
		return _pipelines[index]!;
	}
}
