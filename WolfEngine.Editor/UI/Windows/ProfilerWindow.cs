using System.Collections.Generic;
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

		Begin(ref _isOpen);
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
				DrawNodes(AggregateChildren(frame.Root), frameMs);
			}
		}
		ImGui.End();
	}

	private static void DrawNodes(IReadOnlyList<DisplayNode> nodes, double frameMs)
	{
		for (int i = 0; i < nodes.Count; i++)
		{
			var child = nodes[i];
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
					DrawNodes(child.Children, frameMs);
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

	private static List<DisplayNode> AggregateChildren(FrameProfiler.ProfileNode node)
	{
		var aggregated = new List<DisplayNode>();
		var indicesByName = new Dictionary<string, int>();

		for (int i = 0; i < node.Children.Count; i++)
		{
			var child = node.Children[i];
			var children = AggregateChildren(child);
			if (indicesByName.TryGetValue(child.Name, out var index))
			{
				aggregated[index].AddDuration(child.DurationMs);
				aggregated[index].MergeChildren(children);
				continue;
			}

			indicesByName[child.Name] = aggregated.Count;
			aggregated.Add(new DisplayNode(child.Name, child.DurationMs, children));
		}

		return aggregated;
	}

	private sealed class DisplayNode
	{
		private readonly Dictionary<string, int> _childIndicesByName = new();

		public DisplayNode(string name, double durationMs, List<DisplayNode> children)
		{
			Name = name;
			DurationMs = durationMs;
			Children = children;
			for (int i = 0; i < children.Count; i++)
			{
				_childIndicesByName[children[i].Name] = i;
			}
		}

		public string Name { get; }
		public double DurationMs { get; private set; }
		public List<DisplayNode> Children { get; }

		public void AddDuration(double durationMs)
		{
			DurationMs += durationMs;
		}

		public void MergeChildren(List<DisplayNode> children)
		{
			for (int i = 0; i < children.Count; i++)
			{
				var child = children[i];
				if (_childIndicesByName.TryGetValue(child.Name, out var index))
				{
					Children[index].AddDuration(child.DurationMs);
					Children[index].MergeChildren(child.Children);
					continue;
				}

				_childIndicesByName[child.Name] = Children.Count;
				Children.Add(child);
			}
		}
	}
}
