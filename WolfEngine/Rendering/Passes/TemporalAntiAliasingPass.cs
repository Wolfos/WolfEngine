using System.Numerics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

public sealed class TemporalAntiAliasingPass
{
	private readonly ComputeKernel _kernel;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private DescriptorHandle _linearSampler = DescriptorHandle.Invalid;
	private readonly Action<RenderGraphContext> _execute;

	public TemporalAntiAliasingPass(ShaderSource shaderSource, BindlessResourceRegistry bindlessRegistry)
	{
		ArgumentNullException.ThrowIfNull(shaderSource);

		_kernel = new ComputeKernel(shaderSource, EngineShaderPrograms.TaaResolve, "TaaResolveCS");
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
		_execute = Execute;
	}

	/// <summary>Adds the TAA Resolve node: this frame's lighting, reprojected against last frame's history.</summary>
	internal void AddNodes(RenderGraph graph, in RenderViewResources resources)
	{
		graph.AddPass("TAA Resolve", PassKind.Compute)
			.ReadTexture(resources.LightingBuffer, ResourceState.ShaderResource)
			.ReadTexture(resources.GBufferVelocity, ResourceState.ShaderResource)
			.ReadTexture(resources.GBufferNormal, ResourceState.ShaderResource)
			.ReadTexture(resources.GBufferMaterial, ResourceState.ShaderResource)
			.ReadTexture(resources.GBufferDepth, ResourceState.ShaderResource)
			.ReadTexture(resources.HistoryColorRead, ResourceState.ShaderResource)
			.ReadTexture(resources.HistoryDepthRead, ResourceState.ShaderResource)
			.WriteTexture(resources.ResolvedSceneColor, ResourceState.UnorderedAccess)
			.SetExecute(_execute);
	}

	private void Execute(RenderGraphContext context)
	{
		var view = context.ViewState;
		var config = BuildConfig(
			context,
			view.FrameResources,
			context.Device,
			view.TemporalHistory.IsValid,
			view.ResetTaaHistoryThisFrame || context.SceneData.ResetHistory);
		Record(context, in config);
	}

