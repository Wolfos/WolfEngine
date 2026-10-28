using System.Numerics;
using ImGuiNET;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.UI;

/// <summary>Clip settings, and the selected bone's transform and keying at the playhead.</summary>
public sealed class AnimationClipInspectorWindow : EditorWindow
{
	private const string ShortenPopupId = "Shorten Clip";
	private const string NewClipPopupId = "New Animation Clip";

	private static readonly ColorRGBA UnkeyedTint = new(0.55f, 0.38f, 0.12f, 1.0f);

	private readonly AnimationClipEditorSession _session;
	private readonly IEditorProjectService _project;
	private IDisposable? _fieldEdit;
	private int _pendingLengthFrames = -1;
	private string _newClipName = AnimationClipAssetFactory.DefaultName;
	private Guid _newClipSkeleton;
	private CommittedIntField _framesPerSecondField;
	private CommittedIntField _lengthFramesField;

	public AnimationClipInspectorWindow(AnimationClipEditorSession session, IEditorProjectService project)
	{
		_session = session;
		_project = project;
	}

	public override string Name => "Clip Inspector";

	public override void Draw(EditorScene scene)
	{
		Begin();
		_session.Tick();
		AnimationClipEditorPanels.HandleFocusAndShortcuts(_session);

		DrawClipCommands();

		if (_session.HasClip == false)
		{
			EndFieldEdit();
			AnimationClipEditorPanels.DrawDiagnostic(_session);
			ImGui.TextDisabled("No clip open.");
			ImGui.End();
			return;
		}

		DrawClip();
		ImGui.Spacing();
		DrawMetadata();
		ImGui.Spacing();
		DrawBone();
		ImGui.Spacing();
		DrawKeyframe();

		if (_fieldEdit is not null && ImGui.IsAnyItemActive() == false)
		{
			EndFieldEdit();
		}

		ImGui.End();
	}

	public override void OnHidden()
	{
		EndFieldEdit();
	}

	private void DrawClip()
	{
		ImGui.SeparatorText("Clip");
		ImGui.TextUnformatted(_session.ClipName + (_session.MetadataDocument?.IsDirty == true ? " *" : string.Empty));
		AnimationClipEditorPanels.DrawDiagnostic(_session);

		if (_session.IsReadOnly)
		{
			ImGui.TextDisabled(_session.Document is null
				? "Imported motion is read-only. Curves and markers can be edited."
				: "Read-only clip.");
		}

		if (_session.Document is null &&
		    _project.TryGetAsset(_session.AssetId, out var importedAsset) &&
		    ImGui.Button("Duplicate as Editable Clip"))
		{
			EndFieldEdit();
			_session.DuplicateAsEditable(importedAsset);
		}

		if (_session.Document is not null)
		{
			DrawBindingPickers();
		}

		if (_session.CanEditMetadata)
		{
			DrawDocumentButtons();
		}

		ImGui.BeginDisabled(_session.CanEdit == false || _session.IsEditing);

		if (DrawCommittedInt("FPS", _session.FramesPerSecond, ref _framesPerSecondField, out var framesPerSecond))
		{
			_session.SetFramesPerSecond(Math.Clamp(framesPerSecond, 1, AuthoredAnimationClipFile.MaxFramesPerSecond));
		}

		if (DrawCommittedInt("Length (frames)", _session.LengthFrames, ref _lengthFramesField, out var lengthFrames))
		{
			RequestLength(Math.Max(0, lengthFrames));
		}

		ImGui.TextDisabled(AnimationClipEditorPanels.FormatTime(_session.LengthFrames, _session.FramesPerSecond));

		var loop = _session.Loop;
		if (ImGui.Checkbox("Loop", ref loop))
		{
			_session.SetLoop(loop);
		}

		ImGui.EndDisabled();
		DrawShortenPopup();
	}

	/// <summary>New and Close sit above everything else so they work with or without a clip open.</summary>
	private void DrawClipCommands()
	{
		ImGui.BeginDisabled(_project.HasOpenProject == false);
		if (ImGui.Button("New Clip..."))
		{
			_newClipName = AnimationClipAssetFactory.DefaultName;
			_newClipSkeleton = _session.BoundSkeletonId;
			ImGui.OpenPopup(NewClipPopupId);
		}

		ImGui.EndDisabled();

		if (_session.HasClip)
		{
			ImGui.SameLine();
			if (ImGui.Button("Close Clip"))
			{
				EndFieldEdit();
				_session.Close();
			}
		}

		DrawNewClipPopup();
	}

