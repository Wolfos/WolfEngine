using ImGuiNET;
using System.Numerics;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

public class EditorUIUtility
{
	private const float MinLabelWidth = 96.0f;
	private const float MaxLabelWidth = 320.0f;
	private const float LabelWidthFraction = 0.35f;
	private const float MinimumControlWidth = 140.0f;

	public static bool DrawLabeledField(string label, Func<bool> drawControl)
	{
		BeginLabeledField(label);
		var changed = drawControl();
		EndLabeledField();
		return changed;
	}

	public static bool InputText(string label, ref string value, uint maxLength = 256)
	{
		BeginLabeledField(label);
		var changed = ImGui.InputText("##value", ref value, maxLength);
		EndLabeledField();
		return changed;
	}

	public static bool InputFloat(string label, ref float value)
	{
		BeginLabeledField(label);
		var changed = ImGui.InputFloat("##value", ref value);
		EndLabeledField();
		return changed;
	}

	public static bool InputInt(string label, ref int value)
	{
		BeginLabeledField(label);
		var changed = ImGui.InputInt("##value", ref value);
		EndLabeledField();
		return changed;
	}

	public static bool InputDouble(string label, ref double value)
	{
		BeginLabeledField(label);
		var changed = ImGui.InputDouble("##value", ref value);
		EndLabeledField();
		return changed;
	}

	public static bool Checkbox(string label, ref bool value)
	{
		BeginLabeledField(label);
		var changed = ImGui.Checkbox("##value", ref value);
		EndLabeledField();
		return changed;
	}

	public static bool InputVector2(string label, ref Vector2 value)
	{
		BeginLabeledField(label);
		var changed = ImGui.InputFloat2("##value", ref value);
		EndLabeledField();
		return changed;
	}

	public static bool InputVector3(string label, ref Vector3 value)
	{
		BeginLabeledField(label);
		var changed = ImGui.InputFloat3("##value", ref value);
		EndLabeledField();
		return changed;
	}

	public static bool InputVector4(string label, ref Vector4 value)
	{
		BeginLabeledField(label);
		var changed = ImGui.InputFloat4("##value", ref value);
		EndLabeledField();
		return changed;
	}

	public static bool ColorEdit4(string label, ref Vector4 value)
	{
		BeginLabeledField(label);
		var changed = ImGui.ColorEdit4("##value", ref value);
		EndLabeledField();
		return changed;
	}

	public static bool Combo(string label, string previewValue, Action drawItems)
	{
		BeginLabeledField(label);
		var isOpen = ImGui.BeginCombo("##value", previewValue);
		if (isOpen == false)
		{
			EndLabeledField();
			return false;
		}

		try
		{
			drawItems();
		}
		finally
		{
			ImGui.EndCombo();
			EndLabeledField();
		}

		return true;
	}

	public static bool EnumCombo<TEnum>(string label, ref TEnum value) where TEnum : struct, Enum
	{
		var changed = false;
		var currentValue = value;
		var nextValue = value;
		Combo(label, currentValue.ToString(), () =>
		{
			foreach (var candidate in Enum.GetValues<TEnum>())
			{
				var isSelected = EqualityComparer<TEnum>.Default.Equals(candidate, currentValue);
				if (ImGui.Selectable(candidate.ToString(), isSelected))
				{
					nextValue = candidate;
					changed = true;
				}

				if (isSelected)
				{
					ImGui.SetItemDefaultFocus();
				}
			}
		});

		if (changed)
		{
			value = nextValue;
		}

		return changed;
	}

	public static bool CollapsingHeader(string label, bool isOpenByDefault)
	{
		var pushedBoldHeader = ImGuiUiSystem.PushBoldFont();
		var isOpen = ImGui.CollapsingHeader(label, isOpenByDefault ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
		ImGuiUiSystem.PopFontIfPushed(pushedBoldHeader);
		return isOpen;
	}

	private static void BeginLabeledField(string label)
	{
		ImGui.PushID(label);
		var startX = ImGui.GetCursorPosX();
		var labelWidth = CalculateLabelWidth(label);
		ImGui.AlignTextToFramePadding();
		ImGui.TextUnformatted(label);
		ImGui.SameLine();
		ImGui.SetCursorPosX(startX + labelWidth);
		ImGui.SetNextItemWidth(MathF.Max(1.0f, ImGui.GetContentRegionAvail().X));
	}

	private static void EndLabeledField()
	{
		ImGui.PopID();
	}

	private static float CalculateLabelWidth(string label)
	{
		var availableWidth = ImGui.GetContentRegionAvail().X;
		var itemSpacing = ImGui.GetStyle().ItemSpacing.X;
		var desiredWidth = ImGui.CalcTextSize(label).X + itemSpacing;
		if (availableWidth <= 0.0f)
		{
			return Math.Clamp(desiredWidth, MinLabelWidth, MaxLabelWidth);
		}

		// Scale the label column with panel width, but keep enough room for the editor control.
		var responsiveWidth = Math.Clamp(availableWidth * LabelWidthFraction, MinLabelWidth, MaxLabelWidth);
		var maxAllowedWidth = MathF.Max(MinLabelWidth, availableWidth - MinimumControlWidth);
		return MathF.Min(MathF.Max(desiredWidth, responsiveWidth), maxAllowedWidth);
	}
}
