using System.Numerics;
using ImGuiNET;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;
using WolfEngine.Vfx;

namespace WolfEngine.Editor.UI;

/// <summary>
/// Inspector for <see cref="VfxAsset"/>: a live preview rendered by the real renderer, the emitter list, and the
/// selected emitter's properties. Edits change the loaded asset in place, so the preview shows them at once; the
/// data asset editor saves them, which hot-reloads effects playing in the game.
/// </summary>
public sealed partial class VfxAssetEditor : IDisposable
{
	private readonly IRenderViewHost _host;
	private readonly EditorRenderViews _views;
	private readonly EditorViewportStateBus _viewUi;
	private readonly IVfxService _vfx;
	private readonly IProjectAssetPipelineService _pipeline;
	private readonly IEditorProjectService _project;
	private readonly IPropertyDrawerRegistry _propertyDrawers;
	private VfxPreviewScene? _preview;
	private VfxAsset? _previewedAsset;
	private Guid _modelId;
	private int _selectedEmitter;
	private float _orbitYaw = 0.6f;
	private float _orbitPitch = 0.45f;
	private float _orbitDistance = 6.0f;
	private AssetLinkSelectionButton? _linkButton;

	public VfxAssetEditor(
		IRenderViewHost host,
		EditorRenderViews views,
		EditorViewportStateBus viewUi,
		IVfxService vfx,
		IProjectAssetPipelineService pipeline,
		IEditorProjectService project,
		IPropertyDrawerRegistry propertyDrawers)
	{
		_host = host ?? throw new ArgumentNullException(nameof(host));
		_views = views ?? throw new ArgumentNullException(nameof(views));
		_viewUi = viewUi ?? throw new ArgumentNullException(nameof(viewUi));
		_vfx = vfx ?? throw new ArgumentNullException(nameof(vfx));
		_pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
		_project = project ?? throw new ArgumentNullException(nameof(project));
		_propertyDrawers = propertyDrawers ?? throw new ArgumentNullException(nameof(propertyDrawers));
	}

	/// <summary>The preview, once the editor has been drawn. Exposed for editor automation.</summary>
	public VfxPreviewScene? Preview => _preview;

	public int SelectedEmitter => _selectedEmitter;

	/// <summary>The asset object the preview plays.</summary>
	public VfxAsset? PreviewedAsset => _previewedAsset;

	/// <summary>Points the preview camera, as dragging and scrolling over the preview does.</summary>
	public void SetOrbit(float yaw, float pitch, float distance)
	{
		_orbitYaw = yaw;
		_orbitPitch = Math.Clamp(pitch, -0.2f, 1.45f);
		_orbitDistance = Math.Clamp(distance, 1.0f, 40.0f);
		_preview?.SetOrbit(_orbitYaw, _orbitPitch, _orbitDistance);
	}

	/// <summary>Loads a preview model by asset id and attaches the effect to its entity with that name, if any.</summary>
	public void SetPreviewModel(Guid modelId, string? attachTo)
	{
		var preview = _preview ?? throw new InvalidOperationException("The VFX preview has not been drawn yet.");
		SetModel(preview, modelId);
		if (string.IsNullOrWhiteSpace(attachTo))
		{
			return;
		}

		foreach (var entity in preview.ModelEntities)
		{
			if (string.Equals(preview.GetName(entity), attachTo, StringComparison.Ordinal))
			{
				preview.SetAttachment(entity);
				return;
			}
		}

		throw new InvalidOperationException($"The preview model has no entity named '{attachTo}'.");
	}

	/// <summary>Draws the editor for <paramref name="asset"/>. Returns true when the asset was changed.</summary>
	public bool Draw(VfxAsset asset, AssetLinkSelectionButton linkButton)
	{
		ArgumentNullException.ThrowIfNull(asset);
		_linkButton = linkButton;
		EnsurePreview(asset);

		DrawPreview(asset);
		InspectorLayout.DrawSpace(8.0f);

		var changed = DrawEmitterList(asset);
		InspectorLayout.DrawSpace(8.0f);

		if (asset.Emitters.Count == 0)
		{
			ImGui.TextDisabled("Add an emitter to start.");
			return changed;
		}

		_selectedEmitter = Math.Clamp(_selectedEmitter, 0, asset.Emitters.Count - 1);
		ImGui.PushID(_selectedEmitter);
		try
		{
			changed |= DrawEmitter(asset.Emitters[_selectedEmitter]);
		}
		finally
		{
			ImGui.PopID();
		}

		// The preview plays this same asset object, so it shows the edit on its next frame. Structural edits, such as
		// an emitter's budget or kind, restart that emitter inside the runtime.
		return changed;
	}

