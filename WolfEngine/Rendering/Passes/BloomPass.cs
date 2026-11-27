using System.Numerics;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

public sealed class BloomPass
{
	public enum Stage { Prefilter, Downsample, Upsample, Composite }

	private readonly ComputeKernel[] _kernels;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;
	private readonly Action<RenderGraphContext> _compositeExecute;

	public BloomPass(ShaderSource shaderSource, BindlessResourceRegistry bindlessRegistry)
	{
		ArgumentNullException.ThrowIfNull(shaderSource);

		_kernels =
		[
			new ComputeKernel(shaderSource, EngineShaderPrograms.Bloom, "BloomPrefilterCS"),
			new ComputeKernel(shaderSource, EngineShaderPrograms.Bloom, "BloomDownsampleCS"),
			new ComputeKernel(shaderSource, EngineShaderPrograms.Bloom, "BloomUpsampleCS"),
			new ComputeKernel(shaderSource, EngineShaderPrograms.Bloom, "BloomCompositeCS")
		];
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
		_compositeExecute = ExecuteComposite;
	}

	/// <summary>The handles bloom draws through: its downsample and upsample chains and the composite target.</summary>
	internal readonly record struct Levels(
		RenderGraphResourceHandle[] Downsample,
		RenderGraphResourceHandle[] Upsample,
		RenderGraphResourceHandle Composite);

	/// <summary>
	/// Creates the level chain for a resolved scene colour of <paramref name="resolvedSceneColorSize"/>: each
	/// downsample level halves the previous one, every level but the smallest has an upsample target, and the
	/// composite matches the scene colour.
	/// </summary>
	internal static Levels CreateLevels(RenderGraphResourceRegistry resources, Int2 resolvedSceneColorSize, BloomQuality quality)
	{
		var levelCount = GetLevelCount(resolvedSceneColorSize, quality);
		var downsample = new RenderGraphResourceHandle[levelCount];
		var upsample = new RenderGraphResourceHandle[Math.Max(levelCount - 1, 0)];
		var levelSize = new Int2(
			Math.Max(1, (resolvedSceneColorSize.X + 1) / 2),
			Math.Max(1, (resolvedSceneColorSize.Y + 1) / 2));
		for (var level = 0; level < levelCount; level++)
		{
			downsample[level] = CreateTexture(resources, levelSize);
			if (level < upsample.Length)
			{
				upsample[level] = CreateTexture(resources, levelSize);
			}

			levelSize = new Int2(Math.Max(1, (levelSize.X + 1) / 2), Math.Max(1, (levelSize.Y + 1) / 2));
		}

		return new Levels(downsample, upsample, CreateTexture(resources, resolvedSceneColorSize));
	}

	/// <summary>
	/// Adds bloom's nodes: a prefilter into the first level, the downsample chain, the upsample chain back up, and
	/// the composite over the resolved scene colour.
	/// </summary>
	internal void AddNodes(RenderGraph graph, in RenderViewResources resources)
	{
		var resolved = resources.ResolvedSceneColor;
		var downsample = resources.BloomDownsampleLevels;
		var upsample = resources.BloomUpsampleLevels;

		var firstLevel = downsample[0];
		graph.AddPass("Bloom Prefilter", PassKind.Compute)
			.ReadTexture(resolved, ResourceState.ShaderResource)
			.WriteTexture(firstLevel, ResourceState.UnorderedAccess)
			.SetExecute(context => Execute(context, Stage.Prefilter, resolved, firstLevel, default));

		for (var level = 1; level < downsample.Length; level++)
		{
			var source = downsample[level - 1];
			var output = downsample[level];
			graph.AddPass($"Bloom Downsample {level}", PassKind.Compute)
				.ReadTexture(source, ResourceState.ShaderResource)
				.WriteTexture(output, ResourceState.UnorderedAccess)
				.SetExecute(context => Execute(context, Stage.Downsample, source, output, default));
		}

		for (var level = upsample.Length - 1; level >= 0; level--)
		{
			var small = level == upsample.Length - 1
				? downsample[level + 1]
				: upsample[level + 1];
			var large = downsample[level];
			var output = upsample[level];
			graph.AddPass($"Bloom Upsample {level}", PassKind.Compute)
				.ReadTexture(small, ResourceState.ShaderResource)
				.ReadTexture(large, ResourceState.ShaderResource)
				.WriteTexture(output, ResourceState.UnorderedAccess)
				.SetExecute(context => Execute(context, Stage.Upsample, small, output, large));
		}

		graph.AddPass("Bloom Composite", PassKind.Compute)
			.ReadTexture(resolved, ResourceState.ShaderResource)
			.ReadTexture(GetResult(resources), ResourceState.ShaderResource)
			.WriteTexture(resources.BloomCompositeSceneColor, ResourceState.UnorderedAccess)
			.SetExecute(_compositeExecute);
	}

