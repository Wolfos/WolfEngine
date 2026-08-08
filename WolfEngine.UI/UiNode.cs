namespace WolfEngine.UI;

internal sealed class UiNode
{
	public string Name { get; set; } = string.Empty;
	public string? Text { get; set; }
	public Dictionary<string, object?> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);
	public List<UiNode> Children { get; } = [];
	public ComputedStyle Style { get; set; } = ComputedStyle.Default;
	public float Left { get; set; }
	public float Top { get; set; }
	public float Width { get; set; }
	public float Height { get; set; }

	public string? Id => Attributes.TryGetValue("id", out var value) ? value?.ToString() : null;
	public string? Classes => Attributes.TryGetValue("class", out var value) ? value?.ToString() : null;
	public bool IsText => string.Equals(Name, "#text", StringComparison.Ordinal);

	public void Reset(string name)
	{
		Name = name;
		Text = null;
		Attributes.Clear();
		Children.Clear();
		Style = ComputedStyle.Default;
		Left = 0;
		Top = 0;
		Width = 0;
		Height = 0;
	}

	public int CountNodes()
	{
		var count = 1;
		for (var i = 0; i < Children.Count; i++) count += Children[i].CountNodes();
		return count;
	}
}

internal static class UiTreeReconciler
{
	public static bool Reconcile(UiNode retained, UiNode updated)
	{
		if (!string.Equals(retained.Name, updated.Name, StringComparison.Ordinal) ||
			retained.Children.Count != updated.Children.Count)
		{
			return false;
		}

		var layoutUnchanged = (retained.Text?.Length ?? 0) == (updated.Text?.Length ?? 0) &&
			LayoutStyleEquals(retained.Style, updated.Style);
		retained.Text = updated.Text;
		retained.Style = updated.Style;
		retained.Attributes.Clear();
		foreach (var pair in updated.Attributes) retained.Attributes[pair.Key] = pair.Value;
		for (var i = 0; i < retained.Children.Count; i++)
			layoutUnchanged &= Reconcile(retained.Children[i], updated.Children[i]);
		return layoutUnchanged;
	}

	private static bool LayoutStyleEquals(ComputedStyle left, ComputedStyle right) =>
		left.Display == right.Display && left.Row == right.Row && left.Wrap == right.Wrap &&
		left.Absolute == right.Absolute && left.Width == right.Width && left.Height == right.Height &&
		left.MinWidth == right.MinWidth && left.MinHeight == right.MinHeight &&
		left.MaxWidth == right.MaxWidth && left.MaxHeight == right.MaxHeight &&
		left.Left == right.Left && left.Top == right.Top && left.FlexGrow == right.FlexGrow &&
		left.FlexShrink == right.FlexShrink && left.Gap == right.Gap && left.Padding == right.Padding &&
		left.Margin == right.Margin && left.JustifyContent == right.JustifyContent &&
		left.AlignItems == right.AlignItems && left.FontSize == right.FontSize;
}
