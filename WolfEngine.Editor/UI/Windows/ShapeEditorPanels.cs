using ImGuiNET;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.UI;

/// <summary>Small pieces of UI shared by the Model workspace panels.</summary>
internal static class ShapeEditorPanels
{
	public static void DrawDiagnostic(ShapeEditorSession session)
	{
		DrawError(session.Diagnostic);
		DrawError(session.CompileError is { } error ? $"Compile failed: {error}" : null);
	}

	/// <summary>Call inside a shape panel's window, so document shortcuts reach the shape while it has focus.</summary>
	public static void HandleFocus(ShapeEditorSession session)
	{
		if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
		{
			session.MarkFocused();
		}
	}

	private static void DrawError(string? message)
	{
		if (string.IsNullOrEmpty(message))
		{
			return;
		}

		ImGui.PushStyleColor(ImGuiCol.Text, new ColorRGBA(1.0f, 0.55f, 0.45f, 1.0f));
		ImGui.TextWrapped(message);
		ImGui.PopStyleColor();
	}
}

/// <summary>
/// Turns a widget's continuous changes, such as a slider drag, into one undo step. Call <see cref="Changed"/> when
/// the widget reports a change and <see cref="AfterItem"/> straight after every widget it guards.
/// </summary>
internal sealed class ShapeFieldEdit
{
	private IDisposable? _edit;

	public void Changed(ShapeEditorSession session, string description)
	{
		_edit ??= session.BeginEdit(description);
	}

	public void AfterItem()
	{
		if (_edit is not null && ImGui.IsItemDeactivated())
		{
			End();
		}
	}

	public void End()
	{
		_edit?.Dispose();
		_edit = null;
	}
}
