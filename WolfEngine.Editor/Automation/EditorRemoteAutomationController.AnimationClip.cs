using System.Numerics;
using System.Text.Json;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.UI;

namespace WolfEngine.Editor.Automation;

/// <summary>Clip editor automation. Every call runs on the editor thread and returns the editor's state afterwards.</summary>
public sealed partial class EditorRemoteAutomationController
{
	public Task<AnimationClipEditorStateResult> OpenClipEditorAsync(Guid assetId, bool discardUnsavedChanges, CancellationToken token) =>
		Enqueue(() =>
		{
			var asset = GetAsset(assetId);
			if (asset.Type != AssetType.AnimationClip)
			{
				throw new InvalidOperationException($"Asset '{assetId}' is a {asset.Type}, not an animation clip.");
			}

			_clipEditor.Open(asset, discardUnsavedChanges ? UnsavedChangesPolicy.Discard : UnsavedChangesPolicy.Prompt);
			return InspectClipEditor();
		}, token);

	public Task<AnimationClipEditorStateResult> CreateClipAsync(string folder, string name, Guid skeletonId, CancellationToken token) =>
		Enqueue(() =>
		{
			_clipEditor.CreateClip(folder, name, skeletonId, UnsavedChangesPolicy.Discard);
			return InspectClipEditor();
		}, token);

	public Task<AnimationClipEditorStateResult> DuplicateClipAsync(Guid assetId, CancellationToken token) =>
		Enqueue(() =>
		{
			_clipEditor.DuplicateAsEditable(GetAsset(assetId), UnsavedChangesPolicy.Discard);
			return InspectClipEditor();
		}, token);

	public Task<AnimationClipEditorStateResult> SelectClipBoneAsync(string? bone, CancellationToken token) =>
		Enqueue(() =>
		{
			RequireClip();
			if (bone is not null && _clipEditor.Skeleton?.TryGetBoneIndex(bone, out _) != true)
			{
				throw new InvalidOperationException($"The clip's skeleton has no bone '{bone}'.");
			}

			_clipEditor.SelectBone(bone);
			return InspectClipEditor();
		}, token);

	/// <summary>Edits a bone the way the inspector does: an unkeyed override, or a key with Auto Key on. Omitted parts keep their value.</summary>
	public Task<AnimationClipEditorStateResult> SetClipBoneLocalTransformAsync(
		string bone,
		Vector3? position,
		Vector3? rotationDegrees,
		Vector3? scale,
		CancellationToken token) =>
		Enqueue(() =>
		{
			RequireEditableClip();
			if (_clipEditor.TryGetBoneLocal(bone, out var local) == false)
			{
				throw new InvalidOperationException($"The clip's skeleton has no bone '{bone}'.");
			}

			if (position is { } newPosition)
			{
				local.Position = newPosition;
			}

			if (rotationDegrees is { } newRotation)
			{
				local.Rotation = EulerAngles.FromDegrees(newRotation);
			}

			if (scale is { } newScale)
			{
				local.Scale = newScale;
			}

			_clipEditor.SetBoneLocalTransform(bone, local);
			return InspectClipEditor();
		}, token);

	public Task<AnimationClipEditorStateResult> SetClipPlayheadAsync(int frame, CancellationToken token) =>
		Enqueue(() =>
		{
			RequireClip();
			_clipEditor.SetPlayhead(frame);
			return InspectClipEditor();
		}, token);

	public Task<AnimationClipEditorStateResult> SetClipAutoKeyAsync(bool enabled, CancellationToken token) =>
		Enqueue(() =>
		{
			_clipEditor.AutoKey = enabled;
			return InspectClipEditor();
		}, token);

	/// <summary>Keys a bone, or the selected bone when <paramref name="bone"/> is null, at the playhead.</summary>
	public Task<AnimationClipEditorStateResult> SetClipKeyAsync(string? bone, CancellationToken token) =>
		Enqueue(() =>
		{
			RequireEditableClip();
			var target = bone ?? _clipEditor.SelectedBone
				?? throw new InvalidOperationException("Name a bone or select one first.");
			if (_clipEditor.SetKey(target) == false)
			{
				throw new InvalidOperationException($"Could not key '{target}'.");
			}

			return InspectClipEditor();
		}, token);

	/// <summary>
	/// Selects keys as "bone@frame" references and applies a dopesheet operation to them: select,
	/// move, duplicate or delete. Move and duplicate shift by <paramref name="frameDelta"/>.
	/// </summary>
	public Task<AnimationClipEditorStateResult> EditClipKeysAsync(
		string operation,
		IReadOnlyList<string> keys,
		int frameDelta,
		CancellationToken token) =>
		Enqueue(() =>
		{
			RequireClip();
			_clipEditor.SelectKeys(keys.Select(ParseKeyRef), KeySelectionMode.Replace);

			var applied = operation switch
			{
				"select" => true,
				"move" => _clipEditor.MoveSelectedKeys(frameDelta),
				"duplicate" => _clipEditor.DuplicateSelectedKeys(frameDelta),
				"delete" => _clipEditor.DeleteSelectedKeys(),
				_ => throw new InvalidOperationException($"Unknown key operation '{operation}'. Use select, move, duplicate or delete.")
			};
			if (applied == false)
			{
				throw new InvalidOperationException($"The {operation} did not change any keys.");
			}

			return InspectClipEditor();
		}, token);

