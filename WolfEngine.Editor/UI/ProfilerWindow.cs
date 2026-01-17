using ImGuiNET;
using WolfEngine.Editor.Profiling;

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

	public static void Draw()
	{
		if (_isOpen == false)
		{
			return;
		}

		ImGui.Begin("Profiler", ref _isOpen);
		var root = FrameProfiler.Instance.LastFrameRoot;
		if (root == null)
		{
			ImGui.TextUnformatted("No profiler data available.");
			ImGui.End();
			return;
		}

		double frameMs = root.DurationMs;
		ImGui.Text($"Frame: {frameMs:0.00} ms");
		ImGui.Separator();
		DrawNodes(root, frameMs);
		ImGui.End();
	}

	private static void DrawNodes(FrameProfiler.ProfileNode node, double frameMs)
	{
		for (int i = 0; i < node.Children.Count; i++)
		{
			var child = node.Children[i];
			double ms = child.DurationMs;
			double pct = frameMs > 0.0 ? (ms / frameMs) * 100.0 : 0.0;
			var label = $"{child.Name} - {ms:0.00} ms ({pct:0.0}%)";

			if (child.Children.Count > 0)
			{
				if (ImGui.TreeNode(label))
				{
					DrawNodes(child, frameMs);
					ImGui.TreePop();
				}
			}
			else
			{
				ImGui.TextUnformatted(label);
			}
		}
	}
}
