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

	private sealed class StagePipeline
	{
		public required IGfxPipeline Pipeline { get; init; }
		public required ComputeThreadGroupSize ThreadGroupSize { get; init; }
		public required ShaderPropertyWriter BindlessWriter { get; init; }
		public required ShaderPropertyWriter SettingsWriter { get; init; }
	}

	private readonly IShaderProvider _shaderProvider;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private readonly StagePipeline?[] _stagePipelines = new StagePipeline?[3];
	private GraphicsBackendKind? _compiledBackendKind;

	public DdgiFinalGatherSvgfPass(IShaderProvider shaderProvider, BindlessResourceRegistry bindlessRegistry)
	{
		_shaderProvider = shaderProvider ?? throw new ArgumentNullException(nameof(shaderProvider));
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
	}

	/// <summary>
	/// Builds one stage's parameters. <paramref name="source"/>, <paramref name="output"/> and
	/// <paramref name="iteration"/> only apply to <see cref="Stage.Filter"/>; the variance stage writes
	/// <paramref name="output"/> too.
	/// </summary>
	public DdgiFinalGatherSvgfPassConfig BuildConfig(
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

		var stagePipeline = EnsurePipeline(device, stage);
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
			Pipeline = stagePipeline.Pipeline,
			Stage = stage,
			Depth = isTemporal
				? _bindlessRegistry.RegisterDepthTexture(context.GetTexture(resources.GBufferDepth))
				: DescriptorHandle.Invalid,
			Normal = isTemporal ? ReadHandle(context, resources.GBufferNormal) : DescriptorHandle.Invalid,
			Velocity = isTemporal ? ReadHandle(context, resources.GBufferVelocity) : DescriptorHandle.Invalid,
			Trace = isTemporal ? _bindlessRegistry.GetTextureHandle(trace) : DescriptorHandle.Invalid,
			ColorHistory = isTemporal ? ReadHandle(context, historyRead[DdgiFinalGatherHistorySlot.Color]) : DescriptorHandle.Invalid,
			MomentsHistory = isTemporal ? ReadHandle(context, historyRead[DdgiFinalGatherHistorySlot.Moments]) : DescriptorHandle.Invalid,
			DepthHistory = isTemporal ? ReadHandle(context, historyRead[DdgiFinalGatherHistorySlot.Depth]) : DescriptorHandle.Invalid,
			NormalHistory = isTemporal ? ReadHandle(context, historyRead[DdgiFinalGatherHistorySlot.Normal]) : DescriptorHandle.Invalid,
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
			GuideDepth = isTemporal
				? WriteHandle(context, historyWrite[DdgiFinalGatherHistorySlot.Depth])
				: ReadHandle(context, historyWrite[DdgiFinalGatherHistorySlot.Depth]),
			GuideNormal = isTemporal
				? WriteHandle(context, historyWrite[DdgiFinalGatherHistorySlot.Normal])
				: ReadHandle(context, historyWrite[DdgiFinalGatherHistorySlot.Normal]),
			Source = isFilter ? ReadHandle(context, source) : DescriptorHandle.Invalid,
			Output = isTemporal ? DescriptorHandle.Invalid : WriteHandle(context, output),
			FramebufferSize = resources.SceneFramebufferSize,
			TraceSize = new(trace.Descriptor.Width, trace.Descriptor.Height),
			HistoryValid = resources.DdgiFinalGatherHistoryValid,
			TemporalAlpha = DdgiUtilities.GetFinalGatherTemporalAlpha(settings),
			StepSize = 1 << iteration
		};
	}

	public void Record(RenderGraphContext context, in DdgiFinalGatherSvgfPassConfig config, SceneDrawData sceneData)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(sceneData);

		var stagePipeline = _stagePipelines[(int)config.Stage]
			?? throw new InvalidOperationException($"DDGI final gather SVGF {config.Stage} pipeline was not initialized.");
		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);

		var bindlessWriter = stagePipeline.BindlessWriter;
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("depthHandle", config.Depth.Value);
		bindlessWriter.SetUInt("normalHandle", config.Normal.Value);
		bindlessWriter.SetUInt("velocityHandle", config.Velocity.Value);
		bindlessWriter.SetUInt("traceHandle", config.Trace.Value);
		bindlessWriter.SetUInt("colorHistoryHandle", config.ColorHistory.Value);
		bindlessWriter.SetUInt("momentsHistoryHandle", config.MomentsHistory.Value);
		bindlessWriter.SetUInt("depthHistoryHandle", config.DepthHistory.Value);
		bindlessWriter.SetUInt("normalHistoryHandle", config.NormalHistory.Value);
		bindlessWriter.SetUInt("integratedHandle", config.Integrated.Value);
		bindlessWriter.SetUInt("momentsHandle", config.Moments.Value);
		bindlessWriter.SetUInt("guideDepthHandle", config.GuideDepth.Value);
		bindlessWriter.SetUInt("guideNormalHandle", config.GuideNormal.Value);
		bindlessWriter.SetUInt("sourceHandle", config.Source.Value);
		bindlessWriter.SetUInt("outputHandle", config.Output.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		if (Matrix4x4.Invert(sceneData.InverseProjection, out var projection) == false)
		{
			throw new InvalidOperationException("DDGI final gather SVGF projection parameters could not be reconstructed.");
		}

		var settingsWriter = stagePipeline.SettingsWriter;
		settingsWriter.Clear();
		settingsWriter.SetMatrix4x4("invViewProjection", sceneData.InverseViewProjection);
		settingsWriter.SetMatrix4x4("previousViewProjection", sceneData.PreviousViewProjection);
		settingsWriter.SetVector3("cameraMotion", sceneData.CameraOrigin - sceneData.PreviousCameraOrigin);
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

		var (dispatchX, dispatchY, dispatchZ) = stagePipeline.ThreadGroupSize.GetDispatchGroupCount(
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

	private StagePipeline EnsurePipeline(IGfxDevice device, Stage stage)
	{
		if (_compiledBackendKind.HasValue && _compiledBackendKind.Value != device.BackendKind)
		{
			throw new InvalidOperationException(
				$"DdgiFinalGatherSvgfPass is already compiled for backend '{_compiledBackendKind.Value}', " +
				$"but was requested for '{device.BackendKind}'.");
		}

		if (_stagePipelines[(int)stage] is StagePipeline existing)
		{
			return existing;
		}

		var entryPoint = stage switch
		{
			Stage.Temporal => "DdgiSvgfTemporalCS",
			Stage.Variance => "DdgiSvgfVarianceCS",
			_ => "DdgiSvgfFilterCS"
		};

		var compiled = _shaderProvider.GetComputeShaderWithReflection(
			EngineShaderPrograms.DdgiFinalGatherSvgf,
			entryPoint,
			device.BackendKind);
		var threadGroupSize = compiled.ThreadGroupSize;
		var key = new PipelineKey(
			PassKind.Compute,
			vertexEntryPoint: null,
			pixelEntryPoint: null,
			computeEntryPoint: entryPoint,
			renderTargets: new RenderTargetFormats(Array.Empty<TextureFormat>()),
			depthStencil: new DepthStencilFormat(TextureFormat.Unknown),
			renderState: default,
			shaderVariant: "ddgi_final_gather_svgf.compute.slang");
		var stagePipeline = new StagePipeline
		{
			Pipeline = device.GetOrCreatePipeline(
				key,
				new ShaderBytecodeSet(compute: compiled.Bytecode, computeThreadGroupSize: threadGroupSize)),
			ThreadGroupSize = threadGroupSize,
			BindlessWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("BindlessHandles")),
			SettingsWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("SvgfSettings"))
		};
		_stagePipelines[(int)stage] = stagePipeline;
		_compiledBackendKind = device.BackendKind;
		return stagePipeline;
	}
}
