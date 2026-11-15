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
			Assert.That(Resources(builder).HistoryColorWrite.IsValid, Is.EqualTo(usesTaa));
			Assert.That(ViewState(builder).TemporalHistory.IsAllocated, Is.EqualTo(usesTaa));
			Assert.That(ViewState(builder).Fsr3History.IsAllocated, Is.EqualTo(usesFsr3));
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
			Assert.That(ActiveHistory(builder, mode).IsValid, Is.False);
			builder.CompleteFrame();
			BeginFrame(builder, mode);
			Assert.That(ViewState(builder).ResetTaaHistoryThisFrame, Is.False);
			Assert.That(ActiveHistory(builder, mode).IsValid, Is.True);
			Assert.That(InactiveHistory(builder, mode).IsAllocated, Is.False, "only the selected method holds history");
			builder.CompleteFrame();
		}
		BeginFrame(builder, AntiAliasingMode.Taa, enabled: false);
		Assert.That(ViewState(builder).TemporalHistory.IsValid, Is.False);
		Assert.That(ViewState(builder).TemporalHistory.IsAllocated, Is.False);
		Assert.That(ViewState(builder).Fsr3History.IsAllocated, Is.False);
		BeginFrame(builder, AntiAliasingMode.Taa);
		Assert.That(ViewState(builder).ResetTaaHistoryThisFrame, Is.True);
	}

	[Test]
	public void Fsr3UpscalesFromRenderSizeToDisplaySize()
	{
		var registry = new RenderGraphResourceRegistry();
		var (graph, builder) = ScreenSpaceDecalPassTests.CreateSchedulingFixture(registry);
		var displaySize = new Int2(32, 16);
		var renderSize = new Int2(16, 8);
		BeginFrame(builder, AntiAliasingMode.Fsr3, displaySize: displaySize, renderSize: renderSize);
		builder.Build(graph);

		var resources = Resources(builder);
		var history = ViewState(builder).Fsr3History;

		Assert.Multiple(() =>
		{
			Assert.That(history.Shape, Is.EqualTo(new Fsr3HistoryShape(renderSize, displaySize)));
			Assert.That(Size(registry, resources.ResolvedSceneColor), Is.EqualTo(displaySize));
			Assert.That(Size(registry, resources.Fsr3.NewLocks), Is.EqualTo(displaySize));
			Assert.That(Size(registry, resources.Fsr3.DilatedMotionVectors), Is.EqualTo(renderSize));
			Assert.That(Size(registry, resources.Fsr3.DilatedReactiveMasks), Is.EqualTo(renderSize));
			Assert.That(Size(registry, resources.Fsr3.LumaInstability), Is.EqualTo(renderSize));
			Assert.That(Size(registry, resources.LightingBuffer), Is.EqualTo(renderSize));
			Assert.That(Size(registry, resources.TonemappedLinearSceneColor), Is.EqualTo(displaySize));
		});
	}

	[Test]
	public void Fsr3PrepareReactivityReadsTheUndilatedVelocity()
	{
		// Foreground disocclusion compares each pixel's own motion with the dilated motion it borrowed.
		// Without the raw velocity it falls back to rejecting every angled surface whenever anything moves.
		var (graph, builder) = ScreenSpaceDecalPassTests.CreateSchedulingFixture(new RenderGraphResourceRegistry());
		BeginFrame(builder, AntiAliasingMode.Fsr3);
		builder.Build(graph);

		var prepareReactivity = graph.Passes.Single(pass => pass.Name == "FSR3 Prepare Reactivity");

		Assert.That(prepareReactivity.Reads, Does.Contain(Resources(builder).GBufferVelocity));
	}

	[Test]
	public void TaaWithReducedRenderScaleResolvesAtRenderSize()
	{
		// TAA does not upscale: it resolves at render size and tonemapping filters the image up to the display.
		var registry = new RenderGraphResourceRegistry();
		var (graph, builder) = ScreenSpaceDecalPassTests.CreateSchedulingFixture(registry);
		var displaySize = new Int2(32, 16);
		var renderSize = new Int2(16, 8);
		BeginFrame(builder, AntiAliasingMode.Taa, displaySize: displaySize, renderSize: renderSize);
		builder.Build(graph);

		var resources = Resources(builder);

		Assert.Multiple(() =>
		{
			Assert.That(ViewState(builder).TemporalHistory.Shape.Size, Is.EqualTo(renderSize));
			Assert.That(Size(registry, resources.ResolvedSceneColor), Is.EqualTo(renderSize));
			Assert.That(Size(registry, resources.TonemappedLinearSceneColor), Is.EqualTo(displaySize));
		});
	}

	[Test]
	public void ChangingTheRenderScaleReallocatesFsr3HistoryAndResetsIt()
	{
		var (_, builder) = ScreenSpaceDecalPassTests.CreateSchedulingFixture(new RenderGraphResourceRegistry());
		var displaySize = new Int2(32, 16);
		BeginFrame(builder, AntiAliasingMode.Fsr3, displaySize: displaySize, renderSize: displaySize);
		builder.CompleteFrame();
		BeginFrame(builder, AntiAliasingMode.Fsr3, displaySize: displaySize, renderSize: displaySize);
		Assert.That(ViewState(builder).Fsr3History.IsValid, Is.True);
		builder.CompleteFrame();

		BeginFrame(builder, AntiAliasingMode.Fsr3, displaySize: displaySize, renderSize: new Int2(16, 8));

		Assert.Multiple(() =>
		{
			Assert.That(ViewState(builder).ResetTaaHistoryThisFrame, Is.True);
			Assert.That(ViewState(builder).Fsr3History.IsValid, Is.False);
			Assert.That(ViewState(builder).Fsr3History.Shape.RenderSize, Is.EqualTo(new Int2(16, 8)));
		});
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
		Assert.That(ViewState(builder).TemporalHistory.IsValid, Is.True, "the primary view should have accumulated history");

		var second = builder.GetViewStateForTest(RenderViewId.FromIndex(1));

		Assert.Multiple(() =>
		{
			Assert.That(second, Is.Not.SameAs(ViewState(builder)));
			Assert.That(second.TemporalHistory.IsValid, Is.False);
			Assert.That(second.TemporalHistory.IsAllocated, Is.False);
			Assert.That(second.TemporalHistory.Shape.Size, Is.EqualTo(Int2.Zero));
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
		ViewHistoryTests.AccumulateOneFrame(
			second.TemporalHistory,
			new TemporalHistoryShape(new Int2(8, 8), AntiAliasingMode.Taa));

		builder.ReleaseView(secondId);

		Assert.Multiple(() =>
		{
			// A later view reusing the id must not inherit the released view's history.
			Assert.That(builder.GetViewStateForTest(secondId), Is.Not.SameAs(second));
			Assert.That(builder.GetViewStateForTest(secondId).TemporalHistory.IsValid, Is.False);
			Assert.That(ViewState(builder), Is.SameAs(primary), "releasing another view must not disturb the primary");
		});

		// The primary view is what the builder records into, so it cannot be released.
		builder.ReleaseView(RenderViewId.Primary);
		Assert.That(ViewState(builder), Is.SameAs(primary));
	}

	[Test]
	public void ViewPassesAreTaggedWithTheirViewAndSharedPassesAreNot()
	{
		// Execution rebinds per-view state from the pass's view, so a view pass left untagged would run
		// against whichever view happened to be bound, and a shared pass tagged with a view would be
		// rebound needlessly.
		var (graph, builder) = ScreenSpaceDecalPassTests.CreateSchedulingFixture(new RenderGraphResourceRegistry());
		BeginFrame(builder, AntiAliasingMode.Taa);
		builder.Build(graph);

		var gbuffer = graph.Passes.Single(pass => pass.Name == "GBuffer");
		// Copy To Final reads the view's tonemapped colour, so it is a view pass; the UI handoff covers every view.
		var copyToFinal = graph.Passes.Single(pass => pass.Name == "Copy To Final");
		var uiHandoff = graph.Passes.Single(pass => pass.Name == "UI Handoff");
		Assert.Multiple(() =>
		{
			Assert.That(gbuffer.View, Is.EqualTo(RenderViewId.Primary));
			Assert.That(copyToFinal.View, Is.EqualTo(RenderViewId.Primary));
			Assert.That(uiHandoff.View, Is.EqualTo(RenderViewId.None));
		});
	}

	[Test]
	public void BindView_SwitchesWhichViewsFrameResourcesExecutionReads()
	{
		var (_, builder) = ScreenSpaceDecalPassTests.CreateSchedulingFixture(new RenderGraphResourceRegistry());
		BeginFrame(builder, AntiAliasingMode.Taa);
		var primary = ViewState(builder);
		var second = builder.GetViewStateForTest(RenderViewId.FromIndex(1));

		builder.BindView(second.View);
		Assert.That(ViewState(builder), Is.SameAs(second));

		// A shared pass carries no view and must not disturb the binding.
		builder.BindView(RenderViewId.None);
		Assert.That(ViewState(builder), Is.SameAs(second));

		builder.BindView(RenderViewId.Primary);
		Assert.That(ViewState(builder), Is.SameAs(primary));
	}

	[Test]
	public void ViewRecordingsDoNotNest()
	{
		var (graph, _) = ScreenSpaceDecalPassTests.CreateSchedulingFixture(new RenderGraphResourceRegistry());
		graph.BeginViewRecording(RenderViewId.Primary);
		Assert.Throws<InvalidOperationException>(() => graph.BeginViewRecording(RenderViewId.FromIndex(1)));
		graph.EndViewRecording();
		Assert.DoesNotThrow(() => graph.BeginViewRecording(RenderViewId.FromIndex(1)));
	}

	private static void BeginFrame(
		RenderGraphFrameBuilder builder,
		AntiAliasingMode mode,
		bool enabled = true,
		bool cas = true,
		Int2? displaySize = null,
		Int2? renderSize = null)
	{
		var config = new RenderConfig
		{
			AntiAliasing = new AntiAliasingConfig { Mode = mode, Enabled = enabled, Taa = new TemporalAntiAliasingConfig { EnableCasSharpen = cas } },
			AmbientOcclusion = new AmbientOcclusionConfig { Enabled = false },
			Reflections = new ReflectionConfig { Enabled = false },
			Bloom = new BloomConfig { Enabled = false }
		};
		builder.BeginFrame(
			displaySize ?? new Int2(16, 16),
			renderSize ?? new Int2(16, 16),
			default,
			true,
			false,
			Vector3.UnitY,
			1.0f,
			config,
			Vector3.Zero);
	}

	private static Int2 Size(RenderGraphResourceRegistry registry, RenderGraphResourceHandle handle)
	{
		var descriptor = registry.GetTextureDescriptor(handle);

		return new Int2(descriptor.Width, descriptor.Height);
	}

	private static ViewHistory ActiveHistory(RenderGraphFrameBuilder builder, AntiAliasingMode mode)
	{
		return mode == AntiAliasingMode.Fsr3
			? ViewState(builder).Fsr3History
			: ViewState(builder).TemporalHistory;
	}

	private static ViewHistory InactiveHistory(RenderGraphFrameBuilder builder, AntiAliasingMode mode)
	{
		return mode == AntiAliasingMode.Fsr3
			? ViewState(builder).TemporalHistory
			: ViewState(builder).Fsr3History;
	}

	private static RenderViewResources Resources(RenderGraphFrameBuilder builder) => ViewState(builder).FrameResources;

	/// <summary>The per-view state the builder is currently recording into.</summary>
	private static RenderViewState ViewState(RenderGraphFrameBuilder builder) => GetField<RenderViewState>(builder, "_view");

	private static T GetField<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
}
