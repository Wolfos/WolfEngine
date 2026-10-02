using System.Numerics;
using System.Reflection;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Tests;

public sealed class GameplayUiViewportTests
{
	[TestCase(RenderViewOutput.Texture, false)]
	[TestCase(RenderViewOutput.Backbuffer, true)]
	public void OnlyBackbufferViewsCompositeGameplayUiIntoWindow(RenderViewOutput output, bool writesWindow)
	{
		var (graph, builder) = ScreenSpaceDecalPassTests.CreateSchedulingFixture(new RenderGraphResourceRegistry());
		var view = (RenderViewState)typeof(RenderGraphFrameBuilder).GetField("_view", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(builder)!;
		view.Output = output;
		builder.SetGameplayUiFrame(new GameplayUiRenderFrame { Screen = new UiFrameData { CommandCount = 1, Commands = [new UiDrawCommand()] } });
		builder.BeginFrame(new Int2(100, 60), new Int2(50, 30), default, true, false, Vector3.UnitY, 1,
			new RenderConfig { AntiAliasing = new AntiAliasingConfig { Enabled = false } }, Vector3.Zero);
		builder.Build(graph);
		Assert.That(graph.Passes.Any(p => p.Name == "Gameplay UI Screen Capture"), Is.True);
		Assert.That(graph.Passes.Any(p => p.Name == "Gameplay UI Screen"), Is.EqualTo(writesWindow));
		Assert.That(graph.Passes.Single(p => p.Name == "Copy To Final").Writes.Count, Is.EqualTo(writesWindow ? 2 : 1));
	}
	[TestCase(1200, 220, .5f)]
	[TestCase(400, 900, 1f)]
	[TestCase(1000, 700, .75f)]
	public void TextureViewUsesPhysicalViewportNotWindowOrInternalRenderResolution(int width, int height, float resolutionScale)
	{
		var viewport = new SceneViewportUiState(true, new Int2(width, height), resolutionScale,
			SceneDebugViewIds.FinalColor, false, false, false, false, false, Vector2.Zero, new Vector2(width, height));
		var window = new Int2(3200, 1800);
		var internalSize = new Int2((int)(width * resolutionScale), (int)(height * resolutionScale));
		Assert.That(RenderGraph.ResolveViewDisplaySize(RenderViewOutput.Texture, window, viewport, internalSize),
			Is.EqualTo(new Int2(width, height)));
		Assert.That(RenderGraph.ResolveViewDisplaySize(RenderViewOutput.Backbuffer, window, viewport, internalSize), Is.EqualTo(window));
	}

	[Test]
	public void HiddenViewUsesFallbackInsteadOfZeroSize()
	{
		var fallback = new Int2(800, 600);
		Assert.That(RenderGraph.ResolveViewDisplaySize(RenderViewOutput.Texture, fallback, SceneViewportUiState.Hidden, fallback), Is.EqualTo(fallback));
	}
}
