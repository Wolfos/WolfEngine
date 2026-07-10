using System;
using System.Collections.Generic;
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

	public static bool ColorEdit3(string label, ref Vector3 value)
	{
		BeginLabeledField(label);
		var changed = ImGui.ColorEdit3("##value", ref value);
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

	public static bool PopupButton(
		string label,
		string previewValue,
		string popupId,
		Vector2 popupSize,
		Action drawPopupContents,
		Action? drawTrailingControl = null)
	{
		BeginLabeledField(label);
		try
		{
			var trailingControlWidth = drawTrailingControl is null ? 0.0f : ImGui.GetFrameHeight();
			var itemSpacing = drawTrailingControl is null ? 0.0f : ImGui.GetStyle().ItemSpacing.X;
			var buttonSize = new Vector2(MathF.Max(1.0f, ImGui.GetContentRegionAvail().X - trailingControlWidth - itemSpacing), 0.0f);
			ImGui.PushStyleVar(ImGuiStyleVar.ButtonTextAlign, new Vector2(0.0f, 0.5f));
			try
			{
				if (ImGui.Button($"{previewValue}##value", buttonSize))
				{
					ImGui.OpenPopup(popupId);
				}
			}
			finally
			{
				ImGui.PopStyleVar();
			}

			if (drawTrailingControl is not null)
			{
				ImGui.SameLine(0.0f, itemSpacing);
				drawTrailingControl();
			}

			ImGui.SetNextWindowSize(popupSize, ImGuiCond.Appearing);
			if (ImGui.BeginPopup(popupId) == false)
			{
				return false;
			}

			try
			{
				drawPopupContents();
			}
			finally
			{
				ImGui.EndPopup();
			}

			return true;
		}
		finally
		{
			EndLabeledField();
		}
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

	public static void BeginIndentedGroup()
	{
		ImGui.Indent();
	}

	public static void EndIndentedGroup()
	{
		ImGui.Unindent();
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
