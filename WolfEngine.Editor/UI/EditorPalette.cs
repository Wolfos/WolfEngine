using System.Numerics;
using ImGuiNET;

namespace WolfEngine.Editor.UI;

internal static class EditorPalette
{
	public static Vector4 Primary => ImGui.GetStyle().Colors[(int)ImGuiCol.CheckMark];
	public static Vector4 EntityReference => new(0.50f, 0.85f, 0.74f, 1.0f);

	public static Vector4 Axis(int index)
	{
		return index switch
		{
			0 => new Vector4(0.96f, 0.57f, 0.56f, 1.0f),
			1 => new Vector4(0.59f, 0.84f, 0.62f, 1.0f),
			2 => Primary,
			_ => new Vector4(0.93f, 0.77f, 0.49f, 1.0f)
		};
	}

	public static Vector4 Tint(Vector4 surface, Vector4 accent, float amount)
	{
		var color = Vector4.Lerp(surface, accent, amount);
		color.W = surface.W;

		return color;
	}
}
