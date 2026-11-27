using System.Numerics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

public sealed class AmbientOcclusionUpsamplePass
{
	private readonly ComputeKernel _kernel;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private readonly Action<RenderGraphContext> _execute;

	public AmbientOcclusionUpsamplePass(ShaderSource shaderSource, BindlessResourceRegistry bindlessRegistry)
	{
		ArgumentNullException.ThrowIfNull(shaderSource);

		_kernel = new ComputeKernel(shaderSource, EngineShaderPrograms.AmbientOcclusionUpsample, "AmbientOcclusionUpsampleCS");
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
		_execute = Execute;
	}

	/// <summary>Adds the Ambient Occlusion Upsample node: half-resolution occlusion to the final, full-resolution target.</summary>
	internal void AddNodes(RenderGraph graph, in RenderViewResources resources)
	{
		graph.AddPass("Ambient Occlusion Upsample", PassKind.Compute)
			.ReadTexture(resources.GBufferDepth, ResourceState.ShaderResource)
			.ReadTexture(resources.GBufferNormal, ResourceState.ShaderResource)
			.ReadTexture(resources.AmbientOcclusionRaw, ResourceState.ShaderResource)
			.WriteTexture(resources.AmbientOcclusionFinal, ResourceState.UnorderedAccess)
			.SetExecute(_execute);
	}

	private void Execute(RenderGraphContext context)
	{
		var config = BuildConfig(context, context.ViewState.FrameResources, context.Device);
		Record(context, in config, context.SceneData);
	}

	private AmbientOcclusionUpsamplePassConfig BuildConfig(
		RenderGraphContext context,
		RenderViewResources resources,
		IGfxDevice device)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);

		var pipeline = _kernel.GetPipeline(device);
		_bindlessRegistry.EnsureInitialized(device);

		var depth = context.GetTexture(resources.GBufferDepth);
		var normal = context.GetTexture(resources.GBufferNormal);
		var source = context.GetTexture(resources.AmbientOcclusionRaw);
		var destination = context.GetTexture(resources.AmbientOcclusionFinal);
		var depthHandle = _bindlessRegistry.RegisterDepthTexture(depth);
		var normalHandle = _bindlessRegistry.GetTextureHandle(normal);
		var sourceHandle = _bindlessRegistry.GetTextureHandle(source);
		var outputHandle = _bindlessRegistry.RegisterRwTexture(destination);

		return new AmbientOcclusionUpsamplePassConfig
		{
			Pipeline = pipeline,
			DepthHandle = depthHandle,
			NormalHandle = normalHandle,
			SourceHandle = sourceHandle,
			OutputHandle = outputHandle,
			FullResolution = resources.SceneFramebufferSize,
			AoResolution = new(source.Descriptor.Width, source.Descriptor.Height),
			BlurSharpness = Math.Max(resources.Config.AmbientOcclusion.BlurSharpness, 0.001f)
		};
	}

	private void Record(RenderGraphContext context, in AmbientOcclusionUpsamplePassConfig config, SceneDrawData sceneData)
	{
		ArgumentNullException.ThrowIfNull(context);

		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);

		var bindlessWriter = _kernel.Writer("BindlessHandles");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("depthHandle", config.DepthHandle.Value);
		bindlessWriter.SetUInt("normalHandle", config.NormalHandle.Value);
		bindlessWriter.SetUInt("sourceHandle", config.SourceHandle.Value);
		bindlessWriter.SetUInt("outputHandle", config.OutputHandle.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		var settingsWriter = _kernel.Writer("UpsampleSettings");
		if (Matrix4x4.Invert(sceneData.InverseProjection, out var projectionMatrix) == false)
		{
			throw new InvalidOperationException("Ambient occlusion upsample projection parameters could not be reconstructed.");
		}

		settingsWriter.Clear();
		settingsWriter.SetUInt("fullResolutionX", (uint)Math.Max(config.FullResolution.X, 1));
		settingsWriter.SetUInt("fullResolutionY", (uint)Math.Max(config.FullResolution.Y, 1));
		settingsWriter.SetUInt("aoResolutionX", (uint)Math.Max(config.AoResolution.X, 1));
		settingsWriter.SetUInt("aoResolutionY", (uint)Math.Max(config.AoResolution.Y, 1));
		settingsWriter.SetFloat("blurSharpness", config.BlurSharpness);
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
