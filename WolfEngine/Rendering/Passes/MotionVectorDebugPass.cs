using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// Encodes the G-buffer velocity target as a flow field (hue = direction, brightness = speed)
/// for the Motion Vectors scene debug view. Only runs while that view is selected.
/// </summary>
public sealed class MotionVectorDebugPass
{
	private readonly ComputeKernel _kernel;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private readonly Action<RenderGraphContext> _execute;

	public MotionVectorDebugPass(ShaderSource shaderSource, BindlessResourceRegistry bindlessRegistry)
	{
		ArgumentNullException.ThrowIfNull(shaderSource);

		_kernel = new ComputeKernel(shaderSource, EngineShaderPrograms.MotionVectorDebug, "MotionVectorDebugCS");
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
		_execute = Execute;
	}

	/// <summary>Adds the Motion Vector Debug node, which encodes the GBuffer's velocity as a flow-field colour.</summary>
	internal void AddNodes(RenderGraph graph, in RenderViewResources resources)
	{
		graph.AddPass("Motion Vector Debug", PassKind.Compute)
			.ReadTexture(resources.GBufferVelocity, ResourceState.ShaderResource)
			.WriteTexture(resources.MotionVectorDebugColor, ResourceState.UnorderedAccess)
			.SetExecute(_execute);
	}

	private void Execute(RenderGraphContext context)
	{
		var resources = context.ViewState.FrameResources;
		var config = BuildConfig(
			context,
			context.Device,
			resources.GBufferVelocity,
			resources.MotionVectorDebugColor,
			resources.SceneFramebufferSize,
			resources.Config.MotionVectorDebug);
		Record(context, in config);
	}

	private MotionVectorDebugPassConfig BuildConfig(
		RenderGraphContext context,
		IGfxDevice device,
		RenderGraphResourceHandle velocity,
		RenderGraphResourceHandle output,
		Int2 renderSize,
		in MotionVectorDebugConfig config)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);

		var pipeline = _kernel.GetPipeline(device);
		_bindlessRegistry.EnsureInitialized(device);
		return new MotionVectorDebugPassConfig
		{
			Pipeline = pipeline,
			VelocityHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(velocity)),
			OutputHandle = _bindlessRegistry.RegisterRwTexture(context.GetTexture(output)),
			RenderSize = renderSize,
			MaxPixelsPerFrame = Math.Max(config.MaxPixelsPerFrame, 0.001f),
			LegendRadiusPixels = config.ShowLegend ? Math.Max(config.LegendRadiusPixels, 0) : 0
		};
	}

	private void Record(RenderGraphContext context, in MotionVectorDebugPassConfig config)
	{
		ArgumentNullException.ThrowIfNull(context);

		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);

		var bindlessWriter = _kernel.Writer("BindlessHandles");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("velocityHandle", config.VelocityHandle.Value);
		bindlessWriter.SetUInt("outputHandle", config.OutputHandle.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		var settingsWriter = _kernel.Writer("MotionVectorDebugSettings");
		settingsWriter.Clear();
		settingsWriter.SetUInt("renderSizeX", (uint)Math.Max(config.RenderSize.X, 1));
		settingsWriter.SetUInt("renderSizeY", (uint)Math.Max(config.RenderSize.Y, 1));
		settingsWriter.SetFloat("maxPixelsPerFrame", config.MaxPixelsPerFrame);
		settingsWriter.SetUInt("legendRadiusPixels", (uint)config.LegendRadiusPixels);
		commandList.SetComputeConstants(settingsWriter.RegisterIndex, settingsWriter.AsBytes());

		var threadGroupSize = _kernel.ThreadGroupSize;
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(
			(uint)Math.Max(config.RenderSize.X, 1),
			(uint)Math.Max(config.RenderSize.Y, 1));
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}
}
