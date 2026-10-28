using System.Numerics;
using ImGuiNET;
using WolfEngine.Animation;

namespace WolfEngine.Editor.UI;

/// <summary>Timeline annotations shared by authored clips and imported takes.</summary>
internal static class AnimationClipMetadataEditor
{
	public static bool Draw(AnimationClipMetadata timeline, float duration, float playhead)
	{
		var changed = false;
		ImGui.SeparatorText("Curves and Markers");
		for (var i = 0; i < timeline.Curves.Count; i++)
		{
			ImGui.PushID("curve" + i);
			var curve = timeline.Curves[i];
			var name = curve.Name;
			if (ImGui.InputText("Curve name", ref name, 128))
			{
				curve.Name = name;
				changed = true;
			}

			var interpolation = (int)curve.Interpolation;
			if (ImGui.Combo("Interpolation", ref interpolation, "Step\0Linear\0Cubic\0"))
			{
				curve.Interpolation = (CurveInterpolation)interpolation;
				if (curve.Interpolation == CurveInterpolation.CubicHermite)
				{
					curve.InTangents ??= new float[curve.Times.Length];
					curve.OutTangents ??= new float[curve.Times.Length];
				}

				changed = true;
			}

			DrawPlot(curve, duration);
			for (var key = 0; key < curve.Times.Length; key++)
			{
				ImGui.PushID(key);
				var time = curve.Times[key];
				var value = curve.Values[key];
				if (ImGui.InputFloat("Time", ref time))
				{
					curve.Times[key] = Math.Clamp(time, 0, duration);
					changed = true;
				}

				if (ImGui.InputFloat("Value", ref value))
				{
					curve.Values[key] = value;
					changed = true;
				}

				if (curve.Interpolation == CurveInterpolation.CubicHermite)
				{
					var input = curve.InTangents![key];
					var output = curve.OutTangents![key];
					if (ImGui.InputFloat("In tangent", ref input))
					{
						curve.InTangents[key] = input;
						changed = true;
					}

					if (ImGui.InputFloat("Out tangent", ref output))
					{
						curve.OutTangents[key] = output;
						changed = true;
					}
				}

				var remove = ImGui.Button("Remove key");
				ImGui.PopID();
				if (remove)
				{
					ReorderKeys(curve, Enumerable.Range(0, curve.Times.Length).Where(index => index != key).ToArray());
					changed = true;
					break;
				}
			}

			if (ImGui.Button("Add key at playhead"))
			{
				if (curve.Times.Contains(playhead) == false)
				{
					curve.Times = [.. curve.Times, playhead];
					curve.Values = [.. curve.Values, 0];
					if (curve.InTangents is not null)
					{
						curve.InTangents = [.. curve.InTangents, 0];
					}

					if (curve.OutTangents is not null)
					{
						curve.OutTangents = [.. curve.OutTangents, 0];
					}

					changed = true;
				}
			}

			if (changed)
			{
				ReorderKeys(curve, Enumerable.Range(0, curve.Times.Length).OrderBy(index => curve.Times[index]).ToArray());
			}

			var removeCurve = ImGui.Button("Remove curve");
			ImGui.PopID();
			if (removeCurve)
			{
				timeline.Curves.RemoveAt(i);
				changed = true;
				break;
			}
		}

		if (ImGui.Button("Add curve"))
		{
			var suffix = timeline.Curves.Count;
			while (timeline.Curves.Any(curve => curve.Name == "Curve" + suffix))
			{
				suffix++;
			}

			timeline.Curves.Add(new AnimationCurve { Name = "Curve" + suffix });
			changed = true;
		}

		for (var i = 0; i < timeline.Markers.Count; i++)
		{
			ImGui.PushID("marker" + i);
			var marker = timeline.Markers[i];
			var name = marker.Name;
			var time = marker.Time;
			if (ImGui.InputText("Marker", ref name, 128))
			{
				marker.Name = name;
				changed = true;
			}

			if (ImGui.InputFloat("At seconds", ref time))
			{
				marker.Time = Math.Clamp(time, 0, duration);
				changed = true;
			}

			var remove = ImGui.Button("Remove marker");
			ImGui.PopID();
			if (remove)
			{
				timeline.Markers.RemoveAt(i);
				changed = true;
				break;
			}
		}

		if (ImGui.Button("Add marker at playhead"))
		{
			timeline.Markers.Add(new AnimationMarker { Name = "Marker", Time = playhead });
			changed = true;
		}

		return changed;
	}

	private static void ReorderKeys(AnimationCurve curve, int[] order)
	{
		curve.Times = order.Select(index => curve.Times[index]).ToArray();
		curve.Values = order.Select(index => curve.Values[index]).ToArray();
		curve.InTangents = curve.InTangents is null ? null : order.Select(index => curve.InTangents[index]).ToArray();
		curve.OutTangents = curve.OutTangents is null ? null : order.Select(index => curve.OutTangents[index]).ToArray();
	}

	private static void DrawPlot(AnimationCurve curve, float duration)
	{
		var origin = ImGui.GetCursorScreenPos();
		var size = new Vector2(Math.Max(100, ImGui.GetContentRegionAvail().X), 80);
		ImGui.InvisibleButton("Curve plot", size);
		var draw = ImGui.GetWindowDrawList();
		draw.AddRectFilled(origin, origin + size, 0xFF242424);
		try
		{
			var sampler = curve.Compile();
			var minimum = curve.Values.Length == 0 ? 0 : Math.Min(0, curve.Values.Min());
			var maximum = curve.Values.Length == 0 ? 1 : Math.Max(1, curve.Values.Max());
			var cursor = 0;
			var previous = origin;
			for (var sample = 0; sample <= 100; sample++)
			{
				var value = sampler.Evaluate(duration * sample / 100, ref cursor, 0);
				var position = origin + new Vector2(size.X * sample / 100, size.Y * (1 - (value - minimum) / (maximum - minimum)));
				if (sample > 0)
				{
					draw.AddLine(previous, position, 0xFF60D8F0, 2);
				}

				previous = position;
			}
		}
		catch (Exception exception)
		{
			ImGui.TextDisabled("Curve preview: " + exception.Message);
		}
	}
}
