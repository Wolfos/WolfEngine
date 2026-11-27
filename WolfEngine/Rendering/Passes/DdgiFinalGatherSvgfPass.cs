using System.Numerics;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// Spatiotemporal variance-guided filtering (SVGF) of the DDGI final gather at its trace resolution. Each frame runs
/// temporal accumulation, variance estimation, then one filter dispatch per a-trous level. See
/// ddgi_final_gather_svgf.compute.slang.
/// </summary>
public sealed class DdgiFinalGatherSvgfPass
{
	public enum Stage
	{
		Temporal,
		Variance,
		Filter
	}

	private readonly ComputeKernel[] _kernels;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private readonly Action<RenderGraphContext> _temporalExecute;
	private readonly Action<RenderGraphContext> _varianceExecute;
	private readonly Action<RenderGraphContext>[] _filterExecutes;

	public DdgiFinalGatherSvgfPass(ShaderSource shaderSource, BindlessResourceRegistry bindlessRegistry)
	{
		ArgumentNullException.ThrowIfNull(shaderSource);

		// Indexed by Stage.
		_kernels =
		[
			new ComputeKernel(shaderSource, EngineShaderPrograms.DdgiFinalGatherSvgf, "DdgiSvgfTemporalCS"),
			new ComputeKernel(shaderSource, EngineShaderPrograms.DdgiFinalGatherSvgf, "DdgiSvgfVarianceCS"),
			new ComputeKernel(shaderSource, EngineShaderPrograms.DdgiFinalGatherSvgf, "DdgiSvgfFilterCS")
		];
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
		_temporalExecute = context => Execute(context, Stage.Temporal, 0);
		_varianceExecute = context => Execute(context, Stage.Variance, 0);
		_filterExecutes = new Action<RenderGraphContext>[DdgiUtilities.MaxFinalGatherFilterIterations];
		for (var iteration = 0; iteration < _filterExecutes.Length; iteration++)
		{
			var capturedIteration = iteration;
			_filterExecutes[iteration] = context => Execute(context, Stage.Filter, capturedIteration);
		}
	}

	/// <summary>
	/// The view's intermediates: the variance pass's output, which the first a-trous level filters, and the two
	/// targets the levels between the first and the last ping-pong between. Only this pass reads or writes them.
	/// </summary>
	private sealed class ViewHandles
	{
		public RenderGraphResourceHandle FilterInput;
		public RenderGraphResourceHandle FilterA;
		public RenderGraphResourceHandle FilterB;
	}

	/// <summary>
	/// Adds the denoiser's nodes: temporal accumulation against last frame's history, the variance estimate, and
	/// one a-trous filter level per iteration.
	/// </summary>
	internal void AddNodes(RenderGraph graph, RenderViewState view)
	{
		var resources = view.FrameResources;
		var historyRead = resources.DdgiFinalGatherHistoryRead
			?? throw new InvalidOperationException("DDGI final gather history was not imported.");
		var historyWrite = resources.DdgiFinalGatherHistoryWrite
			?? throw new InvalidOperationException("DDGI final gather history was not imported.");
		var guide = historyWrite[DdgiFinalGatherHistorySlot.Guide];
		var moments = historyWrite[DdgiFinalGatherHistorySlot.Moments];

		// The intermediates have the raw trace's shape.
		var traceDescriptor = graph.Resources.GetTextureDescriptor(resources.DdgiFinalGatherTrace);
		var handles = view.GetPassState<ViewHandles>();
		handles.FilterInput = graph.Resources.CreateTransientTexture(traceDescriptor);
		handles.FilterA = resources.DdgiFinalGatherFilterIterations >= 3
			? graph.Resources.CreateTransientTexture(traceDescriptor)
			: default;
		handles.FilterB = resources.DdgiFinalGatherFilterIterations >= 4
			? graph.Resources.CreateTransientTexture(traceDescriptor)
			: default;

		graph.AddPass("DDGI Final Gather SVGF Temporal", PassKind.Compute)
			.ReadTexture(resources.GBufferDepth, ResourceState.ShaderResource)
			.ReadTexture(resources.GBufferNormal, ResourceState.ShaderResource)
			.ReadTexture(resources.GBufferVelocity, ResourceState.ShaderResource)
			.ReadTexture(resources.DdgiFinalGatherTrace, ResourceState.ShaderResource)
			.ReadTexture(historyRead[DdgiFinalGatherHistorySlot.Color], ResourceState.ShaderResource)
			.ReadTexture(historyRead[DdgiFinalGatherHistorySlot.Moments], ResourceState.ShaderResource)
			.ReadTexture(historyRead[DdgiFinalGatherHistorySlot.Guide], ResourceState.ShaderResource)
			.WriteTexture(resources.DdgiFinalGatherIntegrated, ResourceState.UnorderedAccess)
			.WriteTexture(moments, ResourceState.UnorderedAccess)
			.WriteTexture(guide, ResourceState.UnorderedAccess)
			.SetExecute(_temporalExecute);

		graph.AddPass("DDGI Final Gather SVGF Variance", PassKind.Compute)
			.ReadTexture(resources.DdgiFinalGatherTrace, ResourceState.ShaderResource)
			.ReadTexture(resources.DdgiFinalGatherIntegrated, ResourceState.ShaderResource)
			.ReadTexture(moments, ResourceState.ShaderResource)
			.ReadTexture(guide, ResourceState.ShaderResource)
			.WriteTexture(handles.FilterInput, ResourceState.UnorderedAccess)
			.SetExecute(_varianceExecute);

		for (var iteration = 0; iteration < resources.DdgiFinalGatherFilterIterations; iteration++)
		{
			var (source, output) = GetFilterTargets(resources, handles, iteration);
			graph.AddPass($"DDGI Final Gather SVGF Filter {iteration}", PassKind.Compute)
				.ReadTexture(resources.DdgiFinalGatherTrace, ResourceState.ShaderResource)
				.ReadTexture(guide, ResourceState.ShaderResource)
				.ReadTexture(source, ResourceState.ShaderResource)
				.WriteTexture(output, ResourceState.UnorderedAccess)
				.SetExecute(_filterExecutes[iteration]);
		}
	}

