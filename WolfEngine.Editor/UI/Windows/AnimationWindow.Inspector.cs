using ImGuiNET;
using WolfEngine.Animation;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

public sealed partial class AnimationWindow
{
	private void DrawNode(AnimationGraph graph, AnimationNode node)
	{
		ImGui.PushID(node.Id.ToString());
		try
		{
			InspectorLayout.DrawSpace();
			var bold = ImGuiUiSystem.PushBoldFont();
			ImGui.TextUnformatted(AnimationGraphLayout.Title(node));
			ImGuiUiSystem.PopFontIfPushed(bold);
			ImGui.TextColored(NodeColor(node.Kind), AnimationGraphLayout.KindName(node.Kind));
			InspectorLayout.DrawSpace();
			var name = node.Name;
			if (EditorUIUtility.InputText("Node name", ref name, 128))
			{
				node.Name = name;
			}

			if (node.Kind == AnimationNodeKind.Clip)
			{
				DrawClipProperties(graph, node);
			}

			if (node.Kind is AnimationNodeKind.Blend or AnimationNodeKind.MaskedBlend or AnimationNodeKind.Select or AnimationNodeKind.Locomotion1D)
			{
				var parameter = node.Parameter;
				var label = node.Kind == AnimationNodeKind.Locomotion1D ? "Speed parameter" : "Control parameter";
				ParameterChoice(graph, label, ref parameter);
				node.Parameter = parameter;
				if (parameter.Length == 0)
				{
					var value = node.Value;
					if (EditorUIUtility.InputFloat(node.Kind == AnimationNodeKind.Locomotion1D ? "Constant speed" : "Constant value", ref value))
					{
						node.Value = value;
					}
				}
			}

			if (node.Kind == AnimationNodeKind.MaskedBlend)
			{
				var mask = node.Mask;
				if (DrawAssetRef("Bone mask", ref mask))
				{
					node.Mask = mask;
				}
			}

			if (node.Kind is AnimationNodeKind.Clip or AnimationNodeKind.StateMachine)
			{
				var restart = node.RestartOnActivation;
				if (EditorUIUtility.Checkbox("Restart when activated", ref restart))
				{
					node.RestartOnActivation = restart;
				}
			}

			if (node.Inputs.Count > 0)
			{
				InspectorLayout.DrawSpace();
				ImGui.SeparatorText(node.Kind == AnimationNodeKind.StateMachine ? "States" : "Pose inputs");
				DrawPoseInputs(graph, node);
			}

			if (node.Kind is AnimationNodeKind.Select or AnimationNodeKind.StateMachine or AnimationNodeKind.Locomotion1D)
			{
				if (ImGui.Button("+ Add input"))
				{
					node.Inputs.Add(Guid.Empty);
					if (node.Kind == AnimationNodeKind.Locomotion1D)
					{
						while (node.Thresholds.Count < node.Inputs.Count)
						{
							node.Thresholds.Add(node.Thresholds.Count == 0 ? 0.0f : node.Thresholds[^1] + 1.0f);
						}
					}
				}

				ImGui.SameLine();
				var minimum = node.Kind == AnimationNodeKind.Locomotion1D ? 2 : 1;
				ImGui.BeginDisabled(node.Inputs.Count <= minimum);
				if (ImGui.Button("Remove last"))
				{
					var removed = node.Inputs.Count - 1;
					node.Inputs.RemoveAt(removed);
					if (node.Thresholds.Count > removed)
					{
						node.Thresholds.RemoveRange(removed, node.Thresholds.Count - removed);
					}

					node.Transitions.RemoveAll(transition => transition.From == removed || transition.To == removed);
				}

				ImGui.EndDisabled();
			}

			if (node.Kind == AnimationNodeKind.StateMachine)
			{
				DrawTransitions(graph, node);
			}

			if (node.Kind == AnimationNodeKind.CurveRemap)
			{
				var curve = node.Curve;
				if (EditorUIUtility.InputText("Curve name", ref curve, 128))
				{
					node.Curve = curve;
				}

				var scale = node.CurveScale;
				if (EditorUIUtility.InputFloat("Scale", ref scale))
				{
					node.CurveScale = scale;
				}

				var offset = node.CurveOffset;
				if (EditorUIUtility.InputFloat("Offset", ref offset))
				{
					node.CurveOffset = offset;
				}
			}

			if (node.Kind == AnimationNodeKind.Output)
			{
				if (graph.Output == node.Id)
				{
					ImGui.TextColored(EditorPalette.Primary, "Active graph output");
				}
				else if (ImGui.Button("Use as graph output"))
				{
					graph.Output = node.Id;
				}
			}
		}
		finally
		{
			ImGui.PopID();
		}
	}

