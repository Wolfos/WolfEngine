using System.Numerics;
using ImGuiNET;

namespace WolfEngine.Editor.UI;

/// <summary>
/// The translate, rotate and scale tool buttons and the Local/World dropdown, shared by every viewport
/// with a transform gizmo so they look and behave the same everywhere.
/// </summary>
internal static class TransformToolbar
{
	public const float IconSize = 15.5f;
	public const float DropdownWidth = 80.0f;

	/// <summary>An icon button that stays highlighted while its tool is selected.</summary>
	public static bool DrawToolButton(IIconManager icons, string buttonId, string iconName, bool isSelected)
	{
		if (isSelected)
		{
			var selectedColor = ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive];
			ImGui.PushStyleColor(ImGuiCol.Button, selectedColor);
			ImGui.PushStyleColor(ImGuiCol.ButtonHovered, selectedColor);
			ImGui.PushStyleColor(ImGuiCol.ButtonActive, selectedColor);
		}

		var clicked = ImGui.ImageButton(buttonId, icons.Get(iconName), Vector2.One * IconSize);
		if (isSelected)
		{
			ImGui.PopStyleColor(3);
		}

		return clicked;
	}

	/// <summary>
	/// The three gizmo mode buttons on one line. <paramref name="highlightMode"/> is false when another
	/// tool owns the viewport, so no mode button shows as selected. Returns the mode clicked, if any.
	/// </summary>
	public static TransformGizmoMode? DrawModeButtons(IIconManager icons, TransformGizmoMode mode, bool highlightMode = true)
	{
		TransformGizmoMode? clicked = null;
		if (DrawToolButton(icons, "Translate", "translate", highlightMode && mode == TransformGizmoMode.Translate))
		{
			clicked = TransformGizmoMode.Translate;
		}

		ImGui.SameLine();
		if (DrawToolButton(icons, "Rotate", "rotate", highlightMode && mode == TransformGizmoMode.Rotate))
		{
			clicked = TransformGizmoMode.Rotate;
		}

		ImGui.SameLine();
		if (DrawToolButton(icons, "Scale", "scale", highlightMode && mode == TransformGizmoMode.Scale))
		{
			clicked = TransformGizmoMode.Scale;
		}

		return clicked;
	}

	public static void DrawSpaceDropdown(ref TransformSpace space)
	{
		ImGui.SetNextItemWidth(DropdownWidth);
		if (ImGui.BeginCombo("##Space", space == TransformSpace.Local ? "Local" : "World") == false)
		{
			return;
		}

		if (ImGui.Selectable("Local", space == TransformSpace.Local))
		{
			space = TransformSpace.Local;
		}

		if (ImGui.Selectable("World", space == TransformSpace.World))
		{
			space = TransformSpace.World;
		}

		ImGui.EndCombo();
	}
}