	private TemporalAntiAliasingPassConfig BuildConfig(
		RenderGraphContext context,
		RenderViewResources resources,
		IGfxDevice device,
		bool historyValid,
		bool resetHistory)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);

		var pipeline = _kernel.GetPipeline(device);
		_bindlessRegistry.EnsureInitialized(device);
		if (_linearSampler.IsValid == false)
		{
			_linearSampler = _bindlessRegistry.GetSamplerHandle(new SamplerDescriptor(
				FilterMode.Bilinear,
				AddressMode.Clamp,
				AddressMode.Clamp,
				AddressMode.Clamp));
		}

		var currentColor = context.GetTexture(resources.LightingBuffer);
		var velocity = context.GetTexture(resources.GBufferVelocity);
		var normal = context.GetTexture(resources.GBufferNormal);
		var material = context.GetTexture(resources.GBufferMaterial);
		var currentDepth = context.GetTexture(resources.GBufferDepth);
		var historyColor = context.GetTexture(resources.HistoryColorRead);
		var historyDepth = context.GetTexture(resources.HistoryDepthRead);
		var output = context.GetTexture(resources.ResolvedSceneColor);
		var inverseUnjitteredViewProjectionValid = Matrix4x4.Invert(
			context.SceneData.UnjitteredViewProjection,
			out var inverseUnjitteredViewProjection);

		return new TemporalAntiAliasingPassConfig
		{
			Pipeline = pipeline,
			CurrentColorHandle = _bindlessRegistry.GetTextureHandle(currentColor),
			VelocityHandle = _bindlessRegistry.GetTextureHandle(velocity),
			NormalHandle = _bindlessRegistry.GetTextureHandle(normal),
			MaterialHandle = _bindlessRegistry.GetTextureHandle(material),
			CurrentDepthHandle = _bindlessRegistry.RegisterDepthTexture(currentDepth),
			HistoryColorHandle = _bindlessRegistry.GetTextureHandle(historyColor),
			HistoryDepthHandle = _bindlessRegistry.GetTextureHandle(historyDepth),
			OutputHandle = _bindlessRegistry.RegisterRwTexture(output),
			LinearSampler = _linearSampler,
			RenderSize = resources.SceneFramebufferSize,
			CurrentJitterPixels = context.SceneData.JitterPixels,
			PreviousJitterPixels = context.SceneData.PreviousJitterPixels,
			InverseUnjitteredViewProjection = inverseUnjitteredViewProjectionValid
				? inverseUnjitteredViewProjection
				: Matrix4x4.Identity,
			PreviousViewProjection = context.SceneData.PreviousViewProjection,
			CurrentProjectionZBias = context.SceneData.UnjitteredProjection.M33,
			CurrentProjectionZScale = context.SceneData.UnjitteredProjection.M43,
			Settings = resources.Config.AntiAliasing.Taa,
			HistoryValid = historyValid,
			ResetHistory = resetHistory || inverseUnjitteredViewProjectionValid == false
		};
	}

	private void Record(RenderGraphContext context, in TemporalAntiAliasingPassConfig config)
	{
		ArgumentNullException.ThrowIfNull(context);

		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);

		var bindlessWriter = _kernel.Writer("BindlessHandles");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("currentColorHandle", config.CurrentColorHandle.Value);
		bindlessWriter.SetUInt("velocityHandle", config.VelocityHandle.Value);
		bindlessWriter.SetUInt("normalHandle", config.NormalHandle.Value);
		bindlessWriter.SetUInt("materialHandle", config.MaterialHandle.Value);
		bindlessWriter.SetUInt("currentDepthHandle", config.CurrentDepthHandle.Value);
		bindlessWriter.SetUInt("historyColorHandle", config.HistoryColorHandle.Value);
		bindlessWriter.SetUInt("historyDepthHandle", config.HistoryDepthHandle.Value);
		bindlessWriter.SetUInt("outputHandle", config.OutputHandle.Value);
		bindlessWriter.SetUInt("samplerHandle", config.LinearSampler.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		var settingsWriter = _kernel.Writer("TaaSettings");
		var settings = config.Settings;
		settingsWriter.Clear();
		settingsWriter.SetUInt("renderSizeX", (uint)Math.Max(config.RenderSize.X, 1));
		settingsWriter.SetUInt("renderSizeY", (uint)Math.Max(config.RenderSize.Y, 1));
		settingsWriter.SetUInt("historyValid", config.HistoryValid ? 1u : 0u);
		settingsWriter.SetUInt("resetHistory", config.ResetHistory ? 1u : 0u);
		settingsWriter.SetVector2("currentJitterPixels", config.CurrentJitterPixels);
		settingsWriter.SetVector2("previousJitterPixels", config.PreviousJitterPixels);
		settingsWriter.SetFloat("staticHistoryWeight", Math.Clamp(settings.StaticHistoryWeight, 0.0f, 0.9999f));
		settingsWriter.SetFloat("movingHistoryWeight", Math.Clamp(settings.MovingHistoryWeight, 0.0f, 0.9999f));
		settingsWriter.SetFloat("motionResponsePixels", MathF.Max(settings.MotionResponsePixels, 0.5001f));
		settingsWriter.SetFloat("depthRejectionAbsolute", MathF.Max(settings.DepthRejectionAbsolute, 0.0f));
		settingsWriter.SetFloat("depthRejectionRelative", MathF.Max(settings.DepthRejectionRelative, 0.0f));
		settingsWriter.SetFloat("varianceClipGamma", MathF.Max(settings.VarianceClipGamma, 0.0f));
		settingsWriter.SetFloat("staticClampExpansion", MathF.Max(settings.StaticClampExpansion, 1.0f));
		settingsWriter.SetFloat(
			"clampExpansionMotionPixels",
			MathF.Max(settings.ClampExpansionMotionPixels, 1e-3f));
		settingsWriter.SetFloat("alphaTestHistoryScale", Math.Clamp(settings.AlphaTestHistoryScale, 0.0f, 1.0f));
		settingsWriter.SetFloat("currentProjectionZBias", config.CurrentProjectionZBias);
		settingsWriter.SetFloat("currentProjectionZScale", config.CurrentProjectionZScale);
		settingsWriter.SetMatrix4x4(
			"inverseUnjitteredViewProjection",
			config.InverseUnjitteredViewProjection);
		settingsWriter.SetMatrix4x4("previousViewProjection", config.PreviousViewProjection);
		commandList.SetComputeConstants(settingsWriter.RegisterIndex, settingsWriter.AsBytes());

		var threadGroupSize = _kernel.ThreadGroupSize;
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(
			(uint)Math.Max(config.RenderSize.X, 1),
			(uint)Math.Max(config.RenderSize.Y, 1));
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}
}