	private void DrawClipProperties(AnimationGraph graph, AnimationNode node)
	{
		var slot = node.ClipSlot;
		if (EditorUIUtility.InputText("Clip slot", ref slot, 128))
		{
			node.ClipSlot = slot;
		}

		var speed = node.Speed;
		if (EditorUIUtility.InputFloat("Playback speed", ref speed))
		{
			node.Speed = speed;
		}

		var loop = node.Loop;
		if (EditorUIUtility.Checkbox("Loop", ref loop))
		{
			node.Loop = loop;
		}

		if (EditorUIUtility.CollapsingHeader("Advanced playback"))
		{
			var parameter = node.Parameter;
			ParameterChoice(graph, "Speed multiplier parameter", ref parameter);
			node.Parameter = parameter;
			var start = node.StartTime;
			if (EditorUIUtility.InputFloat("Start time (seconds)", ref start))
			{
				node.StartTime = start;
			}

			var time = node.TimeParameter;
			ParameterChoice(graph, "External normalized time", ref time);
			node.TimeParameter = time;
			var sequence = node.SequenceParameter;
			ParameterChoice(graph, "Action sequence", ref sequence);
			node.SequenceParameter = sequence;
		}
	}

	private void DrawPoseInputs(AnimationGraph graph, AnimationNode node)
	{
		for (var index = 0; index < node.Inputs.Count; index++)
		{
			ImGui.PushID(index);
			try
			{
				var input = node.Inputs[index];
				var source = graph.Nodes.FirstOrDefault(candidate => candidate.Id == input);
				var label = node.Kind switch
				{
					AnimationNodeKind.StateMachine => $"State {index}",
					AnimationNodeKind.Locomotion1D => $"Sample {index}",
					AnimationNodeKind.Output => "Final pose",
					_ => $"Input {index}"};
				var next = input;
				EditorUIUtility.Combo(label, source is null ? "Unconnected" : AnimationGraphLayout.Title(source), () =>
				{
					var allowedSources = AnimationGraphLayout.ConnectionSources(graph, node.Id);
					if (ImGui.Selectable("Unconnected", input == Guid.Empty))
					{
						next = Guid.Empty;
					}

					foreach (var candidate in graph.Nodes)
					{
						if (allowedSources.Contains(candidate.Id) == false)
						{
							continue;
						}

						if (ImGui.Selectable($"{AnimationGraphLayout.Title(candidate)}##{candidate.Id}", input == candidate.Id))
						{
							next = candidate.Id;
						}
					}
				});
				node.Inputs[index] = next;
				if (node.Kind == AnimationNodeKind.StateMachine && source is not null && ImGui.SmallButton("Focus connected node"))
				{
					SelectNode(source.Id, focus: true);
				}

				if (node.Kind == AnimationNodeKind.Locomotion1D)
				{
					var threshold = index < node.Thresholds.Count ? node.Thresholds[index] : index;
					if (EditorUIUtility.InputFloat("Speed threshold", ref threshold))
					{
						while (node.Thresholds.Count <= index)
						{
							node.Thresholds.Add(node.Thresholds.Count);
						}

						node.Thresholds[index] = threshold;
					}
				}
			}
			finally
			{
				ImGui.PopID();
			}
		}
	}

	private static void ParameterChoice(AnimationGraph graph, string label, ref string name)
	{
		var current = name;
		var next = current;
		EditorUIUtility.Combo(label, current.Length == 0 ? "Constant / internal" : current, () =>
		{
			if (ImGui.Selectable("Constant / internal", current.Length == 0))
			{
				next = string.Empty;
			}

			foreach (var parameter in graph.Parameters)
			{
				if (ImGui.Selectable(parameter.Name, parameter.Name == current))
				{
					next = parameter.Name;
				}
			}
		});
		name = next;
	}

	private static string StateName(AnimationGraph graph, AnimationNode node, int state)
	{
		if (state == -1)
		{
			return "Any state";
		}

		if (state < 0 || state >= node.Inputs.Count)
		{
			return $"Missing state {state}";
		}

		var input = graph.Nodes.FirstOrDefault(candidate => candidate.Id == node.Inputs[state]);
		return $"{state} · {(input is null ? "Unconnected" : AnimationGraphLayout.Title(input))}";
	}

	private static int StateChoice(AnimationGraph graph, AnimationNode node, string label, int value, bool allowAny)
	{
		var next = value;
		EditorUIUtility.Combo(label, StateName(graph, node, value), () =>
		{
			if (allowAny && ImGui.Selectable("Any state", value == -1))
			{
				next = -1;
			}

			for (var index = 0; index < node.Inputs.Count; index++)
			{
				if (ImGui.Selectable(StateName(graph, node, index), value == index))
				{
					next = index;
				}
			}
		});
		return next;
	}

