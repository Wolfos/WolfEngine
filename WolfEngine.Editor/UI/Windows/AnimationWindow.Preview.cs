using System.Numerics;
using ImGuiNET;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;
using WolfEngine.Mathematics;

namespace WolfEngine.Editor.UI;

public sealed partial class AnimationWindow
{
	private void DrawPreview()
	{
		ImGui.TextDisabled("Preview runs independently of scene Play mode.");
		var model = _modelId;
		var set = _previewSet;
		var changed = false;
		var current = _project.TryGetAsset(model, out var modelAsset) ? modelAsset.Name : "None";
		EditorUIUtility.Combo("Preview model / prefab", current, () =>
		{
			if (ImGui.Selectable("None", model == Guid.Empty))
			{
				model = Guid.Empty;
				changed = true;
			}

			foreach (var asset in _project.CurrentAssetDatabase.Assets)
			{
				if (asset.Type is AssetType.Model3D or AssetType.Prefab && ImGui.Selectable(asset.Name + "##" + asset.Id, model == asset.Id))
				{
					model = asset.Id;
					changed = true;
				}
			}
		});
		changed |= DrawAssetRef("Preview clip set", ref set);
		_modelId = model;
		_previewSet = set;
		if (changed || _document?.Revision != _compiledRevision)
		{
			TryCompilePreview();
		}

		if (_preview is null)
		{
			ImGui.TextWrapped("Choose a model or prefab with an Animator and its clip set to preview this graph.");
			return;
		}

		var instance = _preview.Instance;
		if (ImGui.Button(instance.Playing ? "Pause" : "Play"))
		{
			instance.Playing = instance.Playing == false;
		}

		ImGui.SameLine();
		if (ImGui.Button("Restart"))
		{
			_preview.Seek(0.0f);
		}

		ImGui.SameLine();
		if (ImGui.Button("Step frame"))
		{
			_preview.Step(1.0f / 30.0f);
		}

		var speed = instance.Speed;
		if (EditorUIUtility.InputFloat("Preview speed", ref speed))
		{
			instance.Speed = speed;
		}

		_scrub = instance.Time;
		if (ImGui.SliderFloat("##Scrub", ref _scrub, 0.0f, 10.0f, "%.2f seconds"))
		{
			instance.Playing = false;
			_preview.Seek(_scrub);
		}

		var width = MathF.Min(512.0f, ImGui.GetContentRegionAvail().X);
		if (width <= 0.0f)
		{
			return;
		}

		var size = new Vector2(width, Math.Clamp(width * 0.8f, 160.0f, 300.0f));
		var minimum = ImGui.GetCursorScreenPos();
		ImGui.Image(UiTextureIds.Viewport(_preview.View), size);
		var scale = ImGui.GetIO().DisplayFramebufferScale;
		_viewUi.PublishUiState(
			_preview.View,
			new SceneViewportUiState(
			ImGui.IsWindowCollapsed() == false && ImGui.IsItemVisible(),
			new Int2((int)(size.X * scale.X), (int)(size.Y * scale.Y)),
			1,
			SceneDebugViewIds.FinalColor,
			ImGui.IsItemHovered(),
			ImGui.IsWindowFocused(),
			false,
			false,
			false,
			minimum,
			minimum + size));
		if (EditorUIUtility.CollapsingHeader("Parameters and evaluation"))
		{
			DrawInstance(instance, false);
		}
	}

	public bool TryCompilePreview()
	{
		_compiledRevision = _document?.Revision ?? -1;
		if (_modelId == Guid.Empty || _document is null)
		{
			DisposePreview();
			_diagnostic = null;
			return false;
		}

		try
		{
			var graph = (AnimationGraph)_document.Asset;
			var clips = _previewSet.IsValid ? _previewSet.Asset : null;
			object? Resolve(Guid id, Type type) => id == _assetId && type == typeof(AnimationGraph) ? graph : Animator.Resolve(id, type);
			var preview = new AnimationPreviewScene(_host, _views, _viewUi, _pipeline, _project, _modelId, graph, clips, Resolve);
			var previous = _preview?.Instance;
			if (previous is not null)
			{
				foreach (var parameter in preview.Instance.Program.ParameterSchema)
				{
					var oldParameter = previous.Program.ParameterSchema.FirstOrDefault(p => p.Name == parameter.Name && p.Type == parameter.Type);
					if (oldParameter is null)
					{
						continue;
					}

					var oldHandle = previous.Program.GetParameter(parameter.Name, parameter.Type);
					var handle = preview.Instance.Program.GetParameter(parameter.Name, parameter.Type);
					if (parameter.Type == AnimationParameterType.Integer)
					{
						preview.Instance.SetInteger(handle, previous.GetInteger(oldHandle));
					}
					else if (parameter.Type == AnimationParameterType.Bool)
					{
						preview.Instance.SetBool(handle, previous.GetParameter(oldHandle) != 0);
					}
					else
					{
						preview.Instance.SetFloat(handle, previous.GetParameter(oldHandle));
					}
				}

				preview.Instance.Speed = previous.Speed;
				preview.Seek(previous.Time);
				preview.Instance.Playing = previous.Playing;
			}

			DisposePreview();
			_preview = preview;
			_diagnostic = null;
			return true;
		}
		catch (Exception exception)
		{
			_diagnostic = exception.Message;
			return false;
		}
	}

