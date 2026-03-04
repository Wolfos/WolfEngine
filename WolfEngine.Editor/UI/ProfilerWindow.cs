using ImGuiNET;
using WolfEngine.Profiling;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

public class ProfilerWindow
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

	public static void Draw(IRenderer renderer)
	{
		if (_isOpen == false)
		{
			return;
		}

		var pushedBoldTitle = ImGuiUiSystem.PushBoldFont();
		ImGui.Begin("Profiler", ref _isOpen);
		var pushedRegularContent = ImGuiUiSystem.PushRegularFont();
		var vsyncEnabled = Screen.VSyncEnabled;
		if (ImGui.Checkbox("VSync", ref vsyncEnabled))
		{
			Screen.VSyncEnabled = vsyncEnabled;
		}

		ImGui.Separator();
		var frames = FrameProfiler.Instance.GetLastFrames();
		if (frames.Count == 0)
		{
			ImGui.TextUnformatted("No profiler data available.");
			ImGuiUiSystem.PopFontIfPushed(pushedRegularContent);
			ImGui.End();
			ImGuiUiSystem.PopFontIfPushed(pushedBoldTitle);
			return;
		}

		for (int i = 0; i < frames.Count; i++)
		{
			var frame = frames[i];
			var header = $"{frame.ThreadName} ({frame.ThreadId})";
			ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0);
			var pushedBoldHeader = ImGuiUiSystem.PushBoldFont();
			var headerOpen = ImGui.CollapsingHeader(header, ImGuiTreeNodeFlags.DefaultOpen);
			ImGuiUiSystem.PopFontIfPushed(pushedBoldHeader);
			ImGui.PopStyleVar();
			if (headerOpen)
			{
				double frameMs = frame.Root.DurationMs;
				ImGui.Text($"Frame: {frameMs:0.00} ms");
				ImGui.Separator();
				DrawNodes(frame.Root, frameMs);
			}
		}
		ImGuiUiSystem.PopFontIfPushed(pushedRegularContent);
		ImGui.End();
		ImGuiUiSystem.PopFontIfPushed(pushedBoldTitle);
	}
	

	private static void DrawNodes(FrameProfiler.ProfileNode node, double frameMs)
	{
		for (int i = 0; i < node.Children.Count; i++)
		{
			var child = node.Children[i];
			double ms = child.DurationMs;
			double pct = frameMs > 0.0 ? (ms / frameMs) * 100.0 : 0.0;
			var details = $"{ms:0.00} ms ({pct:0.0}%)";

			ImGui.PushID(i);
			if (child.Children.Count > 0)
			{
				bool open = ImGui.TreeNodeEx(child.Name, ImGuiTreeNodeFlags.DefaultOpen);
				ImGui.SameLine();
				ImGui.TextUnformatted(details);
				if (open)
				{
					DrawNodes(child, frameMs);
					ImGui.TreePop();
				}
			}
			else
			{
				ImGui.TreeNodeEx(child.Name, ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen);
				ImGui.SameLine();
				ImGui.TextUnformatted(details);
			}
			ImGui.PopID();
		}
	}
}
