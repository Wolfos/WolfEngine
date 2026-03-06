using ImGuiNET;
using WolfEngine.Profiling;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

public class ProfilerWindow: EditorWindow
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

	public override string Name => "Profiler";

	public override void Draw(EditorScene scene)
	{
		if (_isOpen == false)
		{
			return;
		}

		ImGui.Begin("Profiler", ref _isOpen);
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
			ImGui.End();
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
		ImGui.End();
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
