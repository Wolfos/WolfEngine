using System.Numerics;
using System.Reflection;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class MultiViewRecordingTests
{
	private static readonly RenderViewId Second = RenderViewId.FromIndex(1);

	[Test]
	public void TwoViewsRecordIntoOneGraphWithTheirOwnPassesAndTargets()
	{
		var (graph, builder) = RecordTwoViews(new Int2(64, 32), new Int2(24, 48));

		var primaryGBuffer = graph.Passes.Single(pass => pass.Name == "GBuffer" && pass.View == RenderViewId.Primary);
		var secondGBuffer = graph.Passes.Single(pass => pass.Name == "GBuffer" && pass.View == Second);
		Assert.Multiple(() =>
		{
			// Each view writes its own G-buffer: sharing one would have the second view overwrite the first
			// before any of the first view's lighting read it.
			Assert.That(primaryGBuffer.Writes.Intersect(secondGBuffer.Writes), Is.Empty);
			Assert.That(graph.Passes.Count(pass => pass.Name == "ImGui"), Is.EqualTo(1));
			Assert.That(graph.Passes.Single(pass => pass.Name == "ImGui").View, Is.EqualTo(RenderViewId.None));
		});
	}

	[Test]
	public void EachViewKeepsTheFrameResourcesItWasSetUpWith()
	{
		var (_, builder) = RecordTwoViews(new Int2(64, 32), new Int2(24, 48));

		builder.BindView(RenderViewId.Primary);
		var primary = ViewState(builder).FrameResources;
		builder.BindView(Second);
		var second = ViewState(builder).FrameResources;

		Assert.Multiple(() =>
		{
			// Pass callbacks read these when they execute, after both views were set up, so the second view's
			// setup must not have replaced the first view's bundle.
			Assert.That(primary.SceneFramebufferSize, Is.EqualTo(new Int2(64, 32)));
			Assert.That(second.SceneFramebufferSize, Is.EqualTo(new Int2(24, 48)));
			Assert.That(primary.GBufferAlbedo, Is.Not.EqualTo(second.GBufferAlbedo));
		});
	}

	[Test]
	public void ViewPassesAreRecordedBetweenSharedPreparationAndPresentation()
	{
		var (graph, _) = RecordTwoViews(new Int2(64, 32), new Int2(24, 48));
		var names = graph.Passes.Select(pass => pass.Name).ToList();
		var lastViewPass = graph.Passes.Select((pass, index) => (pass, index)).Last(entry => entry.pass.View.IsValid).index;

		Assert.That(names.IndexOf("ImGui"), Is.GreaterThan(lastViewPass), "presentation composites every view");
	}

	private static (RenderGraph Graph, RenderGraphFrameBuilder Builder) RecordTwoViews(Int2 primarySize, Int2 secondSize)
	{
		var (graph, builder) = ScreenSpaceDecalPassTests.CreateSchedulingFixture(new RenderGraphResourceRegistry());
		var config = new RenderConfig
		{
			AntiAliasing = new AntiAliasingConfig { Enabled = false },
			AmbientOcclusion = new AmbientOcclusionConfig { Enabled = false },
			Reflections = new ReflectionConfig { Enabled = false },
			Bloom = new BloomConfig { Enabled = false }
		};
		var framebuffer = new Int2(128, 128);
		builder.BeginSharedFrame(framebuffer, Vector3.UnitY, 1.0f, config.SkyboxConfig);
		builder.RecordSharedPreparation(graph);
		builder.BeginViewFrame(RenderViewId.Primary, framebuffer, primarySize, default, true, false, config, Vector3.Zero);
		builder.RecordBoundView(graph);
		builder.BeginViewFrame(Second, framebuffer, secondSize, default, true, false, config, Vector3.Zero);
		builder.RecordBoundView(graph);
		builder.RecordSharedPresentation(graph);
		return (graph, builder);
	}

	private static RenderViewState ViewState(RenderGraphFrameBuilder builder) =>
		(RenderViewState)typeof(RenderGraphFrameBuilder)
			.GetField("_view", BindingFlags.NonPublic | BindingFlags.Instance)!
			.GetValue(builder)!;
}
