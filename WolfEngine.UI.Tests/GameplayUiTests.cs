using WolfEngine.Rendering;

namespace WolfEngine.UI.Tests;

public sealed class GameplayUiTests
{
	[Test]
	public void CssCascadeAndYogaLayoutResolvePercentAndMinDimensions()
	{
		var root = new UiNode { Name = "root" };
		var panel = new UiNode { Name = "div" };
		panel.Attributes["class"] = "panel featured";
		root.Children.Add(panel);
		var sheet = CssStyleSheet.Parse("""
			.panel { width: 50%; height: 30px; min-width: 240px; background-color: #112233ff; }
			div.featured { height: 40px; }
			""");

		sheet.Apply(root, 400, 200);
		new YogaLayoutEngine().Layout(root, 400, 200);

		Assert.Multiple(() =>
		{
			Assert.That(panel.Width, Is.EqualTo(240).Within(0.01));
			Assert.That(panel.Height, Is.EqualTo(40).Within(0.01));
			Assert.That(panel.Style.Background, Is.EqualTo(new ColorRGBA(17 / 255f, 34 / 255f, 51 / 255f, 1)));
		});
	}

	[Test]
	public void CssCascadeReusesComputedStylesForEquivalentNodes()
	{
		var sheet = CssStyleSheet.Parse(".panel { width: 50%; color: #112233ff; }");
		var first = new UiNode { Name = "div" };
		first.Attributes["class"] = "panel";
		var firstRoot = new UiNode { Name = "root" };
		firstRoot.Children.Add(first);
		sheet.Apply(firstRoot, 1280, 720);

		var second = new UiNode { Name = "div" };
		second.Attributes["class"] = "panel";
		var secondRoot = new UiNode { Name = "root" };
		secondRoot.Children.Add(second);
		sheet.Apply(secondRoot, 1280, 720);

		Assert.That(second.Style, Is.SameAs(first.Style));
	}

	[Test]
	public void ReconcilerSkipsLayoutForSameWidthTextAndVisualOnlyChanges()
	{
		var retained = Node("0042", opacity: 1);
		var updated = Node("0043", opacity: 0.25f);

		var layoutUnchanged = UiTreeReconciler.Reconcile(retained, updated);

		Assert.Multiple(() =>
		{
			Assert.That(layoutUnchanged, Is.True);
			Assert.That(retained.Children[0].Text, Is.EqualTo("0043"));
			Assert.That(retained.Children[0].Style.Opacity, Is.EqualTo(0.25f));
		});
	}

	[Test]
	public void ReconcilerRequestsLayoutWhenTextWidthChanges()
	{
		Assert.That(UiTreeReconciler.Reconcile(Node("9", 1), Node("10", 1)), Is.False);
	}

	[Test]
	public void FrameBuilderBatchesOneThousandBoxesIntoOneDrawCall()
	{
		var root = new UiNode { Name = "root", Width = 1280, Height = 720 };
		for (var i = 0; i < 1000; i++)
		{
			root.Children.Add(new UiNode
			{
				Name = "div",
				Left = i % 50 * 20,
				Top = i / 50 * 20,
				Width = 18,
				Height = 18,
				Style = ComputedStyle.Default with { Background = ColorRGBA.White }
			});
		}

		using var atlas = new UiFontAtlas();
		var frame = new UiFrameBuilder(atlas).Build(root, 1280, 720);

		try
		{
			Assert.Multiple(() =>
			{
				Assert.That(frame.VertexCount, Is.EqualTo(4000));
				Assert.That(frame.IndexCount, Is.EqualTo(6000));
				Assert.That(frame.CommandCount, Is.EqualTo(1));
				Assert.That(frame.HasFontAtlas, Is.True);
			});
		}
		finally
		{
			frame.Release();
		}
	}

	[Test]
	public void RenderTargetTextureIsGpuOwnedAndKeepsStableIdentity()
	{
		var texture = Texture.CreateRenderTarget("HUD", 512, 256);
		Assert.Multiple(() =>
		{
			Assert.That(texture.IsRenderTarget, Is.True);
			Assert.That(texture.Width, Is.EqualTo(512));
			Assert.That(texture.Height, Is.EqualTo(256));
			Assert.That(() => texture.ApplyTextureData(1, 1, false, TextureFormat.Rgba8Unorm,
				[new TextureMipData(1, 1, new byte[4])]),
				Throws.InvalidOperationException);
		});
	}

	private static UiNode Node(string text, float opacity)
	{
		var root = new UiNode { Name = "root" };
		root.Children.Add(new UiNode
		{
			Name = "#text",
			Text = text,
			Style = ComputedStyle.Default with { Opacity = opacity }
		});
		return root;
	}
}
