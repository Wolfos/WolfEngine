using System.Numerics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// Reconstructs reduced-resolution hardware reflection samples at full resolution. Depth and
/// normal weights prevent radiance from crossing silhouettes or unrelated surfaces.
/// </summary>
public sealed class ReflectionsUpsamplePass
{
	private readonly ComputeKernel _kernel;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private readonly Action<RenderGraphContext> _reflectionExecute;
	private readonly Action<RenderGraphContext> _finalGatherExecute;

	public ReflectionsUpsamplePass(ShaderSource shaderSource, BindlessResourceRegistry bindlessRegistry)
	{
		ArgumentNullException.ThrowIfNull(shaderSource);

		_kernel = new ComputeKernel(shaderSource, EngineShaderPrograms.ReflectionsUpsample, "ReflectionsUpsampleCS");
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
		_reflectionExecute = ExecuteReflection;
		_finalGatherExecute = ExecuteFinalGather;
	}

	/// <summary>Adds the Reflections Upsample node: the reduced-resolution trace to full-resolution radiance.</summary>
	internal void AddReflectionNodes(RenderGraph graph, in RenderViewResources resources)
	{
		graph.AddPass("Reflections Upsample", PassKind.Compute)
			.ReadTexture(resources.GBufferDepth, ResourceState.ShaderResource)
			.ReadTexture(resources.GBufferNormal, ResourceState.ShaderResource)
			.ReadTexture(resources.ReflectionsTrace, ResourceState.ShaderResource)
			.WriteTexture(resources.ReflectionsRadiance, ResourceState.UnorderedAccess)
			.SetExecute(_reflectionExecute);
	}

	/// <summary>
	/// Adds the DDGI Final Gather Upsample node. The reflection upsample's depth- and normal-aware reconstruction
	/// suits any one-sample-per-trace-pixel signal, so the final gather shares its pipeline; only the handles differ.
	/// </summary>
	internal void AddFinalGatherNodes(RenderGraph graph, in RenderViewResources resources)
	{
		graph.AddPass("DDGI Final Gather Upsample", PassKind.Compute)
			.ReadTexture(resources.GBufferDepth, ResourceState.ShaderResource)
			.ReadTexture(resources.GBufferNormal, ResourceState.ShaderResource)
			.ReadTexture(resources.DdgiFinalGatherDenoised, ResourceState.ShaderResource)
			.WriteTexture(resources.DdgiFinalGatherIrradiance, ResourceState.UnorderedAccess)
			.SetExecute(_finalGatherExecute);
	}

	private void ExecuteReflection(RenderGraphContext context)
	{
		var config = BuildConfig(context, context.ViewState.FrameResources, context.Device);
		Record(context, in config, context.SceneData);
	}

	private void ExecuteFinalGather(RenderGraphContext context)
	{
		var resources = context.ViewState.FrameResources;
		var config = BuildConfig(
			context,
			resources,
			context.Device,
			resources.DdgiFinalGatherDenoised,
			resources.DdgiFinalGatherIrradiance,
			resources.Config.DiffuseGlobalIllumination.FinalGather.UpsampleSharpness);
		Record(context, in config, context.SceneData);
	}

	private ReflectionsUpsamplePassConfig BuildConfig(
		RenderGraphContext context,
		RenderViewResources resources,
		IGfxDevice device)
	{
		return BuildConfig(
			context,
			resources,
			device,
			resources.ReflectionsTrace,
			resources.ReflectionsRadiance,
			resources.Config.Reflections.RayTracedSettings.UpsampleSharpness);
	}

	/// <summary>
	/// Builds an upsample of any reduced-resolution source that follows the same contract: one sample per trace
	/// pixel, taken at the full-resolution pixel nearest that trace pixel's center. The DDGI final gather reuses
	/// it this way.
	/// </summary>
	private ReflectionsUpsamplePassConfig BuildConfig(
		RenderGraphContext context,
		RenderViewResources resources,
		IGfxDevice device,
		RenderGraphResourceHandle sourceHandle,
		RenderGraphResourceHandle outputHandle,
		float sharpness)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);

		var pipeline = _kernel.GetPipeline(device);
		_bindlessRegistry.EnsureInitialized(device);
		var source = context.GetTexture(sourceHandle);
		var output = context.GetTexture(outputHandle);
		return new ReflectionsUpsamplePassConfig
		{
			Pipeline = pipeline,
			DepthHandle = _bindlessRegistry.RegisterDepthTexture(context.GetTexture(resources.GBufferDepth)),
			NormalHandle = _bindlessRegistry.GetTextureHandle(context.GetTexture(resources.GBufferNormal)),
			SourceHandle = _bindlessRegistry.GetTextureHandle(source),
			OutputHandle = _bindlessRegistry.RegisterRwTexture(output),
			FullResolution = resources.SceneFramebufferSize,
			TraceResolution = new(source.Descriptor.Width, source.Descriptor.Height),
			Sharpness = Math.Max(sharpness, 0.001f)
		};
	}

	private void Record(
		RenderGraphContext context,
		in ReflectionsUpsamplePassConfig config,
		SceneDrawData sceneData)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(sceneData);

		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);
		var bindlessWriter = _kernel.Writer("BindlessHandles");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("depthHandle", config.DepthHandle.Value);
		bindlessWriter.SetUInt("normalHandle", config.NormalHandle.Value);
		bindlessWriter.SetUInt("sourceHandle", config.SourceHandle.Value);
		bindlessWriter.SetUInt("outputHandle", config.OutputHandle.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		if (Matrix4x4.Invert(sceneData.InverseProjection, out var projectionMatrix) == false)
		{
			throw new InvalidOperationException("Reflections upsample projection parameters could not be reconstructed.");
		}

		var settingsWriter = _kernel.Writer("UpsampleSettings");
		settingsWriter.Clear();
		settingsWriter.SetUInt("fullResolutionX", (uint)Math.Max(config.FullResolution.X, 1));
		settingsWriter.SetUInt("fullResolutionY", (uint)Math.Max(config.FullResolution.Y, 1));
		settingsWriter.SetUInt("traceResolutionX", (uint)Math.Max(config.TraceResolution.X, 1));
		settingsWriter.SetUInt("traceResolutionY", (uint)Math.Max(config.TraceResolution.Y, 1));
		settingsWriter.SetFloat("sharpness", config.Sharpness);
		settingsWriter.SetFloat("projZBias", projectionMatrix.M33);
		settingsWriter.SetFloat("projZScale", projectionMatrix.M43);
		commandList.SetComputeConstants(settingsWriter.RegisterIndex, settingsWriter.AsBytes());

		var threadGroupSize = _kernel.ThreadGroupSize;
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(
			(uint)Math.Max(config.FullResolution.X, 1),
			(uint)Math.Max(config.FullResolution.Y, 1));
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}
}
