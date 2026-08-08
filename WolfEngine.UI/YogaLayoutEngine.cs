using Facebook.Yoga;
using WolfEngine.Profiling;
using static Facebook.Yoga.YGNodeAPI;
using static Facebook.Yoga.YGNodeLayoutAPI;
using static Facebook.Yoga.YGNodeStyleAPI;

namespace WolfEngine.UI;

internal interface IUiLayoutEngine : IDisposable
{
	void Layout(UiNode root, float width, float height, bool fullLayoutRequired = true);
}

/// <summary>
/// Retains Yoga's native tree so its dirty propagation and cached layout results survive across frames.
/// </summary>
internal sealed class YogaLayoutEngine : IUiLayoutEngine
{
	private sealed class Binding
	{
		public required UiNode Source { get; init; }
		public required Node Yoga { get; init; }
		public required Binding[] Children { get; init; }
		public Binding? Parent { get; init; }
		public ComputedStyle? AppliedStyle { get; set; }
		public int AppliedTextLength { get; set; } = -1;
	}

	private Binding? _root;
	private float _viewportWidth = -1;
	private float _viewportHeight = -1;
	private bool _disposed;
	private readonly List<Binding> _changedTextBindings = [];

	public void Layout(UiNode root, float width, float height, bool fullLayoutRequired = true)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		var rebuilt = _root is null || !ReferenceEquals(_root.Source, root);
		if (rebuilt)
		{
			using (FrameProfiler.Instance.Measure("Gameplay UI.Yoga Rebuild Tree"))
			{
				ReleaseTree();
				_root = Build(root, null, width, height);
			}
		}

