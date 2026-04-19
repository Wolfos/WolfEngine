using System.Collections.Generic;
using System.Globalization;
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
				ImGui.SameLine();
				ImGui.Text($"GC Alloc: {ProfilerWindowModelBuilder.FormatAllocatedBytes(frame.Root.AllocatedBytes)}");
				ImGui.Separator();
				DrawNodes(ProfilerWindowModelBuilder.AggregateChildren(frame.Root), frameMs, $"profiler-{frame.ThreadId}");
			}
		}
		ImGui.End();
	}

	private static void DrawNodes(IReadOnlyList<ProfilerDisplayNode> nodes, double frameMs, string tableId)
	{
		if (ImGui.BeginTable(tableId, 3, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
		{
			ImGui.TableSetupColumn("Sample", ImGuiTableColumnFlags.WidthStretch, 2.4f);
			ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthStretch, 1.2f);
			ImGui.TableSetupColumn("GC Alloc", ImGuiTableColumnFlags.WidthStretch, 1.0f);
			ImGui.TableHeadersRow();
			DrawRows(nodes, frameMs);
			ImGui.EndTable();
		}
	}

	private static void DrawRows(IReadOnlyList<ProfilerDisplayNode> nodes, double frameMs)
	{
		for (int i = 0; i < nodes.Count; i++)
		{
			var child = nodes[i];
			double ms = child.DurationMs;
			double pct = frameMs > 0.0 ? (ms / frameMs) * 100.0 : 0.0;
			var timeDetails = $"{ms:0.00} ms ({pct:0.0}%)";
			var allocDetails = ProfilerWindowModelBuilder.FormatAllocatedBytes(child.AllocatedBytes);

			ImGui.PushID(i);
			ImGui.TableNextRow();

			ImGui.TableSetColumnIndex(0);
			if (child.Children.Count > 0)
			{
				bool open = ImGui.TreeNodeEx(child.Name, ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanFullWidth);

				ImGui.TableSetColumnIndex(1);
				ImGui.TextUnformatted(timeDetails);

				ImGui.TableSetColumnIndex(2);
				ImGui.TextUnformatted(allocDetails);

				if (open)
				{
					DrawRows(child.Children, frameMs);
					ImGui.TreePop();
				}
			}
			else
			{
				ImGui.TreeNodeEx(child.Name, ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanFullWidth);

				ImGui.TableSetColumnIndex(1);
				ImGui.TextUnformatted(timeDetails);

				ImGui.TableSetColumnIndex(2);
				ImGui.TextUnformatted(allocDetails);
			}
			ImGui.PopID();
		}
	}
}

internal static class ProfilerWindowModelBuilder
{
	public static List<ProfilerDisplayNode> AggregateChildren(FrameProfiler.ProfileNode node)
	{
		var aggregated = new List<ProfilerDisplayNode>();
		var indicesByName = new Dictionary<string, int>();

		for (int i = 0; i < node.Children.Count; i++)
		{
			var child = node.Children[i];
			var children = AggregateChildren(child);
			if (indicesByName.TryGetValue(child.Name, out var index))
			{
				aggregated[index].AddMetrics(child.DurationMs, child.AllocatedBytes);
				aggregated[index].MergeChildren(children);
				continue;
			}

			indicesByName[child.Name] = aggregated.Count;
			aggregated.Add(new ProfilerDisplayNode(child.Name, child.DurationMs, child.AllocatedBytes, children));
		}

		return aggregated;
	}

	public static string FormatAllocatedBytes(long bytes)
	{
		const double kiloByte = 1024.0;
		const double megaByte = kiloByte * 1024.0;

		if (bytes >= megaByte)
		{
			return string.Create(CultureInfo.InvariantCulture, $"{bytes / megaByte:0.00} MB");
		}

		if (bytes >= kiloByte)
		{
			return string.Create(CultureInfo.InvariantCulture, $"{bytes / kiloByte:0.00} KB");
		}

		return $"{bytes} B";
	}
}

internal sealed class ProfilerDisplayNode
{
	private readonly Dictionary<string, int> _childIndicesByName = new();

	public ProfilerDisplayNode(string name, double durationMs, long allocatedBytes, List<ProfilerDisplayNode> children)
	{
		Name = name;
		DurationMs = durationMs;
		AllocatedBytes = allocatedBytes;
		Children = children;
		for (int i = 0; i < children.Count; i++)
		{
			_childIndicesByName[children[i].Name] = i;
		}
	}

	public string Name { get; }
	public double DurationMs { get; private set; }
	public long AllocatedBytes { get; private set; }
	public List<ProfilerDisplayNode> Children { get; }

	public void AddMetrics(double durationMs, long allocatedBytes)
	{
		DurationMs += durationMs;
		AllocatedBytes += allocatedBytes;
	}

	public void MergeChildren(List<ProfilerDisplayNode> children)
	{
		for (int i = 0; i < children.Count; i++)
		{
			var child = children[i];
			if (_childIndicesByName.TryGetValue(child.Name, out var index))
			{
				Children[index].AddMetrics(child.DurationMs, child.AllocatedBytes);
				Children[index].MergeChildren(child.Children);
				continue;
			}

			_childIndicesByName[child.Name] = Children.Count;
			Children.Add(child);
		}
	}
}
