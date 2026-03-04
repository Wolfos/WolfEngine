using ImGuiNET;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

public class EditorPreferencesMenu
{
	private static bool _isOpen;
	public static void Open()
	{
		_isOpen = true;
	}

	public static void Close()
	{
		_isOpen = false;
	}

	public static void Draw()
	{
			if (_isOpen == false) return;

			var pushedBoldTitle = ImGuiUiSystem.PushBoldFont();
			ImGui.Begin("Preferences", ref _isOpen);
			var pushedRegularContent = ImGuiUiSystem.PushRegularFont();
			if (ImGui.Button("Save"))
			{
				EditorPreferences.Save();
		}
		var style = ImGui.GetStyle();
		for (int i = 0; i < (int)ImGuiCol.COUNT; i++)
		{
			var v = style.Colors[i];
			if (EditorUIUtility.DrawLabeledField(((ImGuiCol)i).ToString(), () => ImGui.ColorEdit4("##value", ref v)))
			{
				style.Colors[i] = v;
					EditorPreferences.SetColor((ImGuiCol)i, v);
				}
			}
			ImGuiUiSystem.PopFontIfPushed(pushedRegularContent);
			ImGui.End();
			ImGuiUiSystem.PopFontIfPushed(pushedBoldTitle);
		}
	}