	private void DrawNewClipPopup()
	{
		if (ImGui.BeginPopupModal(NewClipPopupId, ImGuiWindowFlags.AlwaysAutoResize) == false)
		{
			return;
		}

		ImGui.InputText("Name", ref _newClipName, 128);
		DrawAssetCombo("Skeleton", ref _newClipSkeleton, AssetType.Skeleton, allowNone: true, noneLabel: "(choose later)");

		var folder = _session.HasClip && _project.TryGetAsset(_session.AssetId, out var current)
			? AnimationClipAssetFactory.GetFolder(current.RelativeSourcePath)
			: AnimationClipAssetFactory.DefaultFolder;
		ImGui.TextDisabled($"Saved in {folder}");

		if (ImGui.Button("Create"))
		{
			EndFieldEdit();
			_session.CreateClip(folder, _newClipName, _newClipSkeleton);
			ImGui.CloseCurrentPopup();
		}

		ImGui.SameLine();
		if (ImGui.Button("Cancel"))
		{
			ImGui.CloseCurrentPopup();
		}

		ImGui.EndPopup();
	}

	private void DrawBindingPickers()
	{
		ImGui.BeginDisabled(_session.CanEdit == false || _session.IsEditing);

		var skeleton = _session.Document!.Clip.Skeleton.NodeId;
		if (DrawAssetCombo("Skeleton", ref skeleton, AssetType.Skeleton, allowNone: false, noneLabel: "(none)"))
		{
			_session.SetSkeleton(skeleton);
		}

		var previewModel = _session.Document.Clip.PreviewModelId;
		if (DrawAssetCombo("Preview model", ref previewModel, AssetType.Model3D, allowNone: true, noneLabel: "(automatic)", AssetType.Prefab))
		{
			_session.SetPreviewModel(previewModel);
		}

		ImGui.EndDisabled();
	}

	/// <summary>A combo over project assets of the given types. Returns true when the choice changed.</summary>
	private bool DrawAssetCombo(string label, ref Guid value, AssetType type, bool allowNone, string noneLabel, AssetType? alsoType = null)
	{
		var current = value != Guid.Empty && _project.TryGetAsset(value, out var currentAsset)
			? DescribeAsset(currentAsset)
			: noneLabel;
		var changed = false;
		if (ImGui.BeginCombo(label, current) == false)
		{
			return false;
		}

		if (allowNone && ImGui.Selectable(noneLabel, value == Guid.Empty))
		{
			value = Guid.Empty;
			changed = true;
		}

		foreach (var asset in _project.CurrentAssetDatabase.Assets)
		{
			if (asset.Type != type && asset.Type != alsoType)
			{
				continue;
			}

			if (ImGui.Selectable($"{DescribeAsset(asset)}##{asset.Id}", asset.Id == value))
			{
				changed = asset.Id != value;
				value = asset.Id;
			}
		}

		ImGui.EndCombo();
		return changed;
	}

	private static string DescribeAsset(AssetDatabaseEntry asset) =>
		asset.IsGenerated
			? $"{asset.Name} ({asset.RelativeSourcePath})"
			: asset.RelativeSourcePath;

	private void DrawDocumentButtons()
	{
		var document = _session.MetadataDocument!;
		ImGui.BeginDisabled(document.IsDirty == false || _session.IsEditing);
		if (ImGui.Button("Save"))
		{
			_session.Save();
		}

		ImGui.EndDisabled();
		ImGui.SameLine();
		ImGui.BeginDisabled(document.CanUndo == false || _session.IsEditing);
		if (ImGui.Button("Undo"))
		{
			_session.Undo();
		}

		ImGui.EndDisabled();
		ImGui.SameLine();
		ImGui.BeginDisabled(document.CanRedo == false || _session.IsEditing);
		if (ImGui.Button("Redo"))
		{
			_session.Redo();
		}

		ImGui.EndDisabled();
	}

	/// <summary>
	/// An integer field that reports a value only once editing finishes (Enter, Tab or clicking away),
	/// so typing "24" never applies 2 on the way. ImGui's InputScalar rejects EnterReturnsTrue, so the
	/// typed value is held in <paramref name="field"/> while the field is active.
	/// </summary>
	private static bool DrawCommittedInt(string label, int currentValue, ref CommittedIntField field, out int committedValue)
	{
		if (field.Active == false)
		{
			field.Value = currentValue;
		}

		ImGui.InputInt(label, ref field.Value, 0, 0);
		field.Active = ImGui.IsItemActive();
		committedValue = field.Value;
		return ImGui.IsItemDeactivatedAfterEdit() && field.Value != currentValue;
	}

	/// <summary>Shortening deletes keys past the new end, so ask before doing that.</summary>
	private void RequestLength(int lengthFrames)
	{
		if (lengthFrames < _session.LengthFrames && _session.CountKeysBeyond(lengthFrames) > 0)
		{
			_pendingLengthFrames = lengthFrames;
			ImGui.OpenPopup(ShortenPopupId);
			return;
		}

		_session.SetLengthFrames(lengthFrames);
	}

