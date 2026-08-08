using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;

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
		using var layout = new YogaLayoutEngine();
		layout.Layout(root, 400, 200);

		Assert.Multiple(() =>
		{
			Assert.That(panel.Width, Is.EqualTo(240).Within(0.01));
			Assert.That(panel.Height, Is.EqualTo(40).Within(0.01));
			Assert.That(panel.Style.Background, Is.EqualTo(new ColorRGBA(17 / 255f, 34 / 255f, 51 / 255f, 1)));
		});
	}

	[Test]
	public void YogaLayoutRetainsTreeAndUpdatesChangedTextMetrics()
	{
		var root = new UiNode { Name = "root" };
		var container = new UiNode
		{
			Name = "div",
			Style = ComputedStyle.Default with
			{
				Width = UiLength.Pixels(100),
				Height = UiLength.Pixels(50),
				AlignItems = "center",
				JustifyContent = "center"
			}
		};
		var span = new UiNode { Name = "span" };
		var text = new UiNode { Name = "#text", Text = "9" };
		span.Children.Add(text);
		container.Children.Add(span);
		root.Children.Add(container);
		using var layout = new YogaLayoutEngine();
		layout.Layout(root, 200, 100, fullLayoutRequired: false);
		var previousLeft = text.Left;
		var previousWidth = text.Width;

		text.Text = "10";
		layout.Layout(root, 200, 100, fullLayoutRequired: false);

		Assert.Multiple(() =>
		{
			Assert.That(text.Width, Is.GreaterThan(previousWidth));
			Assert.That(text.Left, Is.LessThan(previousLeft));
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

		var changes = UiTreeReconciler.Reconcile(retained, updated);

		Assert.Multiple(() =>
		{
			Assert.That(changes.CanRetain, Is.True);
			Assert.That(changes.LayoutChanged, Is.False);
			Assert.That(changes.IntrinsicSizeChanged, Is.False);
			Assert.That(changes.VisualChanged, Is.True);
			Assert.That(retained.Children[0].Text, Is.EqualTo("0043"));
			Assert.That(retained.Children[0].Style.Opacity, Is.EqualTo(0.25f));
		});
	}

	[Test]
	public void ReconcilerReportsIntrinsicSizeChangeWhenTextWidthChanges()
	{
		var changes = UiTreeReconciler.Reconcile(Node("9", 1), Node("10", 1));
		Assert.Multiple(() =>
		{
			Assert.That(changes.CanRetain, Is.True);
			Assert.That(changes.LayoutChanged, Is.False);
			Assert.That(changes.IntrinsicSizeChanged, Is.True);
			Assert.That(changes.VisualChanged, Is.True);
		});
	}

	[Test]
	public void ReconcilerReportsIdenticalTreeAsUnchanged()
	{
		var changes = UiTreeReconciler.Reconcile(Node("0042", 1), Node("0042", 1));
		Assert.Multiple(() =>
		{
			Assert.That(changes.CanRetain, Is.True);
			Assert.That(changes.LayoutChanged, Is.False);
			Assert.That(changes.IntrinsicSizeChanged, Is.False);
			Assert.That(changes.VisualChanged, Is.False);
		});
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

		var frame = new UiFrameBuilder().Build(root, 1280, 720);

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
	public void FrameBuilderSupportsMoreThanSixtyFiveThousandVertices()
	{
		const int rectangleCount = 16_385;
		using var geometry = new UiGeometryBuilder();
		for (var i = 0; i < rectangleCount; i++)
		{
			var x = i % 256;
			var y = i / 256;
			geometry.AddFilledRect(new(x, y), new(x + 1, y + 1), 0xffffffff);
		}

		var frame = geometry.BuildFrame(256, 256, new UiTextureAtlas
		{
			Width = 1,
			Height = 1,
			PixelsRgba = [255, 255, 255, 255]
		});

		try
		{
			Assert.Multiple(() =>
			{
				Assert.That(System.Runtime.InteropServices.Marshal.SizeOf<UiVertex>(), Is.EqualTo(20));
				Assert.That(frame.VertexCount, Is.EqualTo(rectangleCount * 4));
				Assert.That(frame.VertexCount, Is.GreaterThan(ushort.MaxValue));
				Assert.That(frame.IndexCount, Is.EqualTo(rectangleCount * 6));
				Assert.That(frame.Indices[frame.IndexCount - 1], Is.GreaterThan(ushort.MaxValue));
				Assert.That(frame.CommandCount, Is.EqualTo(1));
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
