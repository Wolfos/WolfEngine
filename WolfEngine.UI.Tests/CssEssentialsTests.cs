using System.Numerics;
using WolfEngine.Rendering;

namespace WolfEngine.UI.Tests;

public sealed class CssEssentialsTests
{
	[Test]
	public void RootParentSelectorDoesNotMatchNestedParentsWithTheSameAttributes()
	{
		var root = Element("same"); var child = Element("same"); var grandchild = Element("same");
		root.Children.Add(child); child.Children.Add(grandchild);
		CssStyleSheet.Parse(":root .same { font-size: 25px; } .same { font-size: 10px; }").Apply(root, 400, 200);
		Assert.That(root.Style.FontSize, Is.EqualTo(10));
		Assert.That(child.Style.FontSize, Is.EqualTo(25));
		Assert.That(grandchild.Style.FontSize, Is.EqualTo(10));
	}
	private static UiNode Element(string cssClass, string name = "div")
	{
		var node = new UiNode { Name = name }; node.Attributes["class"] = cssClass; return node;
	}
	[TestCase("1px", 1, 1, 1, 1)]
	[TestCase("1px 2px", 1, 2, 1, 2)]
	[TestCase("1px 2px 3px", 1, 2, 3, 2)]
	[TestCase("1px 2px 3px 4px", 1, 2, 3, 4)]
	public void SpacingShorthandUsesCssSideOrder(string value, float top, float right, float bottom, float left)
	{
		var node = Element("box");
		CssStyleSheet.Parse($".box {{ padding: {value}; margin: {value}; }}").Apply(node, 400, 200);
		var expected = new UiEdges(UiLength.Pixels(top), UiLength.Pixels(right), UiLength.Pixels(bottom), UiLength.Pixels(left));
		Assert.That(node.Style.Padding, Is.EqualTo(expected)); Assert.That(node.Style.Margin, Is.EqualTo(expected));
	}
	[Test]
	public void LonghandsAndShorthandsRespectDeclarationOrderAndInlineCascade()
	{
		var node = Element("box");
		node.Attributes["style"] = "padding-left: 11px; margin: 7px 8px; margin-top: -2px";
		var sheet = CssStyleSheet.Parse(".box { padding-left: 99px; padding: 1px 2px 3px 4px; padding-top: 5px; margin: 1px; }");
		sheet.Apply(node, 400, 200);
		Assert.That(node.Style.Padding, Is.EqualTo(new UiEdges(UiLength.Pixels(5), UiLength.Pixels(2), UiLength.Pixels(3), UiLength.Pixels(11))));
		Assert.That(node.Style.Margin, Is.EqualTo(new UiEdges(UiLength.Pixels(-2), UiLength.Pixels(8), UiLength.Pixels(7), UiLength.Pixels(8))));
	}
	[Test]
	public void RelativeFontSizesAndSpacingUseParentAndRootFontSizes()
	{
		var root = Element("root", "root"); var parent = Element("parent"); var child = Element("child");
		root.Children.Add(parent); parent.Children.Add(child);
		var sheet = CssStyleSheet.Parse("""
			:root { font-size: 20px; }
			.parent { font-size: 150%; }
			.child { padding: 1em 2rem; width: 5rem; height: 20px; font-size: .5em; }
			""");
		sheet.Apply(root, 400, 300);
		using var yoga = new YogaLayoutEngine(); yoga.Layout(root, 400, 300);
		Assert.That(parent.Style.FontSize, Is.EqualTo(30)); Assert.That(child.Style.FontSize, Is.EqualTo(15));
		Assert.That(child.Style.RootFontSize, Is.EqualTo(20));
		Assert.That(child.ResolvedPadding.Left, Is.EqualTo(40)); Assert.That(child.ResolvedPadding.Top, Is.EqualTo(15));
		Assert.That(child.Width, Is.EqualTo(100));
	}
	[Test]
	public void ViewportFontUnitsRecomputeOnResizeAndFontSizeInheritWinsEarlierDeclaration()
	{
		var root = Element("root", "root"); var child = Element("child"); root.Children.Add(child);
		var sheet = CssStyleSheet.Parse(".root { font-size: 5vw; } .child { font-size: 99px; font-size: inherit; }");
		sheet.Apply(root, 400, 200); Assert.That(child.Style.FontSize, Is.EqualTo(20));
		sheet.Apply(root, 800, 200); Assert.That(child.Style.FontSize, Is.EqualTo(40));
	}
	[Test]
	public void AutoMarginsAndPercentagePaddingUseYogaContainingWidth()
	{
		var root = Element("root", "root"); var parent = Element("parent"); var child = Element("child");
		root.Children.Add(parent); parent.Children.Add(child);
		CssStyleSheet.Parse(".parent { width: 200px; height: 100px; padding: 10%; } .child { width: 50px; height: 20px; margin: 0 auto; }").Apply(root, 400, 200);
		using var yoga = new YogaLayoutEngine(); yoga.Layout(root, 400, 200);
		Assert.That(parent.ResolvedPadding.Top, Is.EqualTo(40));
		Assert.That(parent.ResolvedPadding.Left, Is.EqualTo(40));
		Assert.That(child.Left, Is.EqualTo(75));
	}
	[Test]
	public void RightBottomAndInsetAnchorAndStretchAbsoluteChildren()
	{
		var root = Element("root", "root"); var pin = Element("pin"); var fill = Element("fill");
		root.Children.Add(pin); root.Children.Add(fill);
		var sheet = CssStyleSheet.Parse(".pin { position: absolute; right: 10%; bottom: 20px; width: 50px; height: 30px; } .fill { position: absolute; inset: 10px 20px 30px 40px; }");
		sheet.Apply(root, 400, 200);
		using var yoga = new YogaLayoutEngine(); yoga.Layout(root, 400, 200);
		Assert.That(pin.Left, Is.EqualTo(310)); Assert.That(pin.Top, Is.EqualTo(150));
		Assert.That(fill.Left, Is.EqualTo(40)); Assert.That(fill.Top, Is.EqualTo(10));
		Assert.That(fill.Width, Is.EqualTo(340)); Assert.That(fill.Height, Is.EqualTo(160));
		sheet.Apply(root, 800, 400); yoga.Layout(root, 800, 400);
		Assert.That(pin.Left, Is.EqualTo(670)); Assert.That(pin.Top, Is.EqualTo(350));
	}
	[Test]
	public void NestedClipsIntersectScaleAndRestoreForSiblings()
	{
		var root = new UiNode { Name = "root", Width = 200, Height = 100 };
		var outer = new UiNode { Name = "div", Left = 10, Top = 20, Width = 100, Height = 60, Style = ComputedStyle.Default with { ClipOverflow = true, Background = ColorRGBA.White } };
		var inner = new UiNode { Name = "div", Left = 50, Top = 40, Width = 100, Height = 60, Style = ComputedStyle.Default with { ClipOverflow = true, Background = ColorRGBA.White } };
		inner.Children.Add(new UiNode { Name = "div", Width = 200, Height = 100, Style = ComputedStyle.Default with { Background = ColorRGBA.White } });
		outer.Children.Add(inner); root.Children.Add(outer);
		root.Children.Add(new UiNode { Name = "div", Width = 200, Height = 100, Style = ComputedStyle.Default with { Background = ColorRGBA.White } });
		var frame = new UiFrameBuilder().Build(root, 400, 200, 2);
		try
		{
			Assert.That(frame.CommandCount, Is.EqualTo(4));
			Assert.That(frame.Commands[0].ClipRect, Is.EqualTo(new Vector4(0, 0, 400, 200)));
			Assert.That(frame.Commands[1].ClipRect, Is.EqualTo(new Vector4(20, 40, 220, 160)));
			Assert.That(frame.Commands[2].ClipRect, Is.EqualTo(new Vector4(100, 80, 220, 160)));
			Assert.That(frame.Commands[3].ClipRect, Is.EqualTo(new Vector4(0, 0, 400, 200)));
		}
		finally { frame.Release(); }
	}
	[Test]
	public void CompletelyHiddenSubtreesDoNotProduceGeometryOrEmptyCommands()
	{
		var root = new UiNode { Name = "root", Width = 100, Height = 100, Style = ComputedStyle.Default with { ClipOverflow = true } };
		var child = new UiNode { Name = "div", Left = 200, Width = 50, Height = 50, Style = ComputedStyle.Default with { ClipOverflow = true, Background = ColorRGBA.White } };
		child.Children.Add(new UiNode { Name = "div", Width = 30, Height = 30, Style = ComputedStyle.Default with { Background = ColorRGBA.White } }); root.Children.Add(child);
		var frame = new UiFrameBuilder().Build(root, 100, 100);
		try { Assert.That(frame.VertexCount, Is.Zero); Assert.That(frame.CommandCount, Is.Zero); }
		finally { frame.Release(); }
	}
	[Test]
	public void UnsupportedDeclarationsWarnOnceAndDoNotReplaceValidCascadeValues()
	{
		var sheet = CssStyleSheet.Parse(".box { font-size: 20px; font-size: 2pt; padding: 1px; padding: 2px garbage; overflow: scroll; text-align: justify; margin: auto; }");
		var root = Element("box", "root"); root.Attributes["style"] = "padding-top: -3px; padding-top: -3px";
		for (var i = 0; i < 5; i++) sheet.Apply(root, 400, 200);
		Assert.That(root.Style.FontSize, Is.EqualTo(20)); Assert.That(root.Style.Padding.Top, Is.EqualTo(UiLength.Pixels(1)));
		Assert.That(root.Style.Margin.Left.Unit, Is.EqualTo(UiLengthUnit.Auto));
		Assert.That(sheet.Diagnostics, Has.Count.EqualTo(5));
	}
	[Test]
	public void WarmStyleApplicationWithRelativeUnitsDoesNotAllocate()
	{
		var root = Element("root", "root"); var child = Element("box"); root.Children.Add(child);
		var sheet = CssStyleSheet.Parse(":root { font-size: 20px; } .box { padding: 1em 2rem; margin: 1% auto; font-size: 150%; right: 2em; overflow: hidden; }");
		for (var i = 0; i < 100; i++) sheet.Apply(root, 400, 200);
		var before = GC.GetAllocatedBytesForCurrentThread();
		for (var i = 0; i < 1000; i++) sheet.Apply(root, 400, 200);
		Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
	}

	[Test]
	public void EdgeAndAnchorChangesInvalidateLayoutButClippingOnlyInvalidatesGeometry()
	{
		var retained = new UiNode { Name = "div" };
		var clipping = new UiNode { Name = "div", Style = ComputedStyle.Default with { ClipOverflow = true } };
		var changes = UiTreeReconciler.Reconcile(retained, clipping);
		Assert.That(changes.LayoutChanged, Is.False); Assert.That(changes.VisualChanged, Is.True);
		changes = UiTreeReconciler.Reconcile(retained, new UiNode { Name = "div", Style = clipping.Style with { Right = UiLength.Pixels(10), Padding = new UiEdges(UiLength.Pixels(1), UiLength.Pixels(2), UiLength.Pixels(3), UiLength.Pixels(4)) } });
		Assert.That(changes.LayoutChanged, Is.True);
	}
}
