using System.Numerics;
using System.Reflection;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class AntiAliasingRenderGraphTests
{
	[TestCase(AntiAliasingMode.Taa, true, true)]
	[TestCase(AntiAliasingMode.Taa, true, false)]
	[TestCase(AntiAliasingMode.Fsr3, true, true)]
	[TestCase(AntiAliasingMode.Taa, false, true)]
	[TestCase(AntiAliasingMode.Fsr3, false, true)]
	public void SchedulesOnlySelectedMethodAndRoutesFinalColor(AntiAliasingMode mode, bool enabled, bool cas)
	{
		var registry = new RenderGraphResourceRegistry();
		var (graph, builder) = ScreenSpaceDecalPassTests.CreateSchedulingFixture(registry);
		BeginFrame(builder, mode, enabled, cas);
		builder.Build(graph);
		var names = graph.Passes.Select(pass => pass.Name).ToArray();
		var usesTaa = enabled && mode == AntiAliasingMode.Taa;
		var usesFsr3 = enabled && mode == AntiAliasingMode.Fsr3;
		Assert.Multiple(() =>
		{
			Assert.That(names.Contains("TAA Resolve"), Is.EqualTo(usesTaa));
			Assert.That(names.Contains("TAA History Store"), Is.EqualTo(usesTaa));
			Assert.That(names.Any(name => name.StartsWith("FSR3")), Is.EqualTo(usesFsr3));
			Assert.That(names.Contains("CAS Sharpen"), Is.EqualTo(usesTaa && cas));
			Assert.That(Resources(builder).Fsr3.InternalHistoryWrite.IsValid, Is.EqualTo(usesFsr3));
			Assert.That(Resources(builder).HistoryColorWrite.IsValid, Is.EqualTo(enabled));
			Assert.That(ViewState(builder).Fsr3CurrentLumaTextures[0] is not null, Is.EqualTo(usesFsr3));
		});
		var producer = graph.Passes.Single(pass => pass.Name == (usesTaa && cas ? "CAS Sharpen" : "Tonemapping"));
		Assert.That(graph.Passes.Single(pass => pass.Name == "Copy To Final").Reads, Does.Contain(producer.Writes.Single()));
		if (usesTaa)
		{
			Assert.That(Array.IndexOf(names, "TAA Resolve"), Is.LessThan(Array.IndexOf(names, "TAA History Store")));
			Assert.That(Array.IndexOf(names, "TAA History Store"), Is.LessThan(Array.IndexOf(names, "Tonemapping")));
		}
		// Includes state tracking: TAA must never query unallocated FSR3 handles.
		Assert.DoesNotThrow(builder.CompleteFrame);
	}

	[Test]
	public void SwitchingMethodsAndDisablingInvalidateHistoryAndReleaseFsr3Resources()
	{
		var (_, builder) = ScreenSpaceDecalPassTests.CreateSchedulingFixture(new RenderGraphResourceRegistry());
		foreach (var mode in new[] { AntiAliasingMode.Taa, AntiAliasingMode.Fsr3, AntiAliasingMode.Taa })
		{
			BeginFrame(builder, mode);
			Assert.That(ViewState(builder).ResetTaaHistoryThisFrame, Is.True);
			Assert.That(ViewState(builder).HistoryValid, Is.False);
			builder.CompleteFrame();
			BeginFrame(builder, mode);
			Assert.That(ViewState(builder).ResetTaaHistoryThisFrame, Is.False);
			Assert.That(ViewState(builder).HistoryValid, Is.True);
			builder.CompleteFrame();
		}
		BeginFrame(builder, AntiAliasingMode.Taa, enabled: false);
		Assert.That(ViewState(builder).HistoryValid, Is.False);
		Assert.That(ViewState(builder).HistoryColorTextures[0], Is.Null);
		Assert.That(ViewState(builder).Fsr3CurrentLumaTextures[0], Is.Null);
		BeginFrame(builder, AntiAliasingMode.Taa);
		Assert.That(ViewState(builder).ResetTaaHistoryThisFrame, Is.True);
	}

	[Test]
	public void EachViewKeepsItsOwnTemporalHistory()
	{
		// The reason this state moved off the builder: two views must not share one history. A view that has
		// never rendered has no history, no matter how many frames another view has accumulated.
		var (_, builder) = ScreenSpaceDecalPassTests.CreateSchedulingFixture(new RenderGraphResourceRegistry());
		BeginFrame(builder, AntiAliasingMode.Taa);
		builder.CompleteFrame();
		BeginFrame(builder, AntiAliasingMode.Taa);
		builder.CompleteFrame();
		Assert.That(ViewState(builder).HistoryValid, Is.True, "the primary view should have accumulated history");

		var second = builder.GetViewStateForTest(RenderViewId.FromIndex(1));

		Assert.Multiple(() =>
		{
			Assert.That(second, Is.Not.SameAs(ViewState(builder)));
			Assert.That(second.HistoryValid, Is.False);
			Assert.That(second.HistoryColorTextures[0], Is.Null);
			Assert.That(second.HistorySize, Is.EqualTo(Int2.Zero));
		});
	}

	[Test]
	public void FrameBuilderAndSceneDataTrackPreviousAntiAliasingSettingsSeparately()
	{
		// The builder writes its pair in BeginFrame; the render graph reads its own pair later in the same
		// frame, when it builds scene data. Collapsing them into one pair would make that later read see the
		// value BeginFrame just wrote for the current frame, so a mode change would never reset history.
		var (_, builder) = ScreenSpaceDecalPassTests.CreateSchedulingFixture(new RenderGraphResourceRegistry());
		BeginFrame(builder, AntiAliasingMode.Taa);
		var view = ViewState(builder);
		view.SceneDataPreviousTaaEnabled = false;
		view.SceneDataPreviousAntiAliasingMode = AntiAliasingMode.Fsr3;
		builder.CompleteFrame();

		BeginFrame(builder, AntiAliasingMode.Taa);

		Assert.Multiple(() =>
		{
			Assert.That(view.PreviousTaaEnabled, Is.True, "the builder should have recorded this frame's setting");
			Assert.That(view.PreviousAntiAliasingMode, Is.EqualTo(AntiAliasingMode.Taa));
			Assert.That(view.SceneDataPreviousTaaEnabled, Is.False, "BeginFrame must not write the scene-data pair");
			Assert.That(view.SceneDataPreviousAntiAliasingMode, Is.EqualTo(AntiAliasingMode.Fsr3));
		});
	}

	[Test]
	public void ReleaseView_RetiresThatViewsStateAndKeepsThePrimary()
	{
		var (_, builder) = ScreenSpaceDecalPassTests.CreateSchedulingFixture(new RenderGraphResourceRegistry());
		BeginFrame(builder, AntiAliasingMode.Taa);
		builder.CompleteFrame();
		var primary = ViewState(builder);
		var secondId = RenderViewId.FromIndex(1);
		var second = builder.GetViewStateForTest(secondId);
		second.HistoryValid = true;

		builder.ReleaseView(secondId);

		Assert.Multiple(() =>
		{
			// A later view reusing the id must not inherit the released view's history.
			Assert.That(builder.GetViewStateForTest(secondId), Is.Not.SameAs(second));
			Assert.That(builder.GetViewStateForTest(secondId).HistoryValid, Is.False);
			Assert.That(ViewState(builder), Is.SameAs(primary), "releasing another view must not disturb the primary");
		});

		// The primary view is what the builder records into, so it cannot be released.
		builder.ReleaseView(RenderViewId.Primary);
		Assert.That(ViewState(builder), Is.SameAs(primary));
	}

	private static void BeginFrame(RenderGraphFrameBuilder builder, AntiAliasingMode mode, bool enabled = true, bool cas = true)
	{
		var config = new RenderConfig
		{
			AntiAliasing = new AntiAliasingConfig { Mode = mode, Enabled = enabled, Taa = new TemporalAntiAliasingConfig { EnableCasSharpen = cas } },
			AmbientOcclusion = new AmbientOcclusionConfig { Enabled = false },
			Reflections = new ReflectionConfig { Enabled = false },
			Bloom = new BloomConfig { Enabled = false }
		};
		builder.BeginFrame(new Int2(16, 16), new Int2(16, 16), default, true, false, Vector3.UnitY, 1.0f, config, Vector3.Zero);
	}

	private static RenderViewResources Resources(RenderGraphFrameBuilder builder) => GetField<RenderViewResources>(builder, "_frameResources");

	/// <summary>The per-view state the builder is currently recording into.</summary>
	private static RenderViewState ViewState(RenderGraphFrameBuilder builder) => GetField<RenderViewState>(builder, "_view");

	private static T GetField<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
}
