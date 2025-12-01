#nullable enable

using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Silk.NET.Direct3D12;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.Backend.D3D12;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Rendering;

public readonly struct RenderGraphFrameResources
{
	public Int2 FramebufferSize { get; init; }
	public RenderGraphResourceHandle Backbuffer { get; init; }
	public RenderGraphResourceHandle GBufferAlbedo { get; init; }
	public RenderGraphResourceHandle GBufferNormal { get; init; }
	public RenderGraphResourceHandle GBufferMaterial { get; init; }
	public RenderGraphResourceHandle GBufferEmissive { get; init; }
	public RenderGraphResourceHandle GBufferDepth { get; init; }
	public RenderGraphResourceHandle LightingBuffer { get; init; }
}

public sealed class RenderGraphFrameBuilder
{
	private readonly RenderGraphResourceRegistry _resources;
	private readonly IRenderer _renderer;
	private readonly DeferredLightingPass _deferredLightingPass;
	private SkyboxResources? _skybox;
	private RenderGraphFrameResources _frameResources;
	private UiFrameData _uiFrame = UiFrameData.Empty;
	
	private readonly Action<RenderGraphContext> _gbufferExecute;
	private readonly Action<RenderGraphContext> _deferredLightingExecute;
	private readonly Action<RenderGraphContext> _imguiExecute;

	
	public RenderGraphFrameBuilder(RenderGraphResourceRegistry resources, IRenderer renderer,
		DeferredLightingPass deferredLightingPass)
	{
		_resources = resources;
		_renderer = renderer;
		_deferredLightingPass = deferredLightingPass;
		
		_gbufferExecute = ExecuteGBuffer;
		_deferredLightingExecute = ExecuteDeferredLighting;
		_imguiExecute = ExecuteImGui;
	}

	public void SetSkybox(SkyboxResources skybox)
	{
		_skybox = skybox;
	}

	public RenderGraphFrameResources BeginFrame(
		Int2 framebufferSize,
		RenderGraphResourceHandle backBuffer)
	{
		_frameResources = new()
		{
			FramebufferSize = framebufferSize,
			Backbuffer = backBuffer,
			GBufferAlbedo = _resources.CreateTransientTexture(new TextureDescriptor(framebufferSize.X,
				framebufferSize.Y, TextureFormat.Bgra8Unorm, TextureUsage.RenderTarget | TextureUsage.ShaderResource,
				new Vector4(0.392f, 0.584f, 0.929f, 1.0f))),
			GBufferNormal = _resources.CreateTransientTexture(new TextureDescriptor(framebufferSize.X,
				framebufferSize.Y, TextureFormat.Rgba16Float, TextureUsage.RenderTarget | TextureUsage.ShaderResource,
				new Vector4(0.5f, 0.5f, 1.0f, 1.0f))),
			GBufferMaterial = _resources.CreateTransientTexture(new TextureDescriptor(framebufferSize.X,
				framebufferSize.Y, TextureFormat.Rgba8Unorm, TextureUsage.RenderTarget | TextureUsage.ShaderResource,
				new Vector4(0.0f, 0.0f, 0.0f, 1.0f))),
			GBufferEmissive = _resources.CreateTransientTexture(new TextureDescriptor(framebufferSize.X,
				framebufferSize.Y, TextureFormat.Rgba8Unorm, TextureUsage.RenderTarget | TextureUsage.ShaderResource,
				new Vector4(0.0f, 0.0f, 0.0f, 1.0f))),
			GBufferDepth = _resources.CreateTransientTexture(new TextureDescriptor(framebufferSize.X, framebufferSize.Y,
				TextureFormat.D32Float, TextureUsage.DepthStencil | TextureUsage.ShaderResource, Vector4.Zero, 1.0f)),
			LightingBuffer = _resources.CreateTransientTexture(new TextureDescriptor(
				framebufferSize.X,
				framebufferSize.Y,
				TextureFormat.Bgra8Unorm,
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess))
		};

		return _frameResources;
	}

	public void SetUiFrame(UiFrameData uiFrame)
	{
		_uiFrame = uiFrame;
	}
	

