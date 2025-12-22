#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Silk.NET.Direct3D12;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.Backend.D3D12;
using WolfEngine.Rendering.Backend.Metal;
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
	public RenderGraphResourceHandle SkyboxEnvironment { get; init; }
	public RenderGraphResourceHandle SkyboxIrradiance { get; init; }
	public RenderGraphResourceHandle SkyboxPrefilter { get; init; }
	public RenderGraphResourceHandle SkyboxBrdfLut { get; init; }
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
		var skyboxEnvHandle = default(RenderGraphResourceHandle);
		var skyboxIrrHandle = default(RenderGraphResourceHandle);
		var skyboxPrefilterHandle = default(RenderGraphResourceHandle);
		var skyboxBrdfHandle = default(RenderGraphResourceHandle);
		if (_skybox?.EnvironmentTexture is IGfxTexture envTexture)
		{
			skyboxEnvHandle = _resources.ImportTexture(envTexture, takeOwnership: false,
				initialState: ResourceState.ShaderResource);
			if (_skybox.IrradianceTexture is IGfxTexture irr)
			{
				skyboxIrrHandle = _resources.ImportTexture(irr, takeOwnership: false,
					initialState: ResourceState.ShaderResource);
			}
			if (_skybox.PrefilteredEnvironment is IGfxTexture prefilter)
			{
				skyboxPrefilterHandle = _resources.ImportTexture(prefilter, takeOwnership: false,
					initialState: ResourceState.ShaderResource);
			}
			if (_skybox.BrdfLut is IGfxTexture brdf)
			{
				skyboxBrdfHandle = _resources.ImportTexture(brdf, takeOwnership: false,
					initialState: ResourceState.ShaderResource);
			}
		}

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
				TextureUsage.ShaderResource | TextureUsage.UnorderedAccess)),
			SkyboxEnvironment = skyboxEnvHandle,
			SkyboxIrradiance = skyboxIrrHandle,
			SkyboxPrefilter = skyboxPrefilterHandle,
			SkyboxBrdfLut = skyboxBrdfHandle
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
		var deferredLightingBuilder = graph.AddPass("Deferred Lighting", PassKind.Compute)
			.ReadTexture(_frameResources.GBufferAlbedo, ResourceState.ShaderResource)
			.ReadTexture(_frameResources.GBufferNormal, ResourceState.ShaderResource)
			.ReadTexture(_frameResources.GBufferMaterial, ResourceState.ShaderResource)
			.ReadTexture(_frameResources.GBufferEmissive, ResourceState.ShaderResource)
			.ReadTexture(_frameResources.GBufferDepth, ResourceState.ShaderResource);
		if (_frameResources.SkyboxEnvironment.IsValid)
		{
			deferredLightingBuilder.ReadTexture(_frameResources.SkyboxEnvironment, ResourceState.ShaderResource);
		}
		if (_frameResources.SkyboxIrradiance.IsValid)
		{
			deferredLightingBuilder.ReadTexture(_frameResources.SkyboxIrradiance, ResourceState.ShaderResource);
		}
		if (_frameResources.SkyboxPrefilter.IsValid)
		{
			deferredLightingBuilder.ReadTexture(_frameResources.SkyboxPrefilter, ResourceState.ShaderResource);
		}
		if (_frameResources.SkyboxBrdfLut.IsValid)
		{
			deferredLightingBuilder.ReadTexture(_frameResources.SkyboxBrdfLut, ResourceState.ShaderResource);
		}
		deferredLightingBuilder
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
			DepthClearValue = 1.0f,
			SkyboxEnvironment = DescriptorHandle.Invalid,
			SkyboxSampler = DescriptorHandle.Invalid
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
		config.SkyboxEnvironment = skybox.EnvironmentHandle;
		config.SkyboxSampler = skybox.Sampler;
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
		var imguiRenderer = _renderer.GetImGuiRenderer();

		if (context.CommandList is D3D12CommandList commandList)
		{
			var backbuffer = context.GetTexture(_frameResources.Backbuffer) as ID3D12BackendTexture;
			var lighting = context.GetTexture(_frameResources.LightingBuffer) as ID3D12BackendTexture;
			if (backbuffer is null || lighting is null)
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

			if (_uiFrame.Commands.Length == 0)
			{
				return;
			}

			imguiRenderer.EnsureResources(_renderer.GetGfxDevice(), _uiFrame);
			imguiRenderer.Record(context, _uiFrame, backbuffer);
			return;
		}

		if (context.CommandList is MetalCommandList metalCommandList)
		{
			var backbuffer = context.GetTexture(_frameResources.Backbuffer) as MetalBackbufferTexture;
			var lighting = context.GetTexture(_frameResources.LightingBuffer) as MetalTexture;
			if (backbuffer is null || lighting is null)
			{
				return;
			}

			var source = lighting.Texture;
			var destination = backbuffer.Drawable.Texture;
			if (source.NativePtr == IntPtr.Zero || destination.NativePtr == IntPtr.Zero)
			{
				return;
			}

			var width = Math.Min(source.Width, destination.Width);
			var height = Math.Min(source.Height, destination.Height);
			metalCommandList.CopyTexture(source, destination, (uint)width, (uint)height);
			metalCommandList.SetPresentDrawable(backbuffer.Drawable);

			if (_uiFrame.Commands.Length == 0)
			{
				return;
			}

			var targets = new PassTargets(new[] { new ColorTargetBinding(backbuffer) });
			var viewport = new WolfEngine.Rendering.Abstraction.Viewport(0, 0, backbuffer.Descriptor.Width, backbuffer.Descriptor.Height);
			metalCommandList.BeginPass(targets, viewport);
			imguiRenderer.EnsureResources(_renderer.GetGfxDevice(), _uiFrame);
			imguiRenderer.Record(context, _uiFrame, backbuffer);
			metalCommandList.EndPass();
		}
	}
}
