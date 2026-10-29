using ImGuiNET;
using System.Numerics;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

public enum EditorReferenceKind
{
	Asset,
	Entity
}

public class EditorUIUtility
{
	public static bool DrawLabeledField(string label, Func<bool> drawControl)
	{
		using var field = new EditorField(label);
		using var controlStyle = new EditorFieldControlStyle();

		return drawControl();
	}

	public static bool InputText(string label, ref string value, uint maxLength = 256)
	{
		using var field = new EditorField(label);
		using var controlStyle = new EditorFieldControlStyle();

		return ImGui.InputText("##value", ref value, maxLength);
	}

	public static bool InputFloat(string label, ref float value)
	{
		using var field = new EditorField(label);
		using var controlStyle = new EditorFieldControlStyle();

		return ImGui.InputFloat("##value", ref value);
	}

	public static bool InputInt(string label, ref int value)
	{
		using var field = new EditorField(label);
		using var controlStyle = new EditorFieldControlStyle();

		return ImGui.InputInt("##value", ref value);
	}

	public static bool InputDouble(string label, ref double value)
	{
		using var field = new EditorField(label);
		using var controlStyle = new EditorFieldControlStyle();

		return ImGui.InputDouble("##value", ref value);
	}

	public static bool Checkbox(string label, ref bool value)
	{
		ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(ImGui.GetStyle().FramePadding.X, EditorField.ControlPaddingY));
		try
		{
			return ImGui.Checkbox(label, ref value);
		}
		finally
		{
			ImGui.PopStyleVar();
		}
	}

	public static bool InputVector2(string label, ref Vector2 value)
	{
		Span<float> components = stackalloc float[] { value.X, value.Y };
		var changed = EditorCompoundFields.InputVector(label, components);
		if (changed)
		{
			value = new Vector2(components[0], components[1]);
		}

		return changed;
	}

	public static bool InputVector3(string label, ref Vector3 value)
	{
		Span<float> components = stackalloc float[] { value.X, value.Y, value.Z };
		var changed = EditorCompoundFields.InputVector(label, components);
		if (changed)
		{
			value = new Vector3(components[0], components[1], components[2]);
		}

		return changed;
	}

	public static bool InputVector4(string label, ref Vector4 value)
	{
		Span<float> components = stackalloc float[] { value.X, value.Y, value.Z, value.W };
		var changed = EditorCompoundFields.InputVector(label, components);
		if (changed)
		{
			value = new Vector4(components[0], components[1], components[2], components[3]);
		}

		return changed;
	}

	public static bool ColorEdit4(string label, ref Vector4 value)
	{
		using var field = new EditorField(label, compound: true);
		using var controlStyle = new EditorFieldControlStyle(preserveFrames: true);
		var compact = EditorCompoundFields.UseCompactColorPreview(field.ContentWidth, 4);
		var changed = ImGui.ColorEdit4("##value", ref value, compact ? ImGuiColorEditFlags.NoInputs : ImGuiColorEditFlags.None);
		if (compact)
		{
			EditorCompoundFields.DrawColorPreview(value, alpha: true);
		}

		return changed;
	}

	public static bool ColorEdit3(string label, ref Vector3 value)
	{
		using var field = new EditorField(label, compound: true);
		using var controlStyle = new EditorFieldControlStyle(preserveFrames: true);
		var compact = EditorCompoundFields.UseCompactColorPreview(field.ContentWidth, 3);
		var changed = ImGui.ColorEdit3("##value", ref value, compact ? ImGuiColorEditFlags.NoInputs : ImGuiColorEditFlags.None);
		if (compact)
		{
			EditorCompoundFields.DrawColorPreview(new Vector4(value, 1.0f), alpha: false);
		}

		return changed;
	}

	public static bool Combo(string label, string previewValue, Action drawItems)
	{
		var field = new EditorField(label);
		var isOpen = false;
		try
		{
			using (new EditorFieldControlStyle())
			{
				isOpen = ImGui.BeginCombo("##value", previewValue);
			}

			if (isOpen)
			{
				try
				{
					drawItems();
				}
				finally
				{
					ImGui.EndCombo();
				}
			}

			return isOpen;
		}
		finally
		{
			field.End(isOpen);
		}
	}

	public static bool PopupButton(
		string label,
		string previewValue,
		string popupId,
		Vector2 popupSize,
		Action drawPopupContents,
		Action? drawTrailingControl = null,
		EditorReferenceKind referenceKind = EditorReferenceKind.Asset)
	{
		var field = new EditorField(label, referenceKind: referenceKind);
		var popupOpen = false;
		try
		{
			using (new EditorFieldControlStyle())
			{
				var trailingControlWidth = drawTrailingControl is null ? 0.0f : ImGui.GetFrameHeight();
				var itemSpacing = drawTrailingControl is null ? 0.0f : ImGui.GetStyle().ItemSpacing.X;
				var buttonSize = new Vector2(
					MathF.Max(1.0f, field.ContentWidth - trailingControlWidth - itemSpacing),
					0.0f);
				ImGui.PushStyleVar(ImGuiStyleVar.ButtonTextAlign, new Vector2(0.0f, 0.5f));
				ImGui.PushStyleColor(
					ImGuiCol.Text,
					referenceKind == EditorReferenceKind.Asset ? EditorPalette.Primary : EditorPalette.EntityReference);
				try
				{
					if (ImGui.Button($"{previewValue}###value", buttonSize))
					{
						ImGui.OpenPopup(popupId);
					}

					if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
					{
						ImGui.SetTooltip(previewValue);
					}
				}
				finally
				{
					ImGui.PopStyleColor();
					ImGui.PopStyleVar();
				}

				if (drawTrailingControl is not null)
				{
					ImGui.SameLine(0.0f, itemSpacing);
					drawTrailingControl();
				}
			}

			popupOpen = ImGui.IsPopupOpen(popupId);
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
			field.End(popupOpen);
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

	public static bool CollapsingHeader(string label, bool isOpenByDefault = false)
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
}