	[SuppressMessage("ReSharper", "RedundantArgumentDefaultValue")]
	public bool Build(RenderGraph graph)
	{
		// Register GBuffer pass with proper resource states
		graph.AddPass("GBuffer", PassKind.Graphics)
			.WriteTexture(_frameResources.GBufferAlbedo, ResourceState.RenderTarget)
			.WriteTexture(_frameResources.GBufferNormal, ResourceState.RenderTarget)
			.WriteTexture(_frameResources.GBufferMaterial, ResourceState.RenderTarget)
			.WriteTexture(_frameResources.GBufferEmissive, ResourceState.RenderTarget)
			.WriteTexture(_frameResources.GBufferDepth, ResourceState.DepthWrite)
			.SetExecute(_gbufferExecute);

		// Register Deferred Lighting pass with proper resource states
		graph.AddPass("Deferred Lighting", PassKind.Compute)
			.ReadTexture(_frameResources.GBufferAlbedo, ResourceState.ShaderResource)
			.ReadTexture(_frameResources.GBufferNormal, ResourceState.ShaderResource)
			.ReadTexture(_frameResources.GBufferMaterial, ResourceState.ShaderResource)
			.ReadTexture(_frameResources.GBufferEmissive, ResourceState.ShaderResource)
			.ReadTexture(_frameResources.GBufferDepth, ResourceState.ShaderResource)
			.WriteTexture(_frameResources.LightingBuffer, ResourceState.UnorderedAccess)
			.SetExecute(_deferredLightingExecute);

		graph.AddPass("ImGui", PassKind.Graphics)
			.ReadTexture(_frameResources.LightingBuffer, ResourceState.CopySource)
			.WriteTexture(_frameResources.Backbuffer, ResourceState.RenderTarget)
			.SetExecute(_imguiExecute);

		return true;
	}

	private void ExecuteGBuffer(RenderGraphContext context)
	{
		var albedoTexture = context.GetTexture(_frameResources.GBufferAlbedo);
		var normalTexture = context.GetTexture(_frameResources.GBufferNormal);
		var materialTexture = context.GetTexture(_frameResources.GBufferMaterial);
		var emissiveTexture = context.GetTexture(_frameResources.GBufferEmissive);
		var depthTexture = context.GetTexture(_frameResources.GBufferDepth);

		var gbufferConfig = new GBufferPassConfig
		{
			FramebufferWidth = _frameResources.FramebufferSize.X,
			FramebufferHeight = _frameResources.FramebufferSize.Y,
			AlbedoTarget = albedoTexture,
			NormalTarget = normalTexture,
			MaterialTarget = materialTexture,
			EmissiveTarget = emissiveTexture,
			DepthTarget = depthTexture,
			AlbedoClearColor = new(0.392f, 0.584f, 0.929f, 1.0f),
			EmissiveClearColor = new(0.0f, 0.0f, 0.0f, 1.0f),
			NormalClearColor = new(0.5f, 0.5f, 1.0f, 1.0f),
			MaterialClearColor = new(0.0f, 0.0f, 0.0f, 1.0f),
			DepthClearValue = 1.0f
		};

		if (_skybox is not null &&
		    _skybox.Mesh is not null &&
		    context.SceneData is not null)
		{
			ApplySkybox(ref gbufferConfig, _skybox, context.SceneData.ViewProjection);
		}

		GBufferPass.Record(context, gbufferConfig, context.SceneData!);
	}

	private static void ApplySkybox(ref GBufferPassConfig config, SkyboxResources skybox, Matrix4x4 viewProj)
	{
		config.SkyboxPipeline = skybox.Pipeline;
		config.SkyboxDescriptorSet = skybox.DescriptorSet;
		config.InvViewProjection = viewProj;
		config.SkyboxMesh = skybox.Mesh;
	}
	
	private void ExecuteDeferredLighting(RenderGraphContext context)
	{
		var config = _deferredLightingPass.BuildConfig(context, _frameResources, _renderer.GetGfxDevice());
		_deferredLightingPass.Record(context, ref config, context.SceneData!);
	}

	private unsafe void ExecuteImGui(RenderGraphContext context)
	{
		if (_uiFrame.Commands.Length == 0)
		{
			return;
		}

		var backbuffer = context.GetTexture(_frameResources.Backbuffer) as ID3D12BackendTexture;
		var lighting = context.GetTexture(_frameResources.LightingBuffer) as ID3D12BackendTexture;
		var commandList = context.CommandList as D3D12CommandList;
		if (backbuffer is null || lighting is null || commandList is null)
		{
			return;
		}

		var native = commandList.NativeCommandList;

		ResourceBarrier barrier = new() {Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None};
		barrier.Anonymous.Transition = new()
		{
			PResource = backbuffer.Resource,
			Subresource = D3D12.ResourceBarrierAllSubresources,
			StateBefore = ResourceStates.RenderTarget,
			StateAfter = ResourceStates.CopyDest
		};
		native->ResourceBarrier(1, &barrier);

		native->CopyResource(backbuffer.Resource, lighting.Resource);

		barrier.Anonymous.Transition.StateBefore = ResourceStates.CopyDest;
		barrier.Anonymous.Transition.StateAfter = ResourceStates.RenderTarget;
		native->ResourceBarrier(1, &barrier);

		var imguiRenderer = _renderer.GetImGuiRenderer();
		imguiRenderer.EnsureResources(_renderer.GetGfxDevice(), _uiFrame);
		imguiRenderer.Record(context, _uiFrame, backbuffer);
	}
}
