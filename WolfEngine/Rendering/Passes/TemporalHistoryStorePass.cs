using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

public sealed class TemporalHistoryStorePass
{
	private readonly ComputeKernel _kernel;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private readonly Action<RenderGraphContext> _execute;

	public TemporalHistoryStorePass(ShaderSource shaderSource, BindlessResourceRegistry bindlessRegistry)
	{
		ArgumentNullException.ThrowIfNull(shaderSource);

		_kernel = new ComputeKernel(shaderSource, EngineShaderPrograms.TaaHistoryStore, "TaaHistoryStoreCS");
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
		_execute = Execute;
	}

	/// <summary>Adds the TAA History Store node, which keeps the resolved colour and depth for next frame.</summary>
	internal void AddNodes(RenderGraph graph, in RenderViewResources resources)
	{
		graph.AddPass("TAA History Store", PassKind.Compute)
			.ReadTexture(resources.ResolvedSceneColor, ResourceState.ShaderResource)
			.ReadTexture(resources.GBufferDepth, ResourceState.ShaderResource)
			.WriteTexture(resources.HistoryColorWrite, ResourceState.UnorderedAccess)
			.WriteTexture(resources.HistoryDepthWrite, ResourceState.UnorderedAccess)
			.SetExecute(_execute);
	}

	private void Execute(RenderGraphContext context)
	{
		var config = BuildConfig(context, context.ViewState.FrameResources, context.Device);
		Record(context, in config);
	}

	private TemporalHistoryStorePassConfig BuildConfig(
		RenderGraphContext context,
		RenderViewResources resources,
		IGfxDevice device)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);

		var pipeline = _kernel.GetPipeline(device);
		_bindlessRegistry.EnsureInitialized(device);

		var sourceColor = context.GetTexture(resources.ResolvedSceneColor);
		var sourceDepth = context.GetTexture(resources.GBufferDepth);
		var historyColor = context.GetTexture(resources.HistoryColorWrite);
		var historyDepth = context.GetTexture(resources.HistoryDepthWrite);

		return new TemporalHistoryStorePassConfig
		{
			Pipeline = pipeline,
			SourceColorHandle = _bindlessRegistry.GetTextureHandle(sourceColor),
			SourceDepthHandle = _bindlessRegistry.RegisterDepthTexture(sourceDepth),
			HistoryColorHandle = _bindlessRegistry.RegisterRwTexture(historyColor),
			HistoryDepthHandle = _bindlessRegistry.RegisterRwTexture(historyDepth),
			RenderSize = resources.SceneFramebufferSize,
			ProjectionZBias = context.SceneData.UnjitteredProjection.M33,
			ProjectionZScale = context.SceneData.UnjitteredProjection.M43
		};
	}

	private void Record(RenderGraphContext context, in TemporalHistoryStorePassConfig config)
	{
		ArgumentNullException.ThrowIfNull(context);

		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);

		var bindlessWriter = _kernel.Writer("BindlessHandles");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("sourceColorHandle", config.SourceColorHandle.Value);
		bindlessWriter.SetUInt("sourceDepthHandle", config.SourceDepthHandle.Value);
		bindlessWriter.SetUInt("historyColorHandle", config.HistoryColorHandle.Value);
		bindlessWriter.SetUInt("historyDepthHandle", config.HistoryDepthHandle.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		var settingsWriter = _kernel.Writer("CopySettings");
		settingsWriter.Clear();
		settingsWriter.SetUInt("renderSizeX", (uint)Math.Max(config.RenderSize.X, 1));
		settingsWriter.SetUInt("renderSizeY", (uint)Math.Max(config.RenderSize.Y, 1));
		settingsWriter.SetFloat("projectionZBias", config.ProjectionZBias);
		settingsWriter.SetFloat("projectionZScale", config.ProjectionZScale);
		commandList.SetComputeConstants(settingsWriter.RegisterIndex, settingsWriter.AsBytes());

		var threadGroupSize = _kernel.ThreadGroupSize;
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(
			(uint)Math.Max(config.RenderSize.X, 1),
			(uint)Math.Max(config.RenderSize.Y, 1));
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}
}
