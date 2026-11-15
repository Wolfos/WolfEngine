using System.Numerics;
using System.Runtime.CompilerServices;
using Moq;
using WolfEngine.ECS;
using WolfEngine.Logging;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Features;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class RenderFeatureTests
{
	[Test]
	public void RegistryTracksFeaturesPerWorldAndRetiresThemOnce()
	{
		var registry = new RenderFeatureRegistry();
		var world = new World(WorldTag.Game);
		var other = new World(WorldTag.Game);
		var first = new CountingFeature();
		var second = new CountingFeature();
		var elsewhere = new CountingFeature();
		registry.Register(world, first);
		registry.Register(world, second);
		registry.Register(other, elsewhere);

		Assert.That(registry.GetFeatures(world), Is.EqualTo(new[] { first, second }));
		Assert.That(() => registry.Register(other, first), Throws.InvalidOperationException);

		Assert.That(registry.Unregister(first), Is.True);
		Assert.That(registry.Unregister(first), Is.False);
		registry.UnregisterWorld(world);

		Assert.Multiple(() =>
		{
			Assert.That(registry.GetFeatures(world), Is.Empty);
			Assert.That(registry.GetFeatures(other), Is.EqualTo(new[] { elsewhere }));
			Assert.That(first.IsRetired && second.IsRetired, Is.True);
			Assert.That(elsewhere.IsRetired, Is.False);
			Assert.That(TakeRetired(registry), Is.EqualTo(new RenderFeature[] { first, second }));
			Assert.That(() => registry.Register(world, first), Throws.InvalidOperationException,
				"a retired feature's GPU resources are already being released");
		});
	}

	[Test]
	public void ExtractReusesEachFeaturesFrameObjectAcrossWrites()
	{
		var registry = new RenderFeatureRegistry();
		var world = new World(WorldTag.Game);
		var feature = new CountingFeature();
		registry.Register(world, feature);
		var snapshot = new FrameSnapshot().GetOrCreateView(RenderViewId.Primary);

		snapshot.ExtractRenderFeatures(world, registry);
		var firstFrame = snapshot.RenderFeatureFrames.Single().Data;
		snapshot.Clear();
		snapshot.ExtractRenderFeatures(world, registry);

		Assert.That(snapshot.RenderFeatureFrames.Single().Data, Is.SameAs(firstFrame));
		Assert.That(((CountingFrame)firstFrame).Extracted, Is.EqualTo(2));
	}

	[Test]
	public void ClearingSnapshotsReleasesUnregisteredFeatures()
	{
		// The editor unloads the gameplay assembly after play. A snapshot that kept an unregistered feature or
		// its frame object would keep that assembly loaded.
		var registry = new RenderFeatureRegistry();
		var world = new World(WorldTag.Game);
		var snapshot = new FrameSnapshot().GetOrCreateView(RenderViewId.Primary);
		var reference = RegisterExtractAndUnregister(registry, world, snapshot);
		while (registry.TryTakeRetired(out _))
		{
		}

		snapshot.Clear();
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();

		Assert.That(reference.IsAlive, Is.False);
	}

	[Test]
	public void FeatureThatThrowsInExtractIsReportedOnceAndSkipped()
	{
		var log = new Mock<ILogService>();
		var registry = new RenderFeatureRegistry(log.Object);
		var world = new World(WorldTag.Game);
		var feature = new CountingFeature { ThrowOnExtract = true };
		registry.Register(world, feature);
		var snapshot = new FrameSnapshot().GetOrCreateView(RenderViewId.Primary);

		snapshot.ExtractRenderFeatures(world, registry);
		snapshot.Clear();
		snapshot.ExtractRenderFeatures(world, registry);

		Assert.That(snapshot.RenderFeatureFrames, Is.Empty);
		log.Verify(value => value.Error(It.Is<string>(message => message.Contains(nameof(CountingFeature))), It.IsAny<Exception>()),
			Times.Once);
	}

	[Test]
	public void FeaturePassesRunAfterTransparentsAndBeforeTheTemporalResolve()
	{
		var registry = new RenderFeatureRegistry();
		var resources = new RenderGraphResourceRegistry();
		var (graph, builder) = ScreenSpaceDecalPassTests.CreateSchedulingFixture(resources);
		var feature = new DarkenFeature(CreateShader("DarkenCS"));
		registry.Register(new World(WorldTag.Game), feature);

		BeginFrame(builder, registry, feature);
		builder.Build(graph);

		var names = graph.Passes.Select(pass => pass.Name).ToList();
		var upload = graph.Passes.Single(pass => pass.Name == "DarkenFeature: Upload");
		var darken = graph.Passes.Single(pass => pass.Name == "DarkenFeature: Darken");
		var lighting = Resources(builder).LightingBuffer;
		Assert.Multiple(() =>
		{
			Assert.That(names.IndexOf("Transparent Forward"), Is.LessThan(names.IndexOf(upload.Name)));
			Assert.That(names.IndexOf(upload.Name), Is.LessThan(names.IndexOf(darken.Name)));
			Assert.That(names.IndexOf(darken.Name), Is.LessThan(names.IndexOf("TAA Resolve")));
			Assert.That(darken.Reads, Does.Contain(upload.Writes.Single()), "the kernel reads the uploaded mask");
			Assert.That(darken.Reads, Does.Contain(Resources(builder).GBufferDepth));
			Assert.That(darken.Writes, Is.EqualTo(new[] { lighting }));
			Assert.That(graph.Passes.Single(pass => pass.Name == "TAA Resolve").Reads, Does.Contain(lighting));
			Assert.That(feature.Initialized, Is.EqualTo(1));
		});
		Assert.DoesNotThrow(builder.CompleteFrame);
		builder.CompleteRenderFeatures();
	}

	[Test]
	public void FeatureThatFailsWhileRecordingIsDisabledWithoutBreakingTheFrame()
	{
		var log = new Mock<ILogService>();
		var registry = new RenderFeatureRegistry(log.Object);
		var (graph, builder) = ScreenSpaceDecalPassTests.CreateSchedulingFixture(new RenderGraphResourceRegistry());
		var feature = new DarkenFeature(CreateShader("DarkenCS")) { SkipDispatch = true };
		registry.Register(new World(WorldTag.Game), feature);

		BeginFrame(builder, registry, feature);
		builder.Build(graph);

		Assert.That(feature.IsFaulted, Is.True, "a pass that was never dispatched is a bug in the feature");
		log.Verify(value => value.Error(It.IsAny<string>(),
			It.Is<Exception>(exception => exception.Message.Contains("never dispatched"))), Times.Once);
		Assert.That(graph.Passes.Select(pass => pass.Name), Does.Contain("TAA Resolve"));
	}

	[Test]
	public void ComputePassReportsConstantsTheKernelDoesNotDeclare()
	{
		var registry = new RenderFeatureRegistry(Mock.Of<ILogService>());
		var (graph, builder) = ScreenSpaceDecalPassTests.CreateSchedulingFixture(new RenderGraphResourceRegistry());
		var feature = new DarkenFeature(CreateShader("DarkenCS")) { ConstantName = "strenght" };
		registry.Register(new World(WorldTag.Game), feature);

		BeginFrame(builder, registry, feature);
		builder.Build(graph);

		Assert.That(feature.IsFaulted, Is.True);
		Assert.That(feature.LastError, Does.Contain("strength"), "the message lists the constants the kernel has");
	}

	[Test]
	public void ReleasingResourcesRetiresTexturesThroughTheDevice()
	{
		var device = new Mock<IGfxDevice>();
		var texture = new Mock<IGfxTexture>();
		texture.As<IDisposable>();
		texture.SetupGet(value => value.Descriptor).Returns(new TextureDescriptor(4, 4, TextureFormat.R16Unorm, TextureUsage.ShaderResource));
		device.Setup(value => value.CreateTexture(in It.Ref<TextureDescriptor>.IsAny)).Returns(texture.Object);
		var resources = new RenderFeatureResources(device.Object, "Fog");

		var created = resources.CreateTexture("Mask", 4, 4, TextureFormat.R16Unorm);
		resources.Release();

		Assert.That(created.Size, Is.EqualTo(new Int2(4, 4)));
		device.Verify(value => value.Retire(It.IsAny<Action>(), It.IsAny<string?>()), Times.Once);
		Assert.That(() => resources.CreateTexture("Bad", 4, 4, TextureFormat.Bc1Unorm), Throws.ArgumentException);
	}

	private static void BeginFrame(RenderGraphFrameBuilder builder, RenderFeatureRegistry registry, RenderFeature feature)
	{
		var config = new RenderConfig
		{
			AntiAliasing = new AntiAliasingConfig { Mode = AntiAliasingMode.Taa, Enabled = true },
			AmbientOcclusion = new AmbientOcclusionConfig { Enabled = false },
			Reflections = new ReflectionConfig { Enabled = false },
			Bloom = new BloomConfig { Enabled = false }
		};
		builder.BeginRenderFeatures(registry);
		builder.BeginFrame(new Int2(16, 16), new Int2(16, 16), default, true, false, Vector3.UnitY, 1.0f, config, Vector3.Zero);
		builder.SetViewRenderFeatures([new RenderFeatureFrame(feature, feature.CreateFrame())]);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static WeakReference RegisterExtractAndUnregister(RenderFeatureRegistry registry, World world, RenderViewSnapshot snapshot)
	{
		var feature = new CountingFeature();
		registry.Register(world, feature);
		snapshot.ExtractRenderFeatures(world, registry);
		registry.Unregister(feature);
		return new WeakReference(feature);
	}

	private static List<RenderFeature> TakeRetired(RenderFeatureRegistry registry)
	{
		var retired = new List<RenderFeature>();
		while (registry.TryTakeRetired(out var feature))
		{
			retired.Add(feature);
		}

		return retired;
	}

	private static RenderViewResources Resources(RenderGraphFrameBuilder builder) =>
		((RenderViewState)typeof(RenderGraphFrameBuilder)
			.GetField("_view", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
			.GetValue(builder)!).FrameResources;

	/// <summary>A kernel for the scheduling fixture's device, which reports no backend.</summary>
	private static ComputeShader CreateShader(string entryPoint)
	{
		var threadGroup = new ComputeThreadGroupSize(8, 8, 1);
		var settings = new ShaderConstantBufferLayout("Settings", 0, 16, new Dictionary<string, ShaderConstantFieldLayout>
		{
			["maskHandle"] = new("maskHandle", 0, 4, ShaderConstantFieldValueKind.UInt),
			["depthHandle"] = new("depthHandle", 4, 4, ShaderConstantFieldValueKind.UInt),
			["sceneColorHandle"] = new("sceneColorHandle", 8, 4, ShaderConstantFieldValueKind.UInt),
			["strength"] = new("strength", 12, 4, ShaderConstantFieldValueKind.Float)
		});
		var kernel = new CompiledShaderArtifact(
			ShaderRequest.Compute(ShaderProgramId.ForAsset(Guid.NewGuid()), entryPoint, GraphicsBackendKind.Unknown),
			"test",
			new ShaderBytecodeSet(compute: new byte[] { 1 }, computeThreadGroupSize: threadGroup),
			new ShaderReflectionLayout([settings], []),
			threadGroup);
		return new ComputeShader("Darken", [kernel]);
	}

	private sealed class CountingFrame
	{
		public int Extracted { get; set; }
	}

	private sealed class CountingFeature : RenderFeature<CountingFrame>
	{
		public bool ThrowOnExtract { get; init; }

		public override RenderInjectionPoint InjectionPoint => RenderInjectionPoint.BeforeTemporalResolve;

		protected override void Extract(World world, CountingFrame frame)
		{
			if (ThrowOnExtract)
				throw new InvalidOperationException("Extract failed.");
			frame.Extracted++;
		}

		protected override void Record(RenderFeatureContext context, CountingFrame frame)
		{
		}
	}

	private sealed class DarkenFrame
	{
		public byte[] Mask { get; } = new byte[8];
	}

	private sealed class DarkenFeature(ComputeShader shader) : RenderFeature<DarkenFrame>
	{
		private RenderFeatureTexture? _mask;

		public bool SkipDispatch { get; init; }
		public string ConstantName { get; init; } = "strength";
		public int Initialized { get; private set; }
		public string LastError { get; private set; } = string.Empty;

		public override RenderInjectionPoint InjectionPoint => RenderInjectionPoint.BeforeTemporalResolve;

		protected override void Initialize(RenderFeatureResources resources)
		{
			Initialized++;
			// The fixture's device hands out a 1x1 RGBA16F texture whatever is asked for.
			_mask = resources.CreateTexture("Mask", 1, 1, TextureFormat.Rgba16Float);
		}

		protected override void Extract(World world, DarkenFrame frame)
		{
		}

		protected override void Record(RenderFeatureContext context, DarkenFrame frame)
		{
			try
			{
				context.Upload(_mask!, frame.Mask);
				var pass = context.AddComputePass("Darken", shader, "DarkenCS")
					.Read("maskHandle", _mask!)
					.Read("depthHandle", context.Depth)
					.ReadWrite("sceneColorHandle", context.SceneColor)
					.Set(ConstantName, 0.5f);
				if (SkipDispatch == false)
					pass.Dispatch(context.RenderSize);
			}
			catch (Exception exception)
			{
				LastError = exception.Message;
				throw;
			}
		}
	}
}