	private void Execute(RenderGraphContext context, Stage stage, int iteration)
	{
		var view = context.ViewState;
		var resources = view.FrameResources;
		var handles = view.GetPassState<ViewHandles>();
		var source = default(RenderGraphResourceHandle);
		var output = default(RenderGraphResourceHandle);
		if (stage == Stage.Variance)
		{
			output = handles.FilterInput;
		}
		else if (stage == Stage.Filter)
		{
			(source, output) = GetFilterTargets(resources, handles, iteration);
		}

		var config = BuildConfig(
			context,
			resources,
			context.Device,
			stage,
			source,
			output,
			iteration);
		Record(context, in config, context.SceneData);
	}

	/// <summary>
	/// The first a-trous level reads the variance pass's output and writes the color history the next frame
	/// accumulates into; the last writes the finished target; the ones between alternate over two intermediates.
	/// </summary>
	private static (RenderGraphResourceHandle Source, RenderGraphResourceHandle Output) GetFilterTargets(
		in RenderViewResources resources,
		ViewHandles handles,
		int iteration)
	{
		var historyWrite = resources.DdgiFinalGatherHistoryWrite
			?? throw new InvalidOperationException("DDGI final gather history was not imported.");
		var colorHistory = historyWrite[DdgiFinalGatherHistorySlot.Color];
		var lastIteration = resources.DdgiFinalGatherFilterIterations - 1;

		var source = iteration == 0
			? handles.FilterInput
			: iteration == 1
				? colorHistory
				: GetFilterIntermediate(handles, iteration - 1);
		var output = iteration == 0
			? colorHistory
			: iteration == lastIteration
				? resources.DdgiFinalGatherDenoised
				: GetFilterIntermediate(handles, iteration);

		return (source, output);
	}

	/// <summary>Level 1 writes A, level 2 writes B, level 3 writes A again, and so on.</summary>
	private static RenderGraphResourceHandle GetFilterIntermediate(ViewHandles handles, int iteration)
	{
		return iteration % 2 == 1
			? handles.FilterA
			: handles.FilterB;
	}