	private static void DrawTransitions(AnimationGraph graph, AnimationNode node)
	{
		InspectorLayout.DrawSpace();
		ImGui.SeparatorText($"Transitions · {node.Transitions.Count}");
		ImGui.TextDisabled("Evaluated in order; the first match wins.");
		for (var index = 0; index < node.Transitions.Count; index++)
		{
			var transition = node.Transitions[index];
			ImGui.PushID(index);
			try
			{
				var title = $"{StateName(graph, node, transition.From)} to {StateName(graph, node, transition.To)}###Transition";
				if (EditorUIUtility.CollapsingHeader(title) == false)
				{
					continue;
				}

				transition.From = StateChoice(graph, node, "From", transition.From, allowAny: true);
				transition.To = StateChoice(graph, node, "To", transition.To, allowAny: false);
				var duration = transition.Duration;
				if (EditorUIUtility.InputFloat("Blend duration (seconds)", ref duration))
				{
					transition.Duration = duration;
				}

				var waitForExit = transition.ExitTime >= 0.0f;
				if (EditorUIUtility.Checkbox("Wait for exit progress", ref waitForExit))
				{
					transition.ExitTime = waitForExit ? 1.0f : -1.0f;
				}

				if (waitForExit)
				{
					var exit = transition.ExitTime;
					if (EditorUIUtility.InputFloat("Exit progress", ref exit))
					{
						transition.ExitTime = exit;
					}
				}

				for (var conditionIndex = 0; conditionIndex < transition.Conditions.Count; conditionIndex++)
				{
					ImGui.PushID($"condition{conditionIndex}");
					var condition = transition.Conditions[conditionIndex];
					var parameter = condition.Parameter;
					ParameterChoice(graph, "Condition parameter", ref parameter);
					condition.Parameter = parameter;
					var comparison = condition.Comparison;
					if (EditorUIUtility.EnumCombo("Comparison", ref comparison))
					{
						condition.Comparison = comparison;
					}

					var value = condition.Value;
					if (EditorUIUtility.InputFloat("Compare value", ref value))
					{
						condition.Value = value;
					}

					if (ImGui.SmallButton("Remove condition"))
					{
						transition.Conditions.RemoveAt(conditionIndex--);
					}

					ImGui.PopID();
				}

				if (ImGui.Button("+ Condition"))
				{
					transition.Conditions.Add(new AnimationCondition());
				}

				if (ImGui.Button("Remove transition"))
				{
					node.Transitions.RemoveAt(index--);
				}
			}
			finally
			{
				ImGui.PopID();
			}
		}

		if (ImGui.Button("+ Add transition"))
		{
			node.Transitions.Add(new AnimationTransition());
		}
	}

	private static void DrawParameters(AnimationGraph graph)
	{
		InspectorLayout.DrawSpace();
		ImGui.TextUnformatted("Graph parameters");
		ImGui.TextDisabled("Inputs shared by nodes and gameplay.");
		for (var index = 0; index < graph.Parameters.Count; index++)
		{
			ImGui.PushID(index);
			try
			{
				var parameter = graph.Parameters[index];
				if (EditorUIUtility.CollapsingHeader($"{parameter.Name}###Parameter", isOpenByDefault: true) == false)
				{
					continue;
				}

				var name = parameter.Name;
				if (EditorUIUtility.InputText("Name", ref name, 128))
				{
					parameter.Name = name;
				}

				var type = parameter.Type;
				if (EditorUIUtility.EnumCombo("Type", ref type))
				{
					parameter.Type = type;
					parameter.Default = 0.0f;
				}

				if (type == AnimationParameterType.Bool)
				{
					var value = parameter.Default != 0.0f;
					if (EditorUIUtility.Checkbox("Default", ref value))
					{
						parameter.Default = value ? 1.0f : 0.0f;
					}
				}
				else if (type == AnimationParameterType.Integer)
				{
					var value = (int)parameter.Default;
					if (EditorUIUtility.InputInt("Default", ref value))
					{
						parameter.Default = value;
					}
				}
				else
				{
					var value = parameter.Default;
					if (EditorUIUtility.InputFloat("Default", ref value))
					{
						parameter.Default = value;
					}
				}

				if (ImGui.SmallButton("Remove parameter"))
				{
					graph.Parameters.RemoveAt(index--);
				}
			}
			finally
			{
				ImGui.PopID();
			}
		}

		if (ImGui.Button("+ Add parameter"))
		{
			graph.Parameters.Add(new AnimationParameter { Name = "Parameter" + graph.Parameters.Count });
		}
	}
}