	private void EnsurePreview(VfxAsset asset)
	{
		if (_preview is null)
		{
			_preview = new VfxPreviewScene(_host, _views, _viewUi, _vfx);
			_preview.SetOrbit(_orbitYaw, _orbitPitch, _orbitDistance);
		}

		// The data asset editor reloads the asset after it saves, which hands out a new object.
		if (ReferenceEquals(_previewedAsset, asset) == false)
		{
			_previewedAsset = asset;
			_preview.Restart(asset);
		}
	}

	private void DrawPreview(VfxAsset asset)
	{
		var preview = _preview!;
		preview.MarkDrawn();

		if (ImGui.Button(preview.Playing ? "Pause" : "Play"))
		{
			preview.Playing = preview.Playing == false;
		}

		ImGui.SameLine();
		if (ImGui.Button("Restart"))
		{
			preview.Restart(asset);
		}

		ImGui.SameLine();
		var loop = preview.Loop;
		if (EditorUIUtility.Checkbox("Loop", ref loop))
		{
			preview.Loop = loop;
		}

		ImGui.SameLine();
		ImGui.BeginDisabled(preview.Playing);
		if (ImGui.Button("Step"))
		{
			preview.Advance(1.0f / 60.0f);
		}

		ImGui.EndDisabled();

		var speed = preview.Speed;
		if (EditorUIUtility.DrawLabeledField("Speed", () => ImGui.SliderFloat("##speed", ref speed, 0.05f, 2.0f, "%.2fx")))
		{
			preview.Speed = speed;
		}

		var motion = preview.Motion;
		EditorUIUtility.Combo("Motion", motion.ToString(), () =>
		{
			foreach (var value in Enum.GetValues<VfxPreviewMotion>())
			{
				if (ImGui.Selectable(value.ToString(), value == motion))
				{
					preview.Motion = value;
				}
			}
		});

		DrawModelPicker(preview);

		var width = ImGui.GetContentRegionAvail().X;
		if (width <= 0.0f)
		{
			return;
		}

		var size = new Vector2(width, Math.Clamp(width * 0.7f, 180.0f, 360.0f));
		var minimum = ImGui.GetCursorScreenPos();
		ImGui.Image(UiTextureIds.Viewport(preview.View), size);
		HandleOrbitInput(preview);
		var scale = ImGui.GetIO().DisplayFramebufferScale;
		_viewUi.PublishUiState(
			preview.View,
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

		var statistics = preview.Statistics;
		ImGui.PushTextWrapPos();
		ImGui.TextDisabled(
			$"{preview.Time:0.00} s, {(preview.IsEffectPlaying ? "playing" : "finished")}. " +
			$"Particles reserved: {statistics.ReservedParticles} of {statistics.ParticleCapacity}.");
		ImGui.TextDisabled("Drag to orbit, scroll to zoom. The preview runs independently of Play mode.");
		ImGui.PopTextWrapPos();
	}

	private void HandleOrbitInput(VfxPreviewScene preview)
	{
		var io = ImGui.GetIO();
		var changed = false;
		if (ImGui.IsItemHovered() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
		{
			_orbitYaw += io.MouseDelta.X * 0.01f;
			_orbitPitch = Math.Clamp(_orbitPitch + io.MouseDelta.Y * 0.01f, -0.2f, 1.45f);
			changed = true;
		}

		if (ImGui.IsItemHovered() && io.MouseWheel != 0.0f)
		{
			_orbitDistance = Math.Clamp(_orbitDistance * (1.0f - io.MouseWheel * 0.1f), 1.0f, 40.0f);
			changed = true;
		}

		if (changed)
		{
			preview.SetOrbit(_orbitYaw, _orbitPitch, _orbitDistance);
		}
	}

	private void DrawModelPicker(VfxPreviewScene preview)
	{
		var current = _project.TryGetAsset(_modelId, out var modelAsset) ? modelAsset.Name : "None";
		EditorUIUtility.Combo("Preview model", current, () =>
		{
			if (ImGui.Selectable("None", _modelId == Guid.Empty))
			{
				SetModel(preview, Guid.Empty);
			}

			foreach (var asset in _project.CurrentAssetDatabase.Assets)
			{
				if (asset.Type is AssetType.Model3D or AssetType.Prefab &&
				    ImGui.Selectable(asset.Name + "##" + asset.Id, _modelId == asset.Id))
				{
					SetModel(preview, asset.Id);
				}
			}
		});

		if (_modelId == Guid.Empty)
		{
			return;
		}

		var attachment = preview.Attachment;
		var attachedName = attachment.IsValid ? preview.GetName(attachment) : "Model root";
		EditorUIUtility.Combo("Attach to", attachedName, () =>
		{
			if (ImGui.Selectable("Model root", attachment.IsValid == false))
			{
				preview.SetAttachment(default);
			}

			var entities = preview.ModelEntities;
			for (var i = 0; i < entities.Count; i++)
			{
				var entity = entities[i];
				if (ImGui.Selectable(preview.GetName(entity) + "##" + entity.Index, entity == attachment))
				{
					preview.SetAttachment(entity);
				}
			}
		});
	}

	private void SetModel(VfxPreviewScene preview, Guid modelId)
	{
		_modelId = modelId;
		try
		{
			preview.SetModel(_pipeline, _project, modelId);
		}
		catch (Exception exception)
		{
			_modelId = Guid.Empty;
			preview.SetModel(_pipeline, _project, Guid.Empty);
			Console.WriteLine($"VFX preview could not load the model: {exception.Message}");
		}
	}

	private bool DrawEmitterList(VfxAsset asset)
	{
		var changed = false;
		ImGui.SeparatorText("Emitters");
		var emitters = asset.Emitters;
		var moveFrom = -1;
		var moveTo = -1;
		for (var i = 0; i < emitters.Count; i++)
		{
			var emitter = emitters[i];
			ImGui.PushID(i);
			try
			{
				var enabled = emitter.Enabled;
				if (ImGui.Checkbox("##enabled", ref enabled))
				{
					emitter.Enabled = enabled;
					changed = true;
				}

				ImGui.SameLine();
				var kind = emitter.Kind == VfxEmitterKind.Ribbon ? "Ribbon" : "Sprite";
				var timing = emitter.Duration <= 0.0f
					? "continuous"
					: emitter.Looping ? $"loops every {emitter.Duration:0.##} s" : $"{emitter.Duration:0.##} s";
				if (ImGui.Selectable($"{emitter.Name}   [{kind}, {timing}]", i == _selectedEmitter))
				{
					_selectedEmitter = i;
				}

				if (ImGui.BeginPopupContextItem("EmitterMenu"))
				{
					if (ImGui.MenuItem("Move up", i > 0))
					{
						moveFrom = i;
						moveTo = i - 1;
					}

					if (ImGui.MenuItem("Move down", i < emitters.Count - 1))
					{
						moveFrom = i;
						moveTo = i + 1;
					}

					ImGui.EndPopup();
				}
			}
			finally
			{
				ImGui.PopID();
			}
		}

		if (moveFrom >= 0)
		{
			(emitters[moveFrom], emitters[moveTo]) = (emitters[moveTo], emitters[moveFrom]);
			_selectedEmitter = moveTo;
			changed = true;
		}

		if (ImGui.Button("Add sprite"))
		{
			emitters.Add(new VfxEmitterDefinition
			{
				Name = "Sprites"
			});
			_selectedEmitter = emitters.Count - 1;
			changed = true;
		}

		ImGui.SameLine();
		if (ImGui.Button("Add ribbon"))
		{
			emitters.Add(new VfxEmitterDefinition
			{
				Name = "Ribbon",
				Kind = VfxEmitterKind.Ribbon,
				Duration = 0.0f,
				Blend = VfxBlendMode.Additive
			});
			_selectedEmitter = emitters.Count - 1;
			changed = true;
		}

		ImGui.BeginDisabled(emitters.Count == 0);
		if (ImGui.Button("Duplicate"))
		{
			emitters.Insert(_selectedEmitter + 1, Clone(emitters[_selectedEmitter]));
			_selectedEmitter++;
			changed = true;
		}

		ImGui.SameLine();
		if (ImGui.Button("Remove"))
		{
			emitters.RemoveAt(_selectedEmitter);
			_selectedEmitter = Math.Max(_selectedEmitter - 1, 0);
			changed = true;
		}

		ImGui.EndDisabled();

		return changed;
	}

	/// <summary>Deep copy through the asset's own JSON form, so a duplicate never shares curves with its source.</summary>
	private static VfxEmitterDefinition Clone(VfxEmitterDefinition source)
	{
		var json = System.Text.Json.JsonSerializer.Serialize(source, AssetJson.SerializerOptions);
		var copy = System.Text.Json.JsonSerializer.Deserialize<VfxEmitterDefinition>(json, AssetJson.SerializerOptions)
			?? new VfxEmitterDefinition();
		copy.Name = source.Name + " copy";

		return copy;
	}

	public void Dispose()
	{
		_preview?.Dispose();
		_preview = null;
		_previewedAsset = null;
	}
}
