using ImGuiNET;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.UI;

/// <summary>Small pieces of UI shared by the clip editor panels.</summary>
internal static class AnimationClipEditorPanels
{
	public static void DrawDiagnostic(AnimationClipEditorSession session)
	{
		if (string.IsNullOrEmpty(session.Diagnostic))
		{
			return;
		}

		ImGui.PushStyleColor(ImGuiCol.Text, new ColorRGBA(1.0f, 0.55f, 0.45f, 1.0f));
		ImGui.TextWrapped(session.Diagnostic);
		ImGui.PopStyleColor();
	}

	/// <summary>
	/// Call inside a clip panel's window. While the panel has focus, document shortcuts go to the clip and
	/// the transport keys work: Space plays or pauses, arrows step, Home and End jump, K keys the selected bone.
	/// </summary>
	public static void HandleFocusAndShortcuts(AnimationClipEditorSession session)
	{
		if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) == false)
		{
			return;
		}

		session.MarkFocused();

		var io = ImGui.GetIO();
		if (session.HasClip == false ||
		    io.WantTextInput ||
		    io.KeyCtrl ||
		    io.KeySuper ||
		    io.KeyAlt)
		{
			return;
		}

		if (ImGui.IsKeyPressed(ImGuiKey.Space, false))
		{
			session.TogglePlayback();
		}

		if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow))
		{
			session.SetPlayhead(session.CurrentFrame - 1);
		}

		if (ImGui.IsKeyPressed(ImGuiKey.RightArrow))
		{
			session.SetPlayhead(session.CurrentFrame + 1);
		}

		if (ImGui.IsKeyPressed(ImGuiKey.Home, false))
		{
			session.SetPlayhead(0);
		}

		if (ImGui.IsKeyPressed(ImGuiKey.End, false))
		{
			session.SetPlayhead(session.LengthFrames);
		}

		if (ImGui.IsKeyPressed(ImGuiKey.K, false))
		{
			session.SetKeyOnSelectedBone();
		}
	}

	public static string FormatTime(float frame, int framesPerSecond)
	{
		var seconds = framesPerSecond > 0 ? frame / framesPerSecond : 0.0f;
		return $"{seconds:0.00}s";
	}
}