	public void ConfigurePreview(Guid assetId, Guid modelId, Guid setId)
	{
		if ((_project.TryGetAsset(assetId, out var asset)) == false)
		{
			throw new InvalidOperationException("Animation asset is missing.");
		}

		Open(asset);
		_modelId = modelId;
		_previewSet = new AssetRef<AnimationSet>
		{
			NodeId = setId
		};
		if ((TryCompilePreview()) == false)
		{
			throw new InvalidOperationException(_diagnostic ?? "Preview could not compile.");
		}

		_requestedTab = SidebarTab.Preview;
	}

	public void EditGraph(string operation, Guid nodeId, int inputIndex, Guid inputId)
	{
		if (_document?.Asset is not AnimationGraph graph)
		{
			throw new InvalidOperationException("Open an animation graph first.");
		}

		if (_project.IsAssetReadOnly(_assetId))
		{
			throw new InvalidOperationException("Asset is read-only.");
		}

		CommitGesture();
		if (operation == "undo")
		{
			_document.Undo();
		}
		else if (operation == "redo")
		{
			_document.Redo();
		}
		else if (operation == "connect")
		{
			var node = graph.Nodes.Single(n => n.Id == nodeId);
			if (inputIndex < 0 || inputIndex >= node.Inputs.Count)
			{
				throw new ArgumentOutOfRangeException(nameof(inputIndex));
			}

			_document.Edit(() => node.Inputs[inputIndex] = inputId);
		}
		else
		{
			throw new ArgumentException("Expected connect, undo or redo.", nameof(operation));
		}

		TryCompilePreview();
	}

	public void PreviewTransport(bool playing, float? seek, float? step)
	{
		if (_preview is null)
		{
			throw new InvalidOperationException("No animation preview is open.");
		}

		_preview.Instance.Playing = playing;
		if (seek is { } time)
		{
			_preview.Seek(time);
		}

		if (step is { } delta)
		{
			_preview.Step(delta);
		}
	}

	private void DrawLive(EditorScene scene)
	{
		if (ImGui.BeginCombo(
			"Animator",
			_liveEntity.IsValid && scene.World.HasComponent<NameComponent>(_liveEntity) ? scene.World.GetComponent<NameComponent>(_liveEntity).Name : "None"))
		{
			foreach (var entry in scene.World.View<Animator>())
			{
				var name = scene.World.HasComponent<NameComponent>(entry.Entity) ? scene.World.GetComponent<NameComponent>(entry.Entity).Name : entry.Entity.ToString();
				if (ImGui.Selectable(name))
				{
					_liveEntity = entry.Entity;
				}
			}

			ImGui.EndCombo();
		}

		if (_liveEntity.IsValid && scene.World.HasComponent<Animator>(_liveEntity))
		{
			ref var animator = ref scene.World.GetComponent<Animator>(_liveEntity);
			if (animator.Diagnostic is not null)
			{
				ImGui.TextWrapped(animator.Diagnostic);
			}

			if (animator.GraphInstance is { } instance)
			{
				DrawInstance(instance, true);
			}
		}
	}

	private static void DrawInstance(AnimationGraphInstance instance, bool readOnly)
	{
		ImGui.PushID(readOnly ? "Live animator parameters" : "Preview parameters");
		ImGui.BeginDisabled(readOnly);
		foreach (var parameter in instance.Program.ParameterSchema)
		{
			var handle = instance.Program.GetParameter(parameter.Name, parameter.Type);
			var value = instance.GetParameter(handle);
			switch (parameter.Type)
			{
				case AnimationParameterType.Float:
					if (EditorUIUtility.InputFloat(parameter.Name, ref value))
					{
						instance.SetFloat(handle, value);
					}

					break;
				case AnimationParameterType.Bool:
					var boolean = value != 0;
					if (EditorUIUtility.Checkbox(parameter.Name, ref boolean))
					{
						instance.SetBool(handle, boolean);
					}

					break;
				case AnimationParameterType.Integer:
					var integer = (int)value;
					if (EditorUIUtility.InputInt(parameter.Name, ref integer))
					{
						instance.SetInteger(handle, integer);
					}

					break;
			}
		}

		ImGui.EndDisabled();
		foreach (var contribution in instance.Contributions)
		{
			ImGui.TextUnformatted($"{contribution.ClipSlot}: weight {contribution.Weight:F2}, phase {contribution.NormalizedTime:F2}");
		}

		foreach (var state in instance.States)
		{
			ImGui.TextUnformatted($"State {state.State}, blend {state.BlendRemaining:F2}s, reason: {state.Reason}");
		}

		foreach (var curve in instance.Program.Curves)
		{
			ImGui.TextUnformatted($"{curve}: {instance.GetCurve(instance.Program.GetCurve(curve)):F3}");
		}

		foreach (var marker in instance.Markers)
		{
			ImGui.TextUnformatted($"Marker: {marker.Name} @ {marker.ClipTime:F2}s");
		}

		ImGui.PopID();
	}
}
