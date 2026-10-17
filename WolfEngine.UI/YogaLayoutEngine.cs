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
		public string? AppliedText { get; set; }
		public required UiTextService Text { get; init; }
		public int AppliedTextRevision { get; set; } = -1;
		public bool NativeTextStale { get; set; }
	}

	private Binding? _root;
	private float _viewportWidth = -1;
	private float _viewportHeight = -1;
	private bool _disposed;
	private readonly List<Binding> _changedTextBindings = [];
	private readonly List<Binding> _patchedTextBindings = [];
	private readonly UiTextService _text;
	public YogaLayoutEngine(UiTextService? text = null) => _text = text ?? new UiTextService();

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
		_patchedTextBindings.Clear();
		using (FrameProfiler.Instance.Measure("Gameplay UI.Yoga Sync Dirty Nodes"))
		{
			Sync(_root!, width, height, viewportChanged, _changedTextBindings, !rebuilt && !viewportChanged && !fullLayoutRequired);
			YGNodeStyleSetWidth(_root!.Yoga, width);
			YGNodeStyleSetHeight(_root.Yoga, height);
		}
		if (!rebuilt && !viewportChanged && !fullLayoutRequired && _changedTextBindings.Count == 0)
		{
			PatchContainedTexts();
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
			// An uncontained sibling may have moved a fixed-size boundary. Restore its
			// independently measured text after native readback, using the new position.
			PatchContainedTexts();
		}
		_viewportWidth = width;
		_viewportHeight = height;
	}

	private Binding Build(UiNode source, Binding? parent, float viewportWidth, float viewportHeight)
	{
		var yoga = YGNodeNew();
		var children = new Binding[source.Children.Count];
		var binding = new Binding { Source = source, Yoga = yoga, Children = children, Parent = parent, Text = _text };
		if (source.IsText)
		{
			YGNodeSetContext(yoga, binding);
			YGNodeSetMeasureFunc(yoga, MeasureText);
		}
		ApplyStyle(binding, viewportWidth, viewportHeight);
		for (var i = 0; i < children.Length; i++)
		{
			children[i] = Build(source.Children[i], binding, viewportWidth, viewportHeight);
			YGNodeInsertChild(yoga, children[i].Yoga, (nuint)i);
		}
		return binding;
	}

	private void Sync(
		Binding binding,
		float viewportWidth,
		float viewportHeight,
		bool force,
		List<Binding> changedTextBindings,
		bool allowContainedPatch)
	{
		var styleChanged = force || !ReferenceEquals(binding.AppliedStyle, binding.Source.Style) &&
			!Equals(binding.AppliedStyle, binding.Source.Style);
		var textChanged = binding.Source.IsText && (binding.AppliedTextRevision != binding.Text.Revision ||
			!string.Equals(binding.Source.Text, binding.AppliedText, StringComparison.Ordinal));
		if (textChanged && !styleChanged && allowContainedPatch && CanPatchContainedText(binding))
		{
			binding.AppliedText = binding.Source.Text;
			binding.NativeTextStale = true;
			_patchedTextBindings.Add(binding);
		}
		else if (styleChanged || textChanged || binding.NativeTextStale && !allowContainedPatch)
		{
			if (binding.Source.IsText) changedTextBindings.Add(binding);
			ApplyStyle(binding, viewportWidth, viewportHeight);
			if (binding.Source.IsText) binding.Yoga.MarkDirtyAndPropagate();
			binding.NativeTextStale = false;
		}
		else if (binding.NativeTextStale) _patchedTextBindings.Add(binding);
		for (var i = 0; i < binding.Children.Length; i++)
			Sync(binding.Children[i], viewportWidth, viewportHeight, force, changedTextBindings, allowContainedPatch);
	}

	private void PatchContainedTexts()
	{
		using (FrameProfiler.Instance.Measure("Gameplay UI.Yoga Patch Contained Text"))
		{
			foreach (var text in _patchedTextBindings) PatchContainedText(text);
		}
	}

	private static bool CanPatchContainedText(Binding text)
	{
		var textStyle = text.Source.Style;
		if (textStyle.Absolute || textStyle.FlexGrow != 0 || !textStyle.Margin.IsZero || !textStyle.Padding.IsZero ||
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
		var padding = parentNode.ResolvedPadding;
		var measured = text.Text.Layout(textNode.Text ?? "", textNode.Style, Math.Max(0, parentNode.Width - padding.Left - padding.Right));
		textNode.TextLayout = measured;
		var width = measured.Width;
		var height = measured.Height;
		textNode.Width = width;
		textNode.Height = height;

		for (var wrapper = text.Parent; wrapper is not null && !ReferenceEquals(wrapper, boundary); wrapper = wrapper.Parent)
		{
			wrapper.Source.Width = width;
			wrapper.Source.Height = height;
		}

		var contentLeft = parentNode.Left + padding.Left;
		var contentTop = parentNode.Top + padding.Top;
		var contentWidth = MathF.Max(0, parentNode.Width - padding.Left - padding.Right);
		var contentHeight = MathF.Max(0, parentNode.Height - padding.Top - padding.Bottom);

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
		// Match Yoga's default one-point pixel grid. Text leading edges round down
		// and fractional extents round outwards rather than clipping glyphs.
		var left = branchRoot.Source.Left;
		var top = branchRoot.Source.Top;
		branchRoot.Source.Left = ReferenceEquals(branchRoot, text) ? MathF.Floor(left) : MathF.Round(left);
		branchRoot.Source.Top = ReferenceEquals(branchRoot, text) ? MathF.Floor(top) : MathF.Round(top);
		textNode.Width = (width % 1 > 0.0001f ? MathF.Ceiling(left + width) : MathF.Floor(left + width)) - MathF.Floor(left);
		textNode.Height = (height % 1 > 0.0001f ? MathF.Ceiling(top + height) : MathF.Floor(top + height)) - MathF.Floor(top);

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
			if (style.Absolute || style.FlexGrow != 0 || !style.Padding.IsZero || !style.Margin.IsZero || style.Gap != 0)
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
		SetSpacing(yoga, style.Padding, false, viewportWidth, viewportHeight, style.FontSize, style.RootFontSize);
		SetSpacing(yoga, style.Margin, true, viewportWidth, viewportHeight, style.FontSize, style.RootFontSize);
		YGNodeStyleSetJustifyContent(yoga, ParseJustify(style.JustifyContent));
		YGNodeStyleSetAlignItems(yoga, ParseAlign(style.AlignItems));
		SetDimension(yoga, style.Width, true, viewportWidth, viewportHeight, style.FontSize, style.RootFontSize);
		SetDimension(yoga, style.Height, false, viewportWidth, viewportHeight, style.FontSize, style.RootFontSize);
		SetMinDimension(yoga, style.MinWidth, true, viewportWidth, viewportHeight, style.FontSize, style.RootFontSize);
		SetMinDimension(yoga, style.MinHeight, false, viewportWidth, viewportHeight, style.FontSize, style.RootFontSize);
		SetMaxDimension(yoga, style.MaxWidth, true, viewportWidth, viewportHeight, style.FontSize, style.RootFontSize);
		SetMaxDimension(yoga, style.MaxHeight, false, viewportWidth, viewportHeight, style.FontSize, style.RootFontSize);
		SetPosition(yoga, YGEdge.Left, style.Left, viewportWidth, viewportHeight, style.FontSize, style.RootFontSize);
		SetPosition(yoga, YGEdge.Top, style.Top, viewportWidth, viewportHeight, style.FontSize, style.RootFontSize);
		SetPosition(yoga, YGEdge.Right, style.Right, viewportWidth, viewportHeight, style.FontSize, style.RootFontSize);
		SetPosition(yoga, YGEdge.Bottom, style.Bottom, viewportWidth, viewportHeight, style.FontSize, style.RootFontSize);

		if (source.IsText)
		{
			YGNodeStyleSetWidthAuto(yoga);
			YGNodeStyleSetHeightAuto(yoga);
		}
		binding.AppliedStyle = style;
		binding.AppliedText = source.Text;
		binding.AppliedTextRevision = binding.Text.Revision;
	}

	private static YGSize MeasureText(Node node, float width, MeasureMode widthMode, float height, MeasureMode heightMode)
	{
		var binding = (Binding)YGNodeGetContext(node)!;
		var text = binding.Source;
		var measured = binding.Text.Layout(text.Text ?? "", text.Style, widthMode == MeasureMode.Undefined ? float.PositiveInfinity : width);
		text.TextLayout = measured;
		return new YGSize { Width = widthMode == MeasureMode.Exactly ? width : measured.Width,
			Height = heightMode == MeasureMode.Exactly ? height : measured.Height };
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
		binding.Source.ResolvedPadding = new(YGNodeLayoutGetPadding(binding.Yoga, YGEdge.Top), YGNodeLayoutGetPadding(binding.Yoga, YGEdge.Right),
			YGNodeLayoutGetPadding(binding.Yoga, YGEdge.Bottom), YGNodeLayoutGetPadding(binding.Yoga, YGEdge.Left));
		if (binding.Source.IsText)
			binding.Source.TextLayout = binding.Text.Layout(binding.Source.Text ?? "", binding.Source.Style, binding.Source.Width);
		YGNodeSetHasNewLayout(binding.Yoga, false);
		for (var i = 0; i < binding.Children.Length; i++)
			Read(binding.Children[i], left, top, moved);
	}

	private static void SetDimension(Node node, UiLength length, bool width, float vw, float vh, float em, float rem)
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
		var value = UiCssValues.Resolve(length, vw, vh, em, rem);
		if (width) YGNodeStyleSetWidth(node, value); else YGNodeStyleSetHeight(node, value);
	}

	private static void SetMinDimension(Node node, UiLength length, bool width, float vw, float vh, float em, float rem)
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
		var value = UiCssValues.Resolve(length, vw, vh, em, rem);
		if (width) YGNodeStyleSetMinWidth(node, value); else YGNodeStyleSetMinHeight(node, value);
	}

	private static void SetMaxDimension(Node node, UiLength length, bool width, float vw, float vh, float em, float rem)
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
		var value = UiCssValues.Resolve(length, vw, vh, em, rem);
		if (width) YGNodeStyleSetMaxWidth(node, value); else YGNodeStyleSetMaxHeight(node, value);
	}

	private static void SetPosition(Node node, YGEdge edge, UiLength length, float vw, float vh, float em, float rem)
	{
		if (length.Unit == UiLengthUnit.Auto)
		{
			YGNodeStyleSetPositionAuto(node, edge);
			return;
		}
		if (length.Unit == UiLengthUnit.Percent) YGNodeStyleSetPositionPercent(node, edge, length.Value);
		else YGNodeStyleSetPosition(node, edge, UiCssValues.Resolve(length, vw, vh, em, rem));
	}

	private static void SetSpacing(Node node, UiEdges edges, bool margin, float vw, float vh, float em, float rem)
	{
		SetSpacingEdge(node, YGEdge.Top, edges.Top, margin, vw, vh, em, rem);
		SetSpacingEdge(node, YGEdge.Right, edges.Right, margin, vw, vh, em, rem);
		SetSpacingEdge(node, YGEdge.Bottom, edges.Bottom, margin, vw, vh, em, rem);
		SetSpacingEdge(node, YGEdge.Left, edges.Left, margin, vw, vh, em, rem);
	}

	private static void SetSpacingEdge(Node node, YGEdge edge, UiLength length, bool margin, float vw, float vh, float em, float rem)
	{
		if (margin && length.Unit == UiLengthUnit.Auto) YGNodeStyleSetMarginAuto(node, edge);
		else if (length.Unit == UiLengthUnit.Percent)
		{
			if (margin) YGNodeStyleSetMarginPercent(node, edge, length.Value); else YGNodeStyleSetPaddingPercent(node, edge, length.Value);
		}
		else
		{
			var value = UiCssValues.Resolve(length, vw, vh, em, rem);
			if (margin) YGNodeStyleSetMargin(node, edge, value); else YGNodeStyleSetPadding(node, edge, value);
		}
	}

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
