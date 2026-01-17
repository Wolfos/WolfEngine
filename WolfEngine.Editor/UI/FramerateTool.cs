using System;

namespace WolfEngine.Editor.UI;

public class FramerateTool
{
	private const int SampleCount = 120;
	private readonly float[] _samples = new float[SampleCount];
	private int _nextIndex;
	private int _filledCount;
	private float _averageMs;
	private float _maxMs;

	public void DrawRightAlignedInMenuBar()
	{
		UpdateSamples();
		var label = $"Frame {_averageMs:0.00} ms avg | {_maxMs:0.00} ms max";

		var style = ImGuiNET.ImGui.GetStyle();
		float textWidth = ImGuiNET.ImGui.CalcTextSize(label).X;
		float totalWidth = textWidth + style.FramePadding.X * 2.0f;
		float rightX = Math.Max(0.0f, ImGuiNET.ImGui.GetWindowWidth() - totalWidth - style.WindowPadding.X);

		ImGuiNET.ImGui.SameLine();
		ImGuiNET.ImGui.SetCursorPosX(rightX);
		if (ImGuiNET.ImGui.MenuItem(label))
		{
			ProfilerWindow.Open();
		}
	}

	private void UpdateSamples()
	{
		float deltaSeconds = ImGuiNET.ImGui.GetIO().DeltaTime;
		if (deltaSeconds <= 0.0f)
		{
			return;
		}

		float ms = deltaSeconds * 1000.0f;
		_samples[_nextIndex] = ms;
		_nextIndex = (_nextIndex + 1) % SampleCount;
		if (_filledCount < SampleCount)
		{
			_filledCount++;
		}

		float total = 0.0f;
		float max = float.MinValue;
		for (int i = 0; i < _filledCount; i++)
		{
			float sample = _samples[i];
			total += sample;
			if (sample > max)
			{
				max = sample;
			}
		}

		if (_filledCount == 0)
		{
			_averageMs = 0.0f;
			_maxMs = 0.0f;
			return;
		}

		_averageMs = total / _filledCount;
		_maxMs = max;
	}
}
