using System.Globalization;
using ImGuiNET;
using WolfEngine.Profiling;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

public class ProfilerWindow: EditorWindow
{
	private static bool _isOpen;
	private readonly GpuProfiler _gpuProfiler;

	public ProfilerWindow(GpuProfiler gpuProfiler)
	{
		_gpuProfiler = gpuProfiler;
	}

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
		if (ImGui.BeginTabBar("profiler-tabs"))
		{
			if (ImGui.BeginTabItem("CPU"))
			{
				DrawCpuProfiler();
				ImGui.EndTabItem();
			}
			if (ImGui.BeginTabItem("GPU"))
			{
				DrawGpuProfiler();
				ImGui.EndTabItem();
			}
			ImGui.EndTabBar();
		}
		ImGui.End();
	}

	private static void DrawCpuProfiler()
	{
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
	}

	private void DrawGpuProfiler()
	{
		var unsupportedReason = _gpuProfiler.UnsupportedReason;
		var enabled = _gpuProfiler.Enabled;
		if (unsupportedReason is not null)
		{
			ImGui.BeginDisabled();
		}
		if (ImGui.Checkbox("Enable GPU profiling", ref enabled))
		{
			_gpuProfiler.Enabled = enabled;
		}
		if (unsupportedReason is not null)
		{
			ImGui.EndDisabled();
		}

		ImGui.TextWrapped("GPU profiling inserts timestamp samples into shader stages and pipeline scopes and has significant overhead.");
		ImGui.Separator();

		if (unsupportedReason is not null)
		{
			ImGui.TextWrapped(unsupportedReason);
			return;
		}

		var frame = _gpuProfiler.LatestFrame;
		if (frame is null)
		{
			ImGui.TextUnformatted(enabled
				? "Waiting for the first completed GPU profile frame..."
				: "GPU profiling is disabled.");
			return;
		}

		ImGui.Text($"Frame: {frame.FrameIndex}");
		ImGui.SameLine();
		ImGui.Text($"Total shader time: {frame.DurationMs:0.00} ms");
		if (!enabled)
		{
			ImGui.TextUnformatted("Showing the last captured frame.");
		}
		ImGui.Separator();

		if (ImGui.BeginTable(
			    "gpu-profiler",
			    2,
			    ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg |
			    ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
		{
			ImGui.TableSetupColumn("Pass / Shader", ImGuiTableColumnFlags.WidthStretch, 2.4f);
			ImGui.TableSetupColumn("GPU Time", ImGuiTableColumnFlags.WidthStretch, 1.2f);
			ImGui.TableHeadersRow();
			for (var i = 0; i < frame.Passes.Count; i++)
			{
				DrawGpuPass(frame.Passes[i], frame.DurationMs, i);
			}
			ImGui.EndTable();
		}
	}

	private static void DrawGpuPass(GpuProfilePass pass, double frameMs, int index)
	{
		ImGui.PushID(index);
		ImGui.TableNextRow();
		ImGui.TableSetColumnIndex(0);
		var open = ImGui.TreeNodeEx(
			pass.Name,
			ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanFullWidth);
		ImGui.TableSetColumnIndex(1);
		ImGui.TextUnformatted(ProfilerWindowModelBuilder.FormatGpuTime(pass.DurationMs, frameMs));
		if (open)
		{
			for (var i = 0; i < pass.Scopes.Count; i++)
			{
				var scope = pass.Scopes[i];
				ImGui.PushID(i);
				ImGui.TableNextRow();
				ImGui.TableSetColumnIndex(0);
				ImGui.TreeNodeEx(
					scope.Name,
					ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen |
					ImGuiTreeNodeFlags.SpanFullWidth);
				ImGui.TableSetColumnIndex(1);
				ImGui.TextUnformatted(ProfilerWindowModelBuilder.FormatGpuTime(scope.DurationMs, frameMs));
				ImGui.PopID();
			}
			ImGui.TreePop();
		}
		ImGui.PopID();
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

	public static string FormatGpuTime(double durationMs, double frameMs)
	{
		var percentage = frameMs > 0.0 ? durationMs / frameMs * 100.0 : 0.0;
		return string.Create(CultureInfo.InvariantCulture, $"{durationMs:0.00} ms ({percentage:0.0}%)");
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
