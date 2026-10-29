using System.Numerics;
using ImGuiNET;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

/// <summary>A padded, automatically sized component card with a stable child identity.</summary>
internal readonly struct EditorComponentSection : IDisposable
{
	private static readonly Vector2 RemoveIconSize = Vector2.One * 15.5f;

	public EditorComponentSection(Type componentType)
	{
		ImGui.PushID(componentType.FullName);
		var surface = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg];
		var border = EditorPalette.Primary;
		border.W = 0.24f;
		ImGui.PushStyleColor(ImGuiCol.ChildBg, EditorPalette.Tint(surface, EditorPalette.Primary, 0.025f));
		ImGui.PushStyleColor(ImGuiCol.Border, border);
		ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6.0f);
		ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8.0f, 8.0f));
		ImGui.SetNextWindowSizeConstraints(Vector2.Zero, new Vector2(float.MaxValue));
		ImGui.BeginChild(
			"##component",
			Vector2.Zero,
			ImGuiChildFlags.Borders | ImGuiChildFlags.AutoResizeY | ImGuiChildFlags.AlwaysUseWindowPadding,
			ImGuiWindowFlags.NoScrollWithMouse);
	}

	public void Dispose()
	{
		ImGui.EndChild();
		ImGui.PopStyleVar(2);
		ImGui.PopStyleColor(2);
		ImGui.PopID();
		InspectorLayout.DrawSpace(10.0f);
	}

	public static bool DrawHeader(string label)
	{
		var surface = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg];
		var primary = EditorPalette.Primary;
		ImGui.PushStyleColor(ImGuiCol.Header, EditorPalette.Tint(surface, primary, 0.16f));
		ImGui.PushStyleColor(ImGuiCol.HeaderHovered, EditorPalette.Tint(surface, primary, 0.24f));
		ImGui.PushStyleColor(ImGuiCol.HeaderActive, EditorPalette.Tint(surface, primary, 0.30f));
		ImGui.PushStyleColor(ImGuiCol.Text, primary);
		ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(8.0f, 7.0f));
		var bold = ImGuiUiSystem.PushBoldFont();
		try
		{
			return ImGui.CollapsingHeader(
				label,
				ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.AllowOverlap);
		}
		finally
		{
			ImGuiUiSystem.PopFontIfPushed(bold);
			ImGui.PopStyleVar();
			ImGui.PopStyleColor(4);
		}
	}

	public static bool DrawHeader(string label, nint removeIcon, out bool removeRequested)
	{
		removeRequested = false;
		ImGui.BeginGroup();
		try
		{
			var isOpen = DrawHeader(label);
			if (removeIcon == 0) return isOpen;

			var headerMin = ImGui.GetItemRectMin();
			var headerMax = ImGui.GetItemRectMax();
			var frameHeight = headerMax.Y - headerMin.Y;
			var framePadding = new Vector2(
				MathF.Max((frameHeight - RemoveIconSize.X) * 0.5f, 0.0f),
				MathF.Max((frameHeight - RemoveIconSize.Y) * 0.5f, 0.0f));
			var buttonSize = RemoveIconSize + framePadding * 2.0f;
			var buttonPos = new Vector2(
				headerMax.X - buttonSize.X - ImGui.GetStyle().FramePadding.X,
				headerMin.Y + MathF.Max((frameHeight - buttonSize.Y) * 0.5f, 0.0f));

			ImGui.SetCursorScreenPos(buttonPos);
			ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
			ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1.0f, 1.0f, 1.0f, 0.12f));
			ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1.0f, 1.0f, 1.0f, 0.18f));
			ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0.0f);
			ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, framePadding);
			try
			{
				removeRequested = ImGui.ImageButton($"Remove{label}", removeIcon, RemoveIconSize);
			}
			finally
			{
				ImGui.PopStyleVar(2);
				ImGui.PopStyleColor(3);
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Remove component");
			}

			return isOpen;
		}
		finally
		{
			// The group registers the combined extent and restores normal flow.
			// Restoring the cursor manually would leave an unvalidated extent when
			// a collapsed header is the last item before EndChild.
			ImGui.EndGroup();
		}
	}
}
