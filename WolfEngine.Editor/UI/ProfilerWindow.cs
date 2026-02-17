using ImGuiNET;
using System;
using System.IO;
using WolfEngine.Profiling;

namespace WolfEngine.Editor.UI;

public class ProfilerWindow
{
	private static bool _isOpen;
	private static string _capturePath = BuildDefaultCapturePath();
	private static string _captureStatus = string.Empty;

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

		ImGui.Begin("Profiler", ref _isOpen);
		DrawGpuCaptureControls(renderer);

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
			if (ImGui.CollapsingHeader(header, ImGuiTreeNodeFlags.DefaultOpen))
			{
				double frameMs = frame.Root.DurationMs;
				ImGui.Text($"Frame: {frameMs:0.00} ms");
				ImGui.Separator();
				DrawNodes(frame.Root, frameMs);
			}
		}
		ImGui.End();
	}

	private static void DrawGpuCaptureControls(IRenderer renderer)
	{
		ImGui.TextUnformatted("GPU Capture");
		if (renderer.SupportsGpuCapture == false)
		{
			ImGui.TextDisabled("Programmatic GPU capture is unavailable for this renderer.");
			return;
		}

		ImGui.InputText("Output (.gputrace)", ref _capturePath, 1024);
		if (renderer.IsGpuCaptureActive)
		{
			if (ImGui.Button("Stop GPU Capture"))
			{
				if (renderer.TryStopGpuCapture(out var message))
				{
					if (string.IsNullOrWhiteSpace(message) == false)
					{
						_captureStatus = message;
					}
					else
					{
						_captureStatus = string.IsNullOrWhiteSpace(renderer.LastGpuCapturePath)
							? "GPU capture stopped."
							: $"Saved capture: {renderer.LastGpuCapturePath}";
					}
					_capturePath = BuildDefaultCapturePath();
				}
				else
				{
					_captureStatus = message;
				}
			}
		}
		else if (ImGui.Button("Start GPU Capture"))
		{
			if (renderer.TryStartGpuCapture(_capturePath, out var message))
			{
				if (string.IsNullOrWhiteSpace(message) == false)
				{
					_captureStatus = message;
				}
				else
				{
					_captureStatus = string.IsNullOrWhiteSpace(renderer.LastGpuCapturePath)
						? "GPU capture started."
						: $"GPU capture started: {renderer.LastGpuCapturePath}";
				}
			}
			else
			{
				_captureStatus = message;
			}
		}

		if (string.IsNullOrWhiteSpace(_captureStatus) == false)
		{
			ImGui.TextWrapped(_captureStatus);
		}
	}

	private static string BuildDefaultCapturePath()
	{
		return Path.Combine(
			Path.GetTempPath(),
			"WolfEngineCaptures",
			$"capture-{DateTime.Now:yyyyMMdd-HHmmss}.gputrace");
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
