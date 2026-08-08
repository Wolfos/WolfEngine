using Facebook.Yoga;
using static Facebook.Yoga.YGNodeAPI;
using static Facebook.Yoga.YGNodeLayoutAPI;
using static Facebook.Yoga.YGNodeStyleAPI;

namespace WolfEngine.UI;

internal interface IUiLayoutEngine
{
	void Layout(UiNode root, float width, float height);
}

internal sealed class YogaLayoutEngine : IUiLayoutEngine
{
	public void Layout(UiNode root, float width, float height)
	{
		var yogaRoot = Build(root, width, height);
		try
		{
			YGNodeStyleSetWidth(yogaRoot, width);
			YGNodeStyleSetHeight(yogaRoot, height);
			YGNodeCalculateLayout(yogaRoot, width, height, YGDirection.LTR);
			Read(root, yogaRoot, 0, 0);
		}
		finally
		{
			YGNodeFreeRecursive(yogaRoot);
		}
	}

	private static Node Build(UiNode node, float vw, float vh)
	{
		var yoga = YGNodeNew();
		var style = node.Style;
		YGNodeStyleSetDisplay(yoga, style.Display ? YGDisplay.Flex : YGDisplay.None);
		YGNodeStyleSetFlexDirection(yoga, style.Row ? YGFlexDirection.Row : YGFlexDirection.Column);
		YGNodeStyleSetFlexWrap(yoga, style.Wrap ? YGWrap.Wrap : YGWrap.NoWrap);
		YGNodeStyleSetPositionType(yoga, style.Absolute ? YGPositionType.Absolute : YGPositionType.Relative);
		YGNodeStyleSetFlexGrow(yoga, style.FlexGrow);
		YGNodeStyleSetFlexShrink(yoga, style.FlexShrink);
		YGNodeStyleSetGap(yoga, YGGutter.All, style.Gap);
		YGNodeStyleSetPadding(yoga, YGEdge.All, style.Padding);
		YGNodeStyleSetMargin(yoga, YGEdge.All, style.Margin);
		YGNodeStyleSetJustifyContent(yoga, ParseJustify(style.JustifyContent));
		YGNodeStyleSetAlignItems(yoga, ParseAlign(style.AlignItems));
		SetDimension(yoga, style.Width, true, vw, vh, style.FontSize);
		SetDimension(yoga, style.Height, false, vw, vh, style.FontSize);
		SetMinDimension(yoga, style.MinWidth, true, vw, vh, style.FontSize);
		SetMinDimension(yoga, style.MinHeight, false, vw, vh, style.FontSize);
		SetMaxDimension(yoga, style.MaxWidth, true, vw, vh, style.FontSize);
		SetMaxDimension(yoga, style.MaxHeight, false, vw, vh, style.FontSize);
		SetPosition(yoga, YGEdge.Left, style.Left, vw, vh, style.FontSize);
		SetPosition(yoga, YGEdge.Top, style.Top, vw, vh, style.FontSize);

		if (node.IsText)
		{
			var pixel = MathF.Max(1, style.FontSize / 7f);
			YGNodeStyleSetWidth(yoga, MathF.Max(1, (node.Text?.Length ?? 0) * 6f * pixel));
			YGNodeStyleSetHeight(yoga, MathF.Max(1, 7f * pixel));
		}
		for (var i = 0; i < node.Children.Count; i++) YGNodeInsertChild(yoga, Build(node.Children[i], vw, vh), (nuint)i);
		return yoga;
	}

	private static void Read(UiNode node, Node yoga, float parentLeft, float parentTop)
	{
		node.Left = parentLeft + YGNodeLayoutGetLeft(yoga);
		node.Top = parentTop + YGNodeLayoutGetTop(yoga);
		node.Width = YGNodeLayoutGetWidth(yoga);
		node.Height = YGNodeLayoutGetHeight(yoga);
		for (var i = 0; i < node.Children.Count; i++) Read(node.Children[i], YGNodeGetChild(yoga, (nuint)i)!, node.Left, node.Top);
	}

	private static void SetDimension(Node node, UiLength length, bool width, float vw, float vh, float em)
	{
		if (length.Unit == UiLengthUnit.Auto) return;
		if (length.Unit == UiLengthUnit.Percent)
		{
			if (width) YGNodeStyleSetWidthPercent(node, length.Value); else YGNodeStyleSetHeightPercent(node, length.Value);
			return;
		}
		var value = Resolve(length, vw, vh, em);
		if (width) YGNodeStyleSetWidth(node, value); else YGNodeStyleSetHeight(node, value);
	}

	private static void SetMinDimension(Node node, UiLength length, bool width, float vw, float vh, float em)
	{
		if (length.Unit == UiLengthUnit.Auto) return;
		if (length.Unit == UiLengthUnit.Percent)
		{
			if (width) YGNodeStyleSetMinWidthPercent(node, length.Value); else YGNodeStyleSetMinHeightPercent(node, length.Value);
			return;
		}
		var value = Resolve(length, vw, vh, em);
		if (width) YGNodeStyleSetMinWidth(node, value); else YGNodeStyleSetMinHeight(node, value);
	}

	private static void SetMaxDimension(Node node, UiLength length, bool width, float vw, float vh, float em)
	{
		if (length.Unit == UiLengthUnit.Auto) return;
		if (length.Unit == UiLengthUnit.Percent)
		{
			if (width) YGNodeStyleSetMaxWidthPercent(node, length.Value); else YGNodeStyleSetMaxHeightPercent(node, length.Value);
			return;
		}
		var value = Resolve(length, vw, vh, em);
		if (width) YGNodeStyleSetMaxWidth(node, value); else YGNodeStyleSetMaxHeight(node, value);
	}

	private static void SetPosition(Node node, YGEdge edge, UiLength length, float vw, float vh, float em)
	{
		if (length.Unit == UiLengthUnit.Auto) return;
		if (length.Unit == UiLengthUnit.Percent) YGNodeStyleSetPositionPercent(node, edge, length.Value);
		else YGNodeStyleSetPosition(node, edge, Resolve(length, vw, vh, em));
	}

	private static float Resolve(UiLength length, float vw, float vh, float em) => length.Unit switch
	{
		UiLengthUnit.ViewWidth => vw * length.Value / 100f,
		UiLengthUnit.ViewHeight => vh * length.Value / 100f,
		UiLengthUnit.Em => em * length.Value,
		UiLengthUnit.Rem => 16f * length.Value,
		_ => length.Value
	};

	private static YGJustify ParseJustify(string value) => value switch
	{
		"center" => YGJustify.Center, "flex-end" => YGJustify.FlexEnd,
		"space-between" => YGJustify.SpaceBetween, "space-around" => YGJustify.SpaceAround,
		"space-evenly" => YGJustify.SpaceEvenly, _ => YGJustify.FlexStart
	};

	private static YGAlign ParseAlign(string value) => value switch
	{
		"center" => YGAlign.Center, "flex-start" => YGAlign.FlexStart,
		"flex-end" => YGAlign.FlexEnd, _ => YGAlign.Stretch
	};
}