	private void DrawShortenPopup()
	{
		if (ImGui.BeginPopupModal(ShortenPopupId, ImGuiWindowFlags.AlwaysAutoResize) == false)
		{
			return;
		}

		var deleted = _session.CountKeysBeyond(_pendingLengthFrames);
		ImGui.TextUnformatted($"Shortening the clip to {_pendingLengthFrames} frames deletes {deleted} keys past the new end.");
		if (ImGui.Button("Shorten"))
		{
			_session.SetLengthFrames(_pendingLengthFrames);
			_pendingLengthFrames = -1;
			ImGui.CloseCurrentPopup();
		}

		ImGui.SameLine();
		if (ImGui.Button("Cancel"))
		{
			_pendingLengthFrames = -1;
			ImGui.CloseCurrentPopup();
		}

		ImGui.EndPopup();
	}

	private void DrawMetadata()
	{
		if (_session.MetadataDocument is not { } document) return;

		var timeline = new AnimationClipMetadata
		{
			Curves = document.Clip.Curves,
			Markers = document.Clip.Markers
		}.Clone();
		var duration = _session.GetRuntimeClip()?.Duration ?? 0;
		var playhead = Math.Clamp(_session.PlayheadFrame / _session.FramesPerSecond, 0, duration);
		ImGui.BeginDisabled(_session.CanEditMetadata == false);
		if (AnimationClipMetadataEditor.Draw(timeline, duration, playhead))
		{
			_fieldEdit ??= _session.BeginEdit("Edit Curves and Markers");
			document.SetMetadata(timeline);
		}

		ImGui.EndDisabled();
	}

	private void DrawBone()
	{
		ImGui.SeparatorText("Bone");
		if (_session.SelectedBone is not { } bone)
		{
			ImGui.TextDisabled("Select a bone in the viewport or the Bones panel.");
			return;
		}

		ImGui.TextUnformatted(bone);
		if (_session.TryGetBoneLocal(bone, out var local) == false)
		{
			ImGui.TextDisabled("The clip's skeleton has no bone with this name.");
			return;
		}

		var unkeyed = _session.HasUnkeyedChange(bone);
		if (unkeyed)
		{
			ImGui.PushStyleColor(ImGuiCol.FrameBg, UnkeyedTint);
		}

		ImGui.BeginDisabled(_session.CanEdit == false || _session.IsPlaying);

		var position = local.Position;
		ApplyField(bone, EditorUIUtility.InputVector3("Position", ref position), current => current with { Position = position });

		var rotation = EulerAngles.ToDegrees(local.Rotation);
		ApplyField(bone, EditorUIUtility.InputVector3("Rotation (deg)", ref rotation), current => current with { Rotation = EulerAngles.FromDegrees(rotation) });

		var scale = local.Scale;
		ApplyField(bone, EditorUIUtility.InputVector3("Scale", ref scale), current => current with { Scale = scale });

		ImGui.EndDisabled();

		if (unkeyed)
		{
			ImGui.PopStyleColor();
			ImGui.TextColored(new ColorRGBA(1.0f, 0.7f, 0.3f, 1.0f), "Modified, not keyed. Moving the playhead discards it.");
		}
	}

	private void DrawKeyframe()
	{
		ImGui.SeparatorText("Keyframe");
		ImGui.BeginDisabled(_session.CanEdit == false);

		var autoKey = _session.AutoKey;
		if (ImGui.Checkbox("Auto Key", ref autoKey))
		{
			_session.AutoKey = autoKey;
		}

		ImGui.BeginDisabled(_session.SelectedBone is null || _session.IsPlaying);
		if (ImGui.Button("Set Keyframe (K)"))
		{
			_session.SetKeyOnSelectedBone();
		}

		ImGui.EndDisabled();

		ImGui.SameLine();
		ImGui.BeginDisabled(_session.HasUnkeyedChanges == false);
		if (ImGui.Button("Discard Unkeyed"))
		{
			_session.DiscardUnkeyedChanges();
		}

		ImGui.EndDisabled();
		ImGui.EndDisabled();

		if (_session.SelectedBone is { } bone && _session.Document is { } document)
		{
			var keyed = document.HasKey(bone, _session.CurrentFrame);
			ImGui.TextDisabled(keyed
				? $"Keyed on frame {_session.CurrentFrame}."
				: $"Not keyed on frame {_session.CurrentFrame}.");
		}
	}

	/// <summary>Applies a field change. Everything from the first change until no field is active is one undo step.</summary>
	private void ApplyField(string bone, bool changed, Func<BoneTransform, BoneTransform> update)
	{
		if (changed == false || _session.TryGetBoneLocal(bone, out var current) == false)
		{
			return;
		}

		_fieldEdit ??= _session.BeginEdit("Edit Bone");
		_session.SetBoneLocalTransform(bone, update(current));
	}

	private void EndFieldEdit()
	{
		_fieldEdit?.Dispose();
		_fieldEdit = null;
	}

	/// <summary>An integer field's typed value, kept between frames while the user is editing it.</summary>
	private struct CommittedIntField
	{
		public int Value;
		public bool Active;
	}
}