		var viewportChanged = _viewportWidth != width || _viewportHeight != height;
		_changedTextBindings.Clear();
		using (FrameProfiler.Instance.Measure("Gameplay UI.Yoga Sync Dirty Nodes"))
		{
			Sync(_root!, width, height, viewportChanged, _changedTextBindings);
			YGNodeStyleSetWidth(_root!.Yoga, width);
			YGNodeStyleSetHeight(_root.Yoga, height);
		}
		if (!rebuilt && !viewportChanged && !fullLayoutRequired && TryPatchContainedText())
		{
			_viewportWidth = width;
			_viewportHeight = height;
			return;
		}
		using (FrameProfiler.Instance.Measure("Gameplay UI.Yoga Calculate"))
		{
			YGNodeCalculateLayout(_root!.Yoga, width, height, YGDirection.LTR);
		}
		using (FrameProfiler.Instance.Measure("Gameplay UI.Yoga Readback"))
		{
			Read(_root!, 0, 0, rebuilt);
		}
		_viewportWidth = width;
		_viewportHeight = height;
	}

	private static Binding Build(UiNode source, Binding? parent, float viewportWidth, float viewportHeight)
	{
		var yoga = YGNodeNew();
		var children = new Binding[source.Children.Count];
		var binding = new Binding { Source = source, Yoga = yoga, Children = children, Parent = parent };
		ApplyStyle(binding, viewportWidth, viewportHeight);
		for (var i = 0; i < children.Length; i++)
		{
			children[i] = Build(source.Children[i], binding, viewportWidth, viewportHeight);
			YGNodeInsertChild(yoga, children[i].Yoga, (nuint)i);
		}
		return binding;
	}

	private static void Sync(
		Binding binding,
		float viewportWidth,
		float viewportHeight,
		bool force,
		List<Binding> changedTextBindings)
	{
		var styleChanged = force || !ReferenceEquals(binding.AppliedStyle, binding.Source.Style) &&
			!Equals(binding.AppliedStyle, binding.Source.Style);
		var textLength = binding.Source.IsText ? binding.Source.Text?.Length ?? 0 : -1;
		if (binding.Source.IsText && binding.AppliedTextLength >= 0 && textLength != binding.AppliedTextLength)
			changedTextBindings.Add(binding);
		if (styleChanged || textLength != binding.AppliedTextLength)
		{
			ApplyStyle(binding, viewportWidth, viewportHeight);
		}
		for (var i = 0; i < binding.Children.Length; i++)
			Sync(binding.Children[i], viewportWidth, viewportHeight, force, changedTextBindings);
	}

	private bool TryPatchContainedText()
	{
		using (FrameProfiler.Instance.Measure("Gameplay UI.Yoga Patch Contained Text"))
		{
			if (_changedTextBindings.Count == 0) return false;
			for (var i = 0; i < _changedTextBindings.Count; i++)
			{
				if (!CanPatchContainedText(_changedTextBindings[i])) return false;
			}
			for (var i = 0; i < _changedTextBindings.Count; i++) PatchContainedText(_changedTextBindings[i]);
			return true;
		}
	}

	private static bool CanPatchContainedText(Binding text)
	{
		var textStyle = text.Source.Style;
		if (textStyle.Absolute || textStyle.FlexGrow != 0 || textStyle.Margin != 0 ||
		    !TryFindContainmentBoundary(text, out var boundary, out var branchRoot)) return false;
		return ReferenceEquals(branchRoot, text) || boundary.Source.Style.AlignItems != "stretch";
	}

	private static void PatchContainedText(Binding text)
	{
		if (!TryFindContainmentBoundary(text, out var boundary, out var branchRoot))
			throw new InvalidOperationException("Contained text patch lost its validated layout boundary.");
		var parentNode = boundary.Source;
		var parentStyle = parentNode.Style;
		var textNode = text.Source;
		var pixel = MathF.Max(1, textNode.Style.FontSize / 7f);
		var width = MathF.Max(1, (textNode.Text?.Length ?? 0) * 6f * pixel);
		var height = MathF.Max(1, 7f * pixel);
		textNode.Width = width;
		textNode.Height = height;

		for (var wrapper = text.Parent; wrapper is not null && !ReferenceEquals(wrapper, boundary); wrapper = wrapper.Parent)
		{
			wrapper.Source.Width = width;
			wrapper.Source.Height = height;
		}

		var padding = parentStyle.Padding;
		var contentLeft = parentNode.Left + padding;
		var contentTop = parentNode.Top + padding;
		var contentWidth = MathF.Max(0, parentNode.Width - padding * 2);
		var contentHeight = MathF.Max(0, parentNode.Height - padding * 2);

		if (parentStyle.Row)
		{
			branchRoot.Source.Left = PositionMain(contentLeft, contentWidth, width, parentStyle.JustifyContent);
			branchRoot.Source.Top = PositionCross(contentTop, contentHeight, height, parentStyle.AlignItems);
		}
		else
		{
			branchRoot.Source.Left = PositionCross(contentLeft, contentWidth, width, parentStyle.AlignItems);
			branchRoot.Source.Top = PositionMain(contentTop, contentHeight, height, parentStyle.JustifyContent);
		}

		for (var wrapper = branchRoot; !ReferenceEquals(wrapper, text);)
		{
			var child = wrapper.Children[0];
			child.Source.Left = wrapper.Source.Left;
			child.Source.Top = wrapper.Source.Top;
			wrapper = child;
		}
	}

	private static bool TryFindContainmentBoundary(
		Binding text,
		out Binding boundary,
		out Binding branchRoot)
	{
		branchRoot = text;
		var parent = text.Parent;
		while (parent is not null)
		{
			var style = parent.Source.Style;
			if (!style.Display || parent.Children.Length != 1)
			{
				boundary = null!;
				return false;
			}
			if (style.Width.Unit != UiLengthUnit.Auto && style.Height.Unit != UiLengthUnit.Auto)
			{
				boundary = parent;
				return true;
			}
			if (style.Absolute || style.FlexGrow != 0 || style.Padding != 0 || style.Margin != 0 || style.Gap != 0)
			{
				boundary = null!;
				return false;
			}
			branchRoot = parent;
			parent = parent.Parent;
		}

		boundary = null!;
		return false;
	}

	private static float PositionMain(float start, float available, float size, string justify) => justify switch
	{
		"center" or "space-around" or "space-evenly" => start + (available - size) * 0.5f,
		"flex-end" => start + available - size,
		_ => start
	};

	private static float PositionCross(float start, float available, float size, string align) => align switch
	{
		"center" => start + (available - size) * 0.5f,
		"flex-end" => start + available - size,
		_ => start
	};

	private static void ApplyStyle(Binding binding, float viewportWidth, float viewportHeight)
	{
		var yoga = binding.Yoga;
		var source = binding.Source;
		var style = source.Style;
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
		SetDimension(yoga, style.Width, true, viewportWidth, viewportHeight, style.FontSize);
		SetDimension(yoga, style.Height, false, viewportWidth, viewportHeight, style.FontSize);
		SetMinDimension(yoga, style.MinWidth, true, viewportWidth, viewportHeight, style.FontSize);
		SetMinDimension(yoga, style.MinHeight, false, viewportWidth, viewportHeight, style.FontSize);
		SetMaxDimension(yoga, style.MaxWidth, true, viewportWidth, viewportHeight, style.FontSize);
		SetMaxDimension(yoga, style.MaxHeight, false, viewportWidth, viewportHeight, style.FontSize);
		SetPosition(yoga, YGEdge.Left, style.Left, viewportWidth, viewportHeight, style.FontSize);
		SetPosition(yoga, YGEdge.Top, style.Top, viewportWidth, viewportHeight, style.FontSize);

		if (source.IsText)
		{
			var pixel = MathF.Max(1, style.FontSize / 7f);
			YGNodeStyleSetWidth(yoga, MathF.Max(1, (source.Text?.Length ?? 0) * 6f * pixel));
			YGNodeStyleSetHeight(yoga, MathF.Max(1, 7f * pixel));
		}
		binding.AppliedStyle = style;
		binding.AppliedTextLength = source.IsText ? source.Text?.Length ?? 0 : -1;
	}

	private static void Read(Binding binding, float parentLeft, float parentTop, bool ancestorMoved)
	{
		var hasNewLayout = YGNodeGetHasNewLayout(binding.Yoga);
		if (!ancestorMoved && !hasNewLayout) return;

		var left = parentLeft + YGNodeLayoutGetLeft(binding.Yoga);
		var top = parentTop + YGNodeLayoutGetTop(binding.Yoga);
		var moved = ancestorMoved || binding.Source.Left != left || binding.Source.Top != top;
		binding.Source.Left = left;
		binding.Source.Top = top;
		binding.Source.Width = YGNodeLayoutGetWidth(binding.Yoga);
		binding.Source.Height = YGNodeLayoutGetHeight(binding.Yoga);
		YGNodeSetHasNewLayout(binding.Yoga, false);
		for (var i = 0; i < binding.Children.Length; i++)
			Read(binding.Children[i], left, top, moved);
	}

	private static void SetDimension(Node node, UiLength length, bool width, float vw, float vh, float em)
	{
		if (length.Unit == UiLengthUnit.Auto)
		{
			if (width) YGNodeStyleSetWidthAuto(node); else YGNodeStyleSetHeightAuto(node);
			return;
		}
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
		if (length.Unit == UiLengthUnit.Auto)
		{
			if (width) YGNodeStyleSetMinWidth(node, float.NaN); else YGNodeStyleSetMinHeight(node, float.NaN);
			return;
		}
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
		if (length.Unit == UiLengthUnit.Auto)
		{
			if (width) YGNodeStyleSetMaxWidth(node, float.NaN); else YGNodeStyleSetMaxHeight(node, float.NaN);
			return;
		}
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
		if (length.Unit == UiLengthUnit.Auto)
		{
			YGNodeStyleSetPositionAuto(node, edge);
			return;
		}
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

	private void ReleaseTree()
	{
		if (_root is null) return;
		YGNodeFreeRecursive(_root.Yoga);
		_root = null;
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		ReleaseTree();
	}
}
