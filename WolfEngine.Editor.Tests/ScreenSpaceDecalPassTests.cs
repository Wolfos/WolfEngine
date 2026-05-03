using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.Serialization;
using WolfEngine;
using WolfEngine.ECS;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class ScreenSpaceDecalPassTests
{
	[Test]
	public void CollectDecalProjectors_AddsOnlyEnabledValidProjectors()
	{
		var snapshot = new FrameSnapshot();
		var world = new World(WorldTag.Game);
		var validTexture = CreateTexture("valid");
		var validEntity = world.CreateEntity();
		world.AddComponent(validEntity, new WorldTransform
		{
			LocalToWorld = Matrix4x4.CreateTranslation(2.0f, 0.0f, 0.0f),
			WorldToLocal = Matrix4x4.CreateTranslation(-2.0f, 0.0f, 0.0f)
		});
		world.AddComponent(validEntity, new DecalProjector
		{
			Enabled = true,
			Size = new Vector3(2.0f, 2.0f, 2.0f),
			AlbedoTexture = validTexture,
			ChannelMask = DecalChannelMask.Albedo
		});

		var disabledEntity = world.CreateEntity();
		world.AddComponent(disabledEntity, new WorldTransform { LocalToWorld = Matrix4x4.Identity });
		world.AddComponent(disabledEntity, new DecalProjector
		{
			Enabled = false,
			Size = Vector3.One,
			AlbedoTexture = validTexture,
			ChannelMask = DecalChannelMask.Albedo
		});

		var invalidEntity = world.CreateEntity();
		world.AddComponent(invalidEntity, new WorldTransform { LocalToWorld = Matrix4x4.Identity });
		world.AddComponent(invalidEntity, new DecalProjector
		{
			Enabled = true,
			Size = Vector3.One,
			ChannelMask = DecalChannelMask.Albedo
		});

		var renderGraph = CreateTestRenderGraph();
		RenderPipeline.CollectDecalProjectors(snapshot, world, renderGraph);

		Assert.That(snapshot.DecalPackets, Has.Count.EqualTo(1));
		Assert.That(snapshot.DecalPackets[0].Projector.ChannelMask, Is.EqualTo(DecalChannelMask.Albedo));
		Assert.That(snapshot.DecalPackets[0].Transform.Translation.X, Is.EqualTo(2.0f).Within(0.0001f));
		Assert.That(validTexture.ResourceRequestPending, Is.True);
	}

	[Test]
	public void DecalProjectorGpuPacker_PreservesTransformsAndHandles()
	{
		var packet = new DecalProjectorPacket(
			new DecalProjector
			{
				Enabled = true,
				Size = new Vector3(2.0f, 4.0f, 6.0f),
				UvScaleOffset = new Vector4(2.0f, 3.0f, 0.25f, 0.5f),
				Tint = new ColorRGBA(0.2f, 0.4f, 0.6f, 1.0f),
				MaterialFactors = new Vector3(0.8f, 0.5f, 0.1f),
				EmissiveIntensity = 3.0f,
				AlbedoOpacity = 0.75f,
				NormalOpacity = 0.25f,
				MaterialOpacity = 0.5f,
				EmissiveOpacity = 0.125f,
				ChannelMask = DecalChannelMask.Albedo | DecalChannelMask.Normal | DecalChannelMask.Material
			},
			Matrix4x4.CreateTranslation(10.0f, 5.0f, -2.0f));
		var data = DecalProjectorGpuPacker.CreateGpuData(
			packet,
			new Vector3(3.0f, 1.0f, 0.5f),
			new DecalProjectorResolvedHandles(
				new DescriptorHandle(DescriptorKind.ShaderResourceView, 11),
				new DescriptorHandle(DescriptorKind.ShaderResourceView, 12),
				new DescriptorHandle(DescriptorKind.ShaderResourceView, 13),
				new DescriptorHandle(DescriptorKind.ShaderResourceView, 14),
				new DescriptorHandle(DescriptorKind.Sampler, 4)));

		var identity = data.LocalToWorld * data.WorldToLocal;
		Assert.That(identity.M11, Is.EqualTo(1.0f).Within(0.0001f));
		Assert.That(identity.M22, Is.EqualTo(1.0f).Within(0.0001f));
		Assert.That(identity.M33, Is.EqualTo(1.0f).Within(0.0001f));
		Assert.That(data.LocalToWorld.Translation.X, Is.EqualTo(7.0f).Within(0.0001f));
		Assert.That(data.LocalToWorld.Translation.Y, Is.EqualTo(4.0f).Within(0.0001f));
		Assert.That(data.LocalToWorld.Translation.Z, Is.EqualTo(-2.5f).Within(0.0001f));
		Assert.That(data.AlbedoHandle, Is.EqualTo(new DescriptorHandle(DescriptorKind.ShaderResourceView, 11).Value));
		Assert.That(data.NormalHandle, Is.EqualTo(new DescriptorHandle(DescriptorKind.ShaderResourceView, 12).Value));
		Assert.That(data.MaterialHandle, Is.EqualTo(new DescriptorHandle(DescriptorKind.ShaderResourceView, 13).Value));
		Assert.That(data.SamplerHandle, Is.EqualTo(new DescriptorHandle(DescriptorKind.Sampler, 4).Value));
	}

	[Test]
	public void RenderGraphFrameBuilder_Build_InsertsScreenSpaceDecalBetweenGBufferAndReaders()
	{
		var registry = new RenderGraphResourceRegistry();
		var textureDescriptor = new TextureDescriptor(
			16,
			16,
			TextureFormat.Rgba8Unorm,
			TextureUsage.RenderTarget | TextureUsage.ShaderResource);
		var depthDescriptor = new TextureDescriptor(
			16,
			16,
			TextureFormat.D32Float,
			TextureUsage.DepthStencil | TextureUsage.ShaderResource);

		var frameResources = new RenderGraphFrameResources
		{
			SceneEnabled = true,
			SceneFramebufferSize = new Int2(16, 16),
			GBufferAlbedo = registry.CreateTransientTexture(textureDescriptor),
			GBufferNormal = registry.CreateTransientTexture(textureDescriptor),
			GBufferMaterial = registry.CreateTransientTexture(textureDescriptor),
			GBufferEmissive = registry.CreateTransientTexture(textureDescriptor),
			DecalSourceGBufferAlbedo = registry.CreateTransientTexture(textureDescriptor),
			DecalSourceGBufferNormal = registry.CreateTransientTexture(textureDescriptor),
			DecalSourceGBufferMaterial = registry.CreateTransientTexture(textureDescriptor),
			DecalSourceGBufferEmissive = registry.CreateTransientTexture(textureDescriptor),
			GBufferDepth = registry.CreateTransientTexture(depthDescriptor),
			GBufferVelocity = registry.CreateTransientTexture(textureDescriptor),
			AmbientOcclusionRaw = registry.CreateTransientTexture(textureDescriptor),
			AmbientOcclusionTemp = registry.CreateTransientTexture(textureDescriptor),
			AmbientOcclusionFinal = registry.CreateTransientTexture(textureDescriptor),
			ShadowMapDepth0 = registry.CreateTransientTexture(depthDescriptor),
			ShadowMapDepth1 = registry.CreateTransientTexture(depthDescriptor),
			ShadowMapDepth2 = registry.CreateTransientTexture(depthDescriptor),
			LightingBuffer = registry.CreateTransientTexture(textureDescriptor),
			ResolvedSceneColor = registry.CreateTransientTexture(textureDescriptor),
			TonemappedLinearSceneColor = registry.CreateTransientTexture(textureDescriptor),
			TonemappedSceneColor = registry.CreateTransientTexture(textureDescriptor),
			FinalColor = registry.CreateTransientTexture(textureDescriptor),
			Config = new RenderConfig
			{
				Decals = new DecalConfig { Enabled = true },
				VBAOConfig = new VBAOPass.Config { Enabled = true }
			}
		};

		var builder = (RenderGraphFrameBuilder)FormatterServices.GetUninitializedObject(typeof(RenderGraphFrameBuilder));
		SetField(builder, "_frameResources", frameResources);
		foreach (var field in typeof(RenderGraphFrameBuilder).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
		{
			if (field.FieldType == typeof(Action<RenderGraphContext>))
			{
				field.SetValue(builder, (Action<RenderGraphContext>)(_ => { }));
			}
		}
		SetField(builder, "_requestedSceneDebugViewId", SceneDebugViewIds.FinalColor);
		InitializePrivateField(builder, "_sceneDebugViews");
		InitializePrivateField(builder, "_sceneDebugViewOptions");

		var renderGraph = (RenderGraph)FormatterServices.GetUninitializedObject(typeof(RenderGraph));
		SetField(renderGraph, "_resourceRegistry", registry);
		SetField(renderGraph, "_passes", new List<RenderGraphPass>());
		SetField(renderGraph, "_passPool", new Queue<RenderGraphPass>());

		builder.Build(renderGraph);

		var passes = (List<RenderGraphPass>)GetField(renderGraph, "_passes");
		var passNames = passes.Select(pass => pass.Name).ToArray();
		var gbufferIndex = Array.IndexOf(passNames, "GBuffer");
		var decalIndex = Array.IndexOf(passNames, "ScreenSpaceDecal");
		var aoIndex = Array.IndexOf(passNames, "VBAO Evaluate");
		var deferredIndex = Array.IndexOf(passNames, "Deferred Lighting");

		Assert.That(gbufferIndex, Is.GreaterThanOrEqualTo(0));
		Assert.That(decalIndex, Is.GreaterThan(gbufferIndex));
		Assert.That(aoIndex, Is.GreaterThan(decalIndex));
		Assert.That(deferredIndex, Is.GreaterThan(decalIndex));
	}

	private static Texture CreateTexture(string name)
	{
		return new Texture(
			name,
			1,
			1,
			true,
			TextureFormat.Rgba8Unorm,
			[new TextureMipData(1, 1, [255, 255, 255, 255])]);
	}

	private static RenderGraph CreateTestRenderGraph()
	{
		var renderGraph = (RenderGraph)FormatterServices.GetUninitializedObject(typeof(RenderGraph));
		SetField(renderGraph, "_resourceSync", new object());
		SetField(renderGraph, "_pendingTextures", new HashSet<Texture>());
		SetField(renderGraph, "_ensureMeshQueue", new ConcurrentQueue<Mesh>());
		return renderGraph;
	}

	private static void SetField(object instance, string fieldName, object value)
	{
		var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
		            ?? throw new AssertionException($"Field '{fieldName}' was not found.");
		field.SetValue(instance, value);
	}

	private static object GetField(object instance, string fieldName)
	{
		var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
		            ?? throw new AssertionException($"Field '{fieldName}' was not found.");
		return field.GetValue(instance)!;
	}

	private static void InitializePrivateField(object instance, string fieldName)
	{
		var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
		            ?? throw new AssertionException($"Field '{fieldName}' was not found.");
		if (field.GetValue(instance) is not null)
		{
			return;
		}

		if (field.FieldType.IsArray)
		{
			field.SetValue(instance, Array.CreateInstance(field.FieldType.GetElementType()!, 0));
			return;
		}

		field.SetValue(instance, Activator.CreateInstance(field.FieldType));
	}
}