	public Task<AnimationClipEditorStateResult> SetClipMetadataAsync(string metadataJson, CancellationToken token) =>
		Enqueue(() =>
		{
			RequireEditableClipMetadata();
			var metadata = JsonSerializer.Deserialize<AnimationClipMetadata>(metadataJson, AnimationAssetJson.Options)
				?? throw new InvalidOperationException("Clip metadata cannot be empty.");
			metadata.Validate(_clipEditor.GetRuntimeClip()!.Duration);
			_clipEditor.MetadataDocument!.SetMetadata(metadata);
			return InspectClipEditor();
		}, token);

	public Task<AnimationClipEditorStateResult> ClipHistoryAsync(string operation, CancellationToken token) =>
		Enqueue(() =>
		{
			RequireEditableClipMetadata();
			var applied = operation switch
			{
				"undo" => _clipEditor.Undo(),
				"redo" => _clipEditor.Redo(),
				_ => throw new InvalidOperationException($"Unknown history operation '{operation}'. Use undo or redo.")
			};
			if (applied == false)
			{
				throw new InvalidOperationException($"Nothing to {operation}.");
			}

			return InspectClipEditor();
		}, token);

	public Task<AnimationClipEditorStateResult> SaveClipAsync(CancellationToken token) =>
		Enqueue(() =>
		{
			RequireEditableClipMetadata();
			if (_clipEditor.Save() == false)
			{
				throw new InvalidOperationException(_clipEditor.Diagnostic ?? "The clip could not be saved.");
			}

			return InspectClipEditor();
		}, token);

	public Task<AnimationClipEditorStateResult> GetClipEditorStateAsync(CancellationToken token) =>
		Enqueue(InspectClipEditor, token);

	/// <summary>Captures the clip preview render view without editor UI. The Clip Viewport must be visible.</summary>
	public async Task<FrameCaptureResult> CaptureClipPreviewAsync(string path, CancellationToken token)
	{
		var view = await Enqueue(
			() => _clipEditor.Preview?.View ?? throw new InvalidOperationException("No clip preview is showing. Open a clip with the Clip Viewport visible."),
			token);
		var previous = _renderGraph.SceneCaptureView;
		_renderGraph.SetSceneCaptureView(view);
		try
		{
			return await CaptureFrameAsync(path, token);
		}
		finally
		{
			_renderGraph.SetSceneCaptureView(previous);
		}
	}

	private AnimationClipEditorStateResult InspectClipEditor()
	{
		var session = _clipEditor;
		AnimationClipBoneTransformResult? selectedLocal = null;
		if (session.SelectedBone is { } bone && session.TryGetBoneLocal(bone, out var local))
		{
			var euler = EulerAngles.ToDegrees(local.Rotation);
			selectedLocal = new AnimationClipBoneTransformResult(
				[local.Position.X, local.Position.Y, local.Position.Z],
				[euler.X, euler.Y, euler.Z],
				[local.Scale.X, local.Scale.Y, local.Scale.Z]);
		}

		var unkeyed = new List<string>();
		if (session.Skeleton is { } skeleton)
		{
			foreach (var name in skeleton.BoneNames)
			{
				if (session.HasUnkeyedChange(name))
				{
					unkeyed.Add(name);
				}
			}
		}

		var document = session.MetadataDocument;
		return new AnimationClipEditorStateResult(
			session.HasClip ? session.AssetId.ToString("D") : null,
			session.ClipName,
			session.Diagnostic,
			session.IsReadOnly,
			document?.IsDirty == true,
			session.IsPromptPending,
			session.FramesPerSecond,
			session.LengthFrames,
			session.Loop,
			session.PlayheadFrame,
			session.IsPlaying,
			session.AutoKey,
			session.BoundSkeletonId == Guid.Empty ? null : session.BoundSkeletonId.ToString("D"),
			session.Skeleton?.BoneCount ?? 0,
			session.PreviewModelId == Guid.Empty ? null : session.PreviewModelId.ToString("D"),
			session.SelectedBone,
			selectedLocal,
			session.HasClip ? session.GetBoneKeyFrames() : new Dictionary<string, int[]>(),
			unkeyed,
			session.SelectedKeys.Count,
			document?.CanUndo == true,
			document?.CanRedo == true,
			session.Preview is { } preview ? HashPose(preview.Pose) : 0,
			session.CanEditMetadata,
			document?.Clip.Curves.Select(curve => curve.Clone()).ToArray() ?? [],
			document?.Clip.Markers.Select(marker => marker.Clone()).ToArray() ?? []);
	}

	private AssetDatabaseEntry GetAsset(Guid assetId)
	{
		if (_projectService.TryGetAsset(assetId, out var asset) == false)
		{
			throw new InvalidOperationException($"Asset '{assetId}' was not found.");
		}

		return asset;
	}

	private void RequireClip()
	{
		if (_clipEditor.HasClip == false)
		{
			throw new InvalidOperationException("No clip is open in the clip editor.");
		}
	}

	private void RequireEditableClipMetadata()
	{
		RequireClip();
		if (_clipEditor.CanEditMetadata == false)
		{
			throw new InvalidOperationException("The open clip's curves and markers are read-only.");
		}
	}

	private void RequireEditableClip()
	{
		RequireClip();
		if (_clipEditor.CanEdit == false)
		{
			throw new InvalidOperationException("The open clip is read-only. Duplicate it to edit.");
		}
	}

	private static AnimationKeyRef ParseKeyRef(string key)
	{
		var separator = key.LastIndexOf('@');
		if (separator <= 0 || int.TryParse(key[(separator + 1)..], out var frame) == false)
		{
			throw new InvalidOperationException($"'{key}' is not a key reference. Use bone@frame, for example upper_arm_r@12.");
		}

		return new AnimationKeyRef(key[..separator], frame);
	}
}
