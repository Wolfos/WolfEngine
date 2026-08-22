using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Moq;
using WolfEngine.ECS;
using WolfEngine.Mathematics;
using WolfEngine.Profiling;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.Shaders;
using WolfEngine.Rendering.UI;
using WolfEngine.Utility;

namespace WolfEngine.Tests;

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
		var (renderGraph, frameBuilder) = CreateSchedulingFixture(registry);
		var config = new RenderConfig
		{
			Decals = new DecalConfig { Enabled = true },
			AmbientOcclusion = new AmbientOcclusionConfig { Enabled = true },
			Reflections = new ReflectionConfig { Enabled = false },
			Fsr3 = new Fsr3UpscalerConfig { Enabled = false },
			Bloom = new BloomConfig { Enabled = false }
		};
		frameBuilder.BeginFrame(
			new Int2(16, 16),
			new Int2(16, 16),
			default,
			sceneEnabled: true,
			hasActiveDecals: true,
			Vector3.UnitY,
			1.0f,
			config,
			Vector3.Zero);
		frameBuilder.Build(renderGraph);

		var passes = renderGraph.Passes;
		var passNames = passes.Select(pass => pass.Name).ToArray();
		var gbufferIndex = Array.IndexOf(passNames, "GBuffer");
		var seedIndex = Array.IndexOf(passNames, "GBuffer Decal Seed");
		var decalIndex = Array.IndexOf(passNames, "ScreenSpaceDecal");
		var aoIndex = Array.IndexOf(passNames, "Ambient Occlusion Evaluate");
		var deferredIndex = Array.IndexOf(passNames, "Deferred Lighting");
		var screenSpaceDecal = passes.Single(pass => pass.Name == "ScreenSpaceDecal");
		var ambientOcclusion = passes.Single(pass => pass.Name == "Ambient Occlusion Evaluate");
		var deferredLighting = passes.Single(pass => pass.Name == "Deferred Lighting");

		Assert.That(gbufferIndex, Is.GreaterThanOrEqualTo(0));
		Assert.That(seedIndex, Is.GreaterThan(gbufferIndex));
		Assert.That(decalIndex, Is.GreaterThan(seedIndex));
		Assert.That(aoIndex, Is.GreaterThan(decalIndex));
		Assert.That(deferredIndex, Is.GreaterThan(decalIndex));
		Assert.That(screenSpaceDecal.Writes, Has.Count.EqualTo(4));
		Assert.That(screenSpaceDecal.Writes.All(deferredLighting.Reads.Contains), Is.True);
		Assert.That(screenSpaceDecal.Writes.Intersect(ambientOcclusion.Reads).Count(), Is.EqualTo(1));
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
		var renderGraph = (RenderGraph)RuntimeHelpers.GetUninitializedObject(typeof(RenderGraph));
		SetField(renderGraph, "_resourceSync", new object());
		SetField(renderGraph, "_pendingTextures", new HashSet<Texture>());
		SetField(renderGraph, "_ensureMeshQueue", new ConcurrentQueue<Mesh>());
		return renderGraph;
	}

	private static (RenderGraph Graph, RenderGraphFrameBuilder FrameBuilder) CreateSchedulingFixture(
		RenderGraphResourceRegistry registry)
	{
		var texture = new Mock<IGfxTexture>();
		texture.SetupGet(value => value.Descriptor).Returns(new TextureDescriptor(
			1,
			1,
			TextureFormat.Rgba16Float,
			TextureUsage.ShaderResource | TextureUsage.UnorderedAccess));
		var device = new Mock<IGfxDevice>();
		device.Setup(value => value.CreateTexture(in It.Ref<TextureDescriptor>.IsAny)).Returns(texture.Object);
		var renderer = new Mock<IRenderer>();
		renderer.Setup(value => value.GetGfxDevice()).Returns(device.Object);
		var shaderProvider = new Mock<IShaderProvider>();
		var gpuDrawBackendBridge = new Mock<IGpuDrawBackendBridge>();
		var bindlessRegistry = new BindlessResourceRegistry();
		var gpuDrawResources = new GpuDrawResources(shaderProvider.Object);
		var hardeningStats = new GpuDrawHardeningStats();
		var gameplayUiRenderer = new GameplayUiGpuRenderer(shaderProvider.Object, bindlessRegistry);

		var graph = new RenderGraph(
			registry,
			renderer.Object,
			new ArenaAllocator(),
			gpuDrawResources,
			hardeningStats,
			new GpuProfiler(),
			Mock.Of<IUiFrameProvider>(),
			NullGameplayUiFrameProvider.Instance,
			new EditorViewportStateBus(),
			new EditorFrameCoordinator(),
			new RenderFrameCoordinator(),
			new MainThreadDispatcher(),
			NullImGuiRenderer.Instance,
			gameplayUiRenderer,
			shaderProvider.Object,
			bindlessRegistry,
			gpuDrawBackendBridge.Object);
		var passSet = new RenderGraphPassSet(
			renderer.Object,
			shaderProvider.Object,
			bindlessRegistry,
			gpuDrawResources,
			hardeningStats,
			gpuDrawBackendBridge.Object);
		var frameBuilder = new RenderGraphFrameBuilder(
			registry,
			renderer.Object,
			passSet,
			gpuDrawResources,
			NullImGuiRenderer.Instance,
			gameplayUiRenderer,
			shaderProvider.Object);

		return (graph, frameBuilder);
	}

	private static void SetField(object instance, string fieldName, object value)
	{
		var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
		            ?? throw new AssertionException($"Field '{fieldName}' was not found.");
		field.SetValue(instance, value);
	}

}