	/// <summary>The finished bloom: the largest upsample level, or the first level when there is no upsample chain.</summary>
	private static RenderGraphResourceHandle GetResult(in RenderViewResources resources)
	{
		return resources.BloomUpsampleLevels.Length > 0
			? resources.BloomUpsampleLevels[0]
			: resources.BloomDownsampleLevels[0];
	}

	private void ExecuteComposite(RenderGraphContext context)
	{
		var resources = context.ViewState.FrameResources;
		Execute(context, Stage.Composite, resources.ResolvedSceneColor, resources.BloomCompositeSceneColor, GetResult(resources));
	}

	private void Execute(
		RenderGraphContext context,
		Stage stage,
		RenderGraphResourceHandle source,
		RenderGraphResourceHandle output,
		RenderGraphResourceHandle secondary)
	{
		var config = BuildConfig(context, context.Device, stage, source, output, secondary, context.ViewState.FrameResources.Config.Bloom);
		Record(context, stage, in config);
	}

	private static int GetLevelCount(Int2 size, BloomQuality quality)
	{
		var maximum = quality switch
		{
			BloomQuality.Low => 4,
			BloomQuality.Medium => 5,
			_ => 6
		};
		var count = 0;
		var levelSize = new Int2(Math.Max(1, (size.X + 1) / 2), Math.Max(1, (size.Y + 1) / 2));
		while (count < maximum)
		{
			count++;
			if (levelSize.X == 1 && levelSize.Y == 1) break;

			levelSize = new Int2(Math.Max(1, (levelSize.X + 1) / 2), Math.Max(1, (levelSize.Y + 1) / 2));
		}

		return count;
	}

	private static RenderGraphResourceHandle CreateTexture(RenderGraphResourceRegistry resources, Int2 size)
	{
		return resources.CreateTransientTexture(new TextureDescriptor(
			size.X,
			size.Y,
			TextureFormat.Rgba16Float,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess,
			new ColorRGBA(0.0f, 0.0f, 0.0f, 1.0f)));
	}

	private BloomPassConfig BuildConfig(RenderGraphContext context, IGfxDevice device, Stage stage,
		RenderGraphResourceHandle sourceHandle, RenderGraphResourceHandle outputHandle,
		RenderGraphResourceHandle secondaryHandle, BloomConfig settings)
	{
		var pipeline = _kernels[(int)stage].GetPipeline(device);
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

	private void Record(RenderGraphContext context, Stage stage, in BloomPassConfig config)
	{
		var index = (int)stage;
		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);
		var bindless = _kernels[index].Writer("BindlessHandles");
		bindless.Clear();
		bindless.SetUInt("sourceHandle", config.SourceHandle.Value);
		bindless.SetUInt("secondaryHandle", config.SecondaryHandle.Value);
		bindless.SetUInt("outputHandle", config.OutputHandle.Value);
		bindless.SetUInt("samplerHandle", config.LinearSampler.Value);
		commandList.SetComputeConstants(bindless.RegisterIndex, bindless.AsBytes());

		var settings = _kernels[index].Writer("BloomSettings");
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

		var groups = _kernels[index].ThreadGroupSize;
		var (x, y, z) = groups.GetDispatchGroupCount((uint)Math.Max(config.OutputSize.X, 1), (uint)Math.Max(config.OutputSize.Y, 1));
		commandList.Dispatch(x, y, z);
	}
}