	/// <summary>
	/// Builds one stage's parameters. <paramref name="source"/>, <paramref name="output"/> and
	/// <paramref name="iteration"/> only apply to <see cref="Stage.Filter"/>; the variance stage writes
	/// <paramref name="output"/> too.
	/// </summary>
	private DdgiFinalGatherSvgfPassConfig BuildConfig(
		RenderGraphContext context,
		RenderViewResources resources,
		IGfxDevice device,
		Stage stage,
		RenderGraphResourceHandle source,
		RenderGraphResourceHandle output,
		int iteration)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);

		var pipeline = _kernels[(int)stage].GetPipeline(device);
		_bindlessRegistry.EnsureInitialized(device);

		var settings = resources.Config.DiffuseGlobalIllumination.FinalGather;
		var trace = context.GetTexture(resources.DdgiFinalGatherTrace);
		var historyRead = resources.DdgiFinalGatherHistoryRead
			?? throw new InvalidOperationException("DDGI final gather history was not imported.");
		var historyWrite = resources.DdgiFinalGatherHistoryWrite
			?? throw new InvalidOperationException("DDGI final gather history was not imported.");
		var isTemporal = stage == Stage.Temporal;
		var isVariance = stage == Stage.Variance;
		var isFilter = stage == Stage.Filter;

		return new DdgiFinalGatherSvgfPassConfig
		{
			Pipeline = pipeline,
			Stage = stage,
			Depth = isTemporal
				? _bindlessRegistry.RegisterDepthTexture(context.GetTexture(resources.GBufferDepth))
				: DescriptorHandle.Invalid,
			Normal = isTemporal ? ReadHandle(context, resources.GBufferNormal) : DescriptorHandle.Invalid,
			Velocity = isTemporal ? ReadHandle(context, resources.GBufferVelocity) : DescriptorHandle.Invalid,
			Trace = isTemporal ? _bindlessRegistry.GetTextureHandle(trace) : DescriptorHandle.Invalid,
			ColorHistory = isTemporal ? ReadHandle(context, historyRead[DdgiFinalGatherHistorySlot.Color]) : DescriptorHandle.Invalid,
			MomentsHistory = isTemporal ? ReadHandle(context, historyRead[DdgiFinalGatherHistorySlot.Moments]) : DescriptorHandle.Invalid,
			GuideHistory = isTemporal ? ReadHandle(context, historyRead[DdgiFinalGatherHistorySlot.Guide]) : DescriptorHandle.Invalid,
			Integrated = isTemporal
				? WriteHandle(context, resources.DdgiFinalGatherIntegrated)
				: isVariance
					? ReadHandle(context, resources.DdgiFinalGatherIntegrated)
					: DescriptorHandle.Invalid,
			Moments = isTemporal
				? WriteHandle(context, historyWrite[DdgiFinalGatherHistorySlot.Moments])
				: isVariance
					? ReadHandle(context, historyWrite[DdgiFinalGatherHistorySlot.Moments])
					: DescriptorHandle.Invalid,
			Guide = isTemporal
				? WriteHandle(context, historyWrite[DdgiFinalGatherHistorySlot.Guide])
				: ReadHandle(context, historyWrite[DdgiFinalGatherHistorySlot.Guide]),
			Source = isFilter ? ReadHandle(context, source) : DescriptorHandle.Invalid,
			Output = isTemporal ? DescriptorHandle.Invalid : WriteHandle(context, output),
			FramebufferSize = resources.SceneFramebufferSize,
			TraceSize = new(trace.Descriptor.Width, trace.Descriptor.Height),
			HistoryValid = resources.DdgiFinalGatherHistoryValid,
			TemporalAlpha = DdgiUtilities.GetFinalGatherTemporalAlpha(settings),
			StepSize = 1 << iteration
		};
	}

	private void Record(RenderGraphContext context, in DdgiFinalGatherSvgfPassConfig config, SceneDrawData sceneData)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(sceneData);

		var kernel = _kernels[(int)config.Stage];
		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);

		var bindlessWriter = kernel.Writer("BindlessHandles");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("depthHandle", config.Depth.Value);
		bindlessWriter.SetUInt("normalHandle", config.Normal.Value);
		bindlessWriter.SetUInt("velocityHandle", config.Velocity.Value);
		bindlessWriter.SetUInt("traceHandle", config.Trace.Value);
		bindlessWriter.SetUInt("colorHistoryHandle", config.ColorHistory.Value);
		bindlessWriter.SetUInt("momentsHistoryHandle", config.MomentsHistory.Value);
		bindlessWriter.SetUInt("guideHistoryHandle", config.GuideHistory.Value);
		bindlessWriter.SetUInt("integratedHandle", config.Integrated.Value);
		bindlessWriter.SetUInt("momentsHandle", config.Moments.Value);
		bindlessWriter.SetUInt("guideHandle", config.Guide.Value);
		bindlessWriter.SetUInt("sourceHandle", config.Source.Value);
		bindlessWriter.SetUInt("outputHandle", config.Output.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		if (Matrix4x4.Invert(sceneData.InverseProjection, out var projection) == false)
		{
			throw new InvalidOperationException("DDGI final gather SVGF projection parameters could not be reconstructed.");
		}

		var settingsWriter = kernel.Writer("SvgfSettings");
		settingsWriter.Clear();
		settingsWriter.SetUInt("framebufferSizeX", (uint)Math.Max(config.FramebufferSize.X, 1));
		settingsWriter.SetUInt("framebufferSizeY", (uint)Math.Max(config.FramebufferSize.Y, 1));
		settingsWriter.SetUInt("traceSizeX", (uint)Math.Max(config.TraceSize.X, 1));
		settingsWriter.SetUInt("traceSizeY", (uint)Math.Max(config.TraceSize.Y, 1));
		settingsWriter.SetFloat("projZBias", projection.M33);
		settingsWriter.SetFloat("projZScale", projection.M43);
		settingsWriter.SetUInt("historyValid", config.HistoryValid ? 1u : 0u);
		settingsWriter.SetFloat("colorAlpha", config.TemporalAlpha);
		settingsWriter.SetFloat("momentsAlpha", config.TemporalAlpha);
		settingsWriter.SetUInt("stepSize", (uint)Math.Max(config.StepSize, 1));
		commandList.SetComputeConstants(settingsWriter.RegisterIndex, settingsWriter.AsBytes());

		var (dispatchX, dispatchY, dispatchZ) = kernel.ThreadGroupSize.GetDispatchGroupCount(
			(uint)Math.Max(config.TraceSize.X, 1),
			(uint)Math.Max(config.TraceSize.Y, 1));
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}

	private DescriptorHandle ReadHandle(RenderGraphContext context, RenderGraphResourceHandle handle)
	{
		return _bindlessRegistry.GetTextureHandle(context.GetTexture(handle));
	}

	private DescriptorHandle WriteHandle(RenderGraphContext context, RenderGraphResourceHandle handle)
	{
		return _bindlessRegistry.RegisterRwTexture(context.GetTexture(handle));
	}
}
