using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

public sealed class GBufferDecalSeedPass
{
	private readonly ComputeKernel _kernel;
	private readonly BindlessResourceRegistry _bindlessRegistry;
	private readonly Action<RenderGraphContext> _execute;

	public GBufferDecalSeedPass(ShaderSource shaderSource, BindlessResourceRegistry bindlessRegistry)
	{
		ArgumentNullException.ThrowIfNull(shaderSource);

		_kernel = new ComputeKernel(shaderSource, EngineShaderPrograms.GBufferDecalSeed, "GBufferDecalSeedCS");
		_bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
		_execute = Execute;
	}

	/// <summary>
	/// Adds the GBuffer Decal Seed node, which copies the GBuffer the scene was drawn into over the targets decals
	/// are projected onto.
	/// </summary>
	internal void AddNodes(RenderGraph graph, in RenderViewResources resources)
	{
		graph.AddPass("GBuffer Decal Seed", PassKind.Compute)
			.ReadTexture(resources.DecalSourceGBufferAlbedo, ResourceState.ShaderResource)
			.ReadTexture(resources.DecalSourceGBufferNormal, ResourceState.ShaderResource)
			.ReadTexture(resources.DecalSourceGBufferMaterial, ResourceState.ShaderResource)
			.ReadTexture(resources.DecalSourceGBufferEmissive, ResourceState.ShaderResource)
			.WriteTexture(resources.GBufferAlbedo, ResourceState.UnorderedAccess)
			.WriteTexture(resources.GBufferNormal, ResourceState.UnorderedAccess)
			.WriteTexture(resources.GBufferMaterial, ResourceState.UnorderedAccess)
			.WriteTexture(resources.GBufferEmissive, ResourceState.UnorderedAccess)
			.SetExecute(_execute);
	}

	private void Execute(RenderGraphContext context)
	{
		var config = BuildConfig(context, context.ViewState.FrameResources, context.Device);
		Record(context, in config);
	}

	private GBufferDecalSeedPassConfig BuildConfig(
		RenderGraphContext context,
		RenderViewResources resources,
		IGfxDevice device)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(device);

		var pipeline = _kernel.GetPipeline(device);
		_bindlessRegistry.EnsureInitialized(device);
		var sourceAlbedo = context.GetTexture(resources.DecalSourceGBufferAlbedo);
		var sourceNormal = context.GetTexture(resources.DecalSourceGBufferNormal);
		var sourceMaterial = context.GetTexture(resources.DecalSourceGBufferMaterial);
		var sourceEmissive = context.GetTexture(resources.DecalSourceGBufferEmissive);
		var targetAlbedo = context.GetTexture(resources.GBufferAlbedo);
		var targetNormal = context.GetTexture(resources.GBufferNormal);
		var targetMaterial = context.GetTexture(resources.GBufferMaterial);
		var targetEmissive = context.GetTexture(resources.GBufferEmissive);

		return new GBufferDecalSeedPassConfig
		{
			Pipeline = pipeline,
			SourceAlbedoHandle = _bindlessRegistry.GetTextureHandle(sourceAlbedo),
			SourceNormalHandle = _bindlessRegistry.GetTextureHandle(sourceNormal),
			SourceMaterialHandle = _bindlessRegistry.GetTextureHandle(sourceMaterial),
			SourceEmissiveHandle = _bindlessRegistry.GetTextureHandle(sourceEmissive),
			TargetAlbedoHandle = _bindlessRegistry.RegisterRwTexture(targetAlbedo),
			TargetNormalHandle = _bindlessRegistry.RegisterRwTexture(targetNormal),
			TargetMaterialHandle = _bindlessRegistry.RegisterRwTexture(targetMaterial),
			TargetEmissiveHandle = _bindlessRegistry.RegisterRwTexture(targetEmissive),
			RenderSize = resources.SceneFramebufferSize
		};
	}

	private void Record(RenderGraphContext context, in GBufferDecalSeedPassConfig config)
	{
		ArgumentNullException.ThrowIfNull(context);

		var commandList = context.CommandList;
		commandList.BindPipeline(config.Pipeline);

		var bindlessWriter = _kernel.Writer("BindlessHandles");
		bindlessWriter.Clear();
		bindlessWriter.SetUInt("sourceAlbedoHandle", config.SourceAlbedoHandle.Value);
		bindlessWriter.SetUInt("sourceNormalHandle", config.SourceNormalHandle.Value);
		bindlessWriter.SetUInt("sourceMaterialHandle", config.SourceMaterialHandle.Value);
		bindlessWriter.SetUInt("sourceEmissiveHandle", config.SourceEmissiveHandle.Value);
		bindlessWriter.SetUInt("targetAlbedoHandle", config.TargetAlbedoHandle.Value);
		bindlessWriter.SetUInt("targetNormalHandle", config.TargetNormalHandle.Value);
		bindlessWriter.SetUInt("targetMaterialHandle", config.TargetMaterialHandle.Value);
		bindlessWriter.SetUInt("targetEmissiveHandle", config.TargetEmissiveHandle.Value);
		commandList.SetComputeConstants(bindlessWriter.RegisterIndex, bindlessWriter.AsBytes());

		var settingsWriter = _kernel.Writer("CopySettings");
		settingsWriter.Clear();
		settingsWriter.SetUInt("renderSizeX", (uint)Math.Max(config.RenderSize.X, 1));
		settingsWriter.SetUInt("renderSizeY", (uint)Math.Max(config.RenderSize.Y, 1));
		commandList.SetComputeConstants(settingsWriter.RegisterIndex, settingsWriter.AsBytes());

		var threadGroupSize = _kernel.ThreadGroupSize;
		var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(
			(uint)Math.Max(config.RenderSize.X, 1),
			(uint)Math.Max(config.RenderSize.Y, 1));
		commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
	}
}
