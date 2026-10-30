using ImGuiNET;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

/// <summary>
/// State shared by every clip editor panel: the open clip, the selected bone, the playhead, unkeyed
/// edits and the preview. Panels read and drive this; none of them own clip state themselves.
/// <para>
/// Authored clips open as an <see cref="AnimationClipDocument"/>. Imported clips open read-only
/// for motion and are sampled from their runtime asset. Their annotations open in a separate
/// metadata document backed by the model source metadata.
/// </para>
/// <para>
/// A bone edit with Auto Key off is an unkeyed override: it shows in the preview but is not part of
/// the clip until keyed, and moving the playhead or starting playback discards it.
/// </para>
/// </summary>
public sealed class AnimationClipEditorSession : IEditorDocumentEditor, IBoneEditSink, IDisposable
{
	private readonly IEditorProjectService _project;
	private readonly IProjectAssetPipelineService _pipeline;
	private readonly IEditorWorkspaceService _workspaces;
	private readonly IAssetSelectionService _selection;
	private readonly IRenderViewHost _host;
	private readonly EditorRenderViews _views;
	private readonly EditorViewportStateBus _viewUi;

	// Documents stay open per asset so switching clips never drops unsaved edits.
	private readonly Dictionary<Guid, AnimationClipDocument> _documents = new();

	// Local transforms that differ from what the clip samples to at the playhead.
	private readonly Dictionary<string, BoneTransform> _overrides = new(StringComparer.Ordinal);

	// Overrides that only mirror a key written during the current edit; they go when the edit ends.
	private readonly HashSet<string> _keyedOverrides = new(StringComparer.Ordinal);

	private readonly HashSet<AnimationKeyRef> _selectedKeys = new();

	private AnimationClip? _importedClip;
	private AnimationClipDocument? _importedMetadataDocument;
	private Dictionary<string, int[]>? _boneKeyFrames;
	private long _boneKeyFramesRevision = -1;
	private AnimationClip? _compiledClip;
	private long _compiledRevision = -1;
	private AnimationClipPreviewScene? _preview;
	private Pose? _scratchPose;
	private PoseKey _appliedPose;
	private IDisposable? _documentTransaction;
	private int _editDepth;
	private int _overrideRevision;
	private Guid? _lastSeenSelection;
	private int _lastTickFrame = -1;
	private int _focusedFrame = int.MinValue;
	private string? _projectPath;
	private AssetDatabaseEntry? _asset;
	private Guid _boundSkeletonId;
	private Guid _boundPreviewModelId;
	private PendingAction _pending;
	private bool _openPrompt;

	public AnimationClipEditorSession(
		IEditorProjectService project,
		IProjectAssetPipelineService pipeline,
		IEditorWorkspaceService workspaces,
		IAssetSelectionService selection,
		IRenderViewHost host,
		EditorRenderViews views,
		EditorViewportStateBus viewUi)
	{
		_project = project;
		_pipeline = pipeline;
		_workspaces = workspaces;
		_selection = selection;
		_host = host;
		_views = views;
		_viewUi = viewUi;
	}

	public bool HasClip => AssetId != Guid.Empty;
	public Guid AssetId { get; private set; }
	public string ClipName { get; private set; } = string.Empty;

	/// <summary>The editable clip, or null when the open clip is imported.</summary>
	public AnimationClipDocument? Document { get; private set; }

	/// <summary>True for imported clips and for clips on read-only mounts.</summary>
	public bool IsReadOnly { get; private set; }

	public bool CanEdit => Document is not null && IsReadOnly == false;

	/// <summary>The clip's annotations are editable even when its imported motion is read-only.</summary>
	public AnimationClipDocument? MetadataDocument => Document ?? _importedMetadataDocument;
	public bool CanEditMetadata => MetadataDocument is not null && _project.IsAssetReadOnly(AssetId) == false;

	public Skeleton? Skeleton { get; private set; }
	public Guid PreviewModelId { get; private set; }
	public string? Diagnostic { get; private set; }

	public string? SelectedBone { get; private set; }

	/// <summary>Increments whenever the selection changes, so panels can reveal it once.</summary>
	public int SelectionRevision { get; private set; }

	/// <summary>Playhead in frames. Fractional while playing, whole while scrubbing.</summary>
	public float PlayheadFrame { get; private set; }

	/// <summary>The whole frame that keys are written to.</summary>
	public int CurrentFrame => Math.Clamp((int)MathF.Round(PlayheadFrame), 0, Math.Max(0, LengthFrames));

	public bool IsPlaying { get; private set; }
	public float PlaybackSpeed { get; set; } = 1.0f;

	/// <summary>When on, every bone edit writes a key at the playhead instead of an unkeyed override.</summary>
	public bool AutoKey { get; set; }

	public bool IsEditing => _editDepth > 0;
	public bool HasUnkeyedChanges => _overrides.Count > _keyedOverrides.Count;

	public int FramesPerSecond => Document?.Clip.FramesPerSecond ?? ImportedFramesPerSecond;
	public int LengthFrames => Document?.Clip.LengthFrames ?? ImportedLengthFrames;
	public bool Loop => Document?.Clip.Loop ?? _importedClip?.Loop ?? false;

	public AnimationClipPreviewScene? Preview => _preview;

	/// <summary>Keys selected in the dopesheet. Keys that no longer exist, for example after undo, drop out.</summary>
	public IReadOnlySet<AnimationKeyRef> SelectedKeys
	{
		get
		{
			GetBoneKeyFrames();
			return _selectedKeys;
		}
	}

	/// <inheritdoc />
	public bool HasShortcutFocus => HasClip && _focusedFrame >= ImGui.GetFrameCount() - 1;

	private int ImportedFramesPerSecond =>
		_importedClip is null ? AuthoredAnimationClipFile.DefaultFramesPerSecond : Math.Max(1, (int)MathF.Round(_importedClip.FramesPerSecond));

	private int ImportedLengthFrames =>
		_importedClip is null ? 0 : (int)MathF.Round(_importedClip.Duration * ImportedFramesPerSecond);

	public static bool IsAuthoredClip(AssetDatabaseEntry asset) =>
		AuthoredAnimationClipJson.IsAuthoredClipPath(asset.RelativeSourcePath);

	/// <summary>True while the unsaved-changes prompt is waiting for an answer.</summary>
	public bool IsPromptPending => _pending.Kind != PendingActionKind.None;

	/// <summary>The skeleton the open clip is bound to, for pre-filling new clips.</summary>
	public Guid BoundSkeletonId => _boundSkeletonId;

	/// <summary>
	/// Opens a clip asset and brings up the Animate workspace. If the clip open now has unsaved edits,
	/// <paramref name="unsavedChanges"/> decides what happens to them; by default the user is asked,
	/// and this returns false until they answer.
	/// </summary>
	public bool Open(AssetDatabaseEntry asset, UnsavedChangesPolicy unsavedChanges = UnsavedChangesPolicy.Prompt)
	{
		ArgumentNullException.ThrowIfNull(asset);
		CheckProject();

		if (asset.Type != AssetType.AnimationClip)
		{
			return false;
		}

		// Only a later change of asset selection should open a clip. Recording the opened clip instead
		// would make a still-selected source, such as the imported clip just duplicated, look like a new
		// selection and reopen it on the next frame.
		_lastSeenSelection = _selection.SelectedAssetId;
		ShowWorkspace();

		if (asset.Id == AssetId)
		{
			return true;
		}

		if (ResolveUnsavedChanges(unsavedChanges, new PendingAction(PendingActionKind.Open, asset)) == false)
		{
			return false;
		}

		ResetState();
		try
		{
			LoadClip(asset);
			return true;
		}
		catch (Exception exception)
		{
			ResetState();
			Diagnostic = $"Could not open '{asset.Name}': {exception.Message}";
			return false;
		}
	}

	/// <summary>Closes the open clip, asking about unsaved edits first by default.</summary>
	public bool Close(UnsavedChangesPolicy unsavedChanges = UnsavedChangesPolicy.Prompt)
	{
		if (HasClip == false)
		{
			return true;
		}

		if (ResolveUnsavedChanges(unsavedChanges, new PendingAction(PendingActionKind.Close, null)) == false)
		{
			return false;
		}

		ResetState();
		return true;
	}

	/// <summary>
	/// Creates an authored clip in a folder, bound to a skeleton, and opens it. The skeleton may be
	/// empty and chosen later in the inspector.
	/// </summary>
	public bool CreateClip(string folder, string name, Guid skeletonId, UnsavedChangesPolicy unsavedChanges = UnsavedChangesPolicy.Prompt)
	{
		try
		{
			CheckProject();
			var path = AnimationClipAssetFactory.GetUniquePath(_project, folder, name);
			var clip = new AuthoredAnimationClipFile
			{
				Skeleton = new AssetRef<Skeleton>
				{
					NodeId = skeletonId
				}
			};
			var asset = AnimationClipAssetFactory.Create(_project, _pipeline, path, clip);
			return Open(asset, unsavedChanges);
		}
		catch (Exception exception)
		{
			Diagnostic = $"Could not create the clip: {exception.Message}";
			return false;
		}
	}

	/// <summary>
	/// Copies an imported clip into a new editable clip next to its source file and opens it. Keys
	/// that do not sit on frames, or that use tangents, are resampled to one key per frame.
	/// </summary>
	public bool DuplicateAsEditable(AssetDatabaseEntry importedClip, UnsavedChangesPolicy unsavedChanges = UnsavedChangesPolicy.Prompt)
	{
		ArgumentNullException.ThrowIfNull(importedClip);

		try
		{
			CheckProject();
			var runtime = (importedClip.Id == AssetId
				? GetRuntimeClip()
				: AssetDatabase.GetInstance<AnimationClip>(importedClip.Id))
				?? throw new InvalidOperationException("The imported clip could not be loaded.");

			var clip = AuthoredAnimationClipFile.FromClip(runtime, Math.Max(1, (int)MathF.Round(runtime.FramesPerSecond)));
			if (AnimationClipBindingResolver.TryResolve(
				    importedClip,
				    null,
				    _project.CurrentAssetDatabase.Assets,
				    _pipeline.GetDependencies(_project.ProjectRootPath!),
				    out var binding,
				    out _))
			{
				clip.Skeleton = new AssetRef<Skeleton>
				{
					NodeId = binding.SkeletonId
				};
				clip.PreviewModelId = binding.PreviewModelId;
			}

			var folder = AnimationClipAssetFactory.GetFolder(importedClip.RelativeSourcePath);
			var path = AnimationClipAssetFactory.GetUniquePath(_project, folder, importedClip.Name);
			var asset = AnimationClipAssetFactory.Create(_project, _pipeline, path, clip);
			return Open(asset, unsavedChanges);
		}
		catch (Exception exception)
		{
			Diagnostic = $"Could not duplicate '{importedClip.Name}': {exception.Message}";
			return false;
		}
	}

	/// <inheritdoc />
	public void DrawDialogs()
	{
		const string popupId = "Unsaved Clip Changes";
		if (_openPrompt)
		{
			ImGui.OpenPopup(popupId);
			_openPrompt = false;
		}

		var isOpen = true;
		if (ImGui.BeginPopupModal(popupId, ref isOpen, ImGuiWindowFlags.AlwaysAutoResize) == false)
		{
			if (isOpen == false)
			{
				_pending = default;
			}

			return;
		}

		var action = _pending.Kind == PendingActionKind.Open
			? $"opening '{_pending.Asset?.Name}'"
			: "closing it";
		ImGui.TextUnformatted($"'{ClipName}' has unsaved changes. Save them before {action}?");

		if (ImGui.Button("Save"))
		{
			ResolvePending(UnsavedChangesPolicy.Save);
			ImGui.CloseCurrentPopup();
		}

		ImGui.SameLine();
		if (ImGui.Button("Discard"))
		{
			ResolvePending(UnsavedChangesPolicy.Discard);
			ImGui.CloseCurrentPopup();
		}

		ImGui.SameLine();
		if (ImGui.Button("Cancel"))
		{
			_pending = default;
			ImGui.CloseCurrentPopup();
		}

		ImGui.EndPopup();
	}

	/// <summary>Carries out the action the prompt was shown for, using the user's answer.</summary>
	internal void ResolvePending(UnsavedChangesPolicy answer)
	{
		var pending = _pending;
		_pending = default;
		switch (pending.Kind)
		{
			case PendingActionKind.Open:
				Open(pending.Asset!, answer);
				break;

			case PendingActionKind.Close:
				Close(answer);
				break;
		}
	}

	public bool SetSkeleton(Guid skeletonId)
	{
		if (CanEdit == false || IsEditing)
		{
			return false;
		}

		Document!.SetSkeleton(skeletonId);
		SyncBinding();
		return true;
	}

	/// <summary>Sets the model the clip is previewed on; empty picks one that uses the skeleton.</summary>
	public bool SetPreviewModel(Guid modelId)
	{
		if (CanEdit == false || IsEditing)
		{
			return false;
		}

		Document!.SetPreviewModel(modelId);
		SyncBinding();
		return true;
	}

	private void ResetState()
	{
		EndAllEdits();
		DisposePreview();
		AssetId = Guid.Empty;
		ClipName = string.Empty;
		Document = null;
		_importedMetadataDocument = null;
		IsReadOnly = false;
		Skeleton = null;
		PreviewModelId = Guid.Empty;
		Diagnostic = null;
		_asset = null;
		_boundSkeletonId = Guid.Empty;
		_boundPreviewModelId = Guid.Empty;
		_importedClip = null;
		_boneKeyFrames = null;
		_boneKeyFramesRevision = -1;
		_selectedKeys.Clear();
		_compiledClip = null;
		_compiledRevision = -1;
		_scratchPose = null;
		_appliedPose = default;
		PlayheadFrame = 0.0f;
		IsPlaying = false;
		DiscardUnkeyedChanges();
		SelectBone(null);
	}

	/// <summary>Panels call this while they have keyboard focus, so document shortcuts reach this editor.</summary>
	public void MarkFocused()
	{
		_focusedFrame = ImGui.GetFrameCount();
	}

	public void SelectBone(string? bone)
	{
		if (string.Equals(SelectedBone, bone, StringComparison.Ordinal))
		{
			return;
		}

		SelectedBone = bone;
		SelectionRevision++;
	}

	public void Play()
	{
		if (HasClip == false || LengthFrames <= 0)
		{
			return;
		}

		DiscardUnkeyedChanges();
		if (Loop == false && PlayheadFrame >= LengthFrames)
		{
			PlayheadFrame = 0.0f;
		}

		IsPlaying = true;
	}

	public void Pause()
	{
		IsPlaying = false;
		PlayheadFrame = CurrentFrame;
	}

	public void TogglePlayback()
	{
		if (IsPlaying)
		{
			Pause();
		}
		else
		{
			Play();
		}
	}

	public void Stop()
	{
		IsPlaying = false;
		SetPlayheadInternal(0);
	}

	/// <summary>Moves the playhead to a frame, stopping playback. Unkeyed edits are discarded if the frame changes.</summary>
	public void SetPlayhead(int frame)
	{
		IsPlaying = false;
		SetPlayheadInternal(frame);
	}

	/// <summary>
	/// Advances playback and follows asset selection. Safe to call from several panels: it only runs
	/// once per UI frame.
	/// </summary>
	public void Tick()
	{
		var frame = ImGui.GetFrameCount();
		if (frame == _lastTickFrame)
		{
			return;
		}

		_lastTickFrame = frame;
		CheckProject();
		TryFollowAssetLocation();
		SyncBinding();
		FollowAssetSelection();
		Advance(ImGui.GetIO().DeltaTime);
	}

	/// <summary>Advances the playhead by real time while playing.</summary>
	internal void Advance(float deltaSeconds)
	{
		if (IsPlaying == false || HasClip == false)
		{
			return;
		}

		var length = LengthFrames;
		if (length <= 0)
		{
			IsPlaying = false;
			PlayheadFrame = 0.0f;
			return;
		}

		var next = PlayheadFrame + deltaSeconds * PlaybackSpeed * FramesPerSecond;
		if (Loop)
		{
			next %= length;
			if (next < 0.0f)
			{
				next += length;
			}
		}
		else if (next >= length)
		{
			next = length;
			IsPlaying = false;
		}

		PlayheadFrame = next;
	}

	public bool HasUnkeyedChange(string bone) =>
		_overrides.ContainsKey(bone) && _keyedOverrides.Contains(bone) == false;

	/// <summary>The bone's local transform as shown: an unkeyed edit if there is one, otherwise the clip at the playhead.</summary>
	public bool TryGetBoneLocal(string bone, out BoneTransform local)
	{
		if (_overrides.TryGetValue(bone, out local))
		{
			return true;
		}

		var skeleton = Skeleton;
		if (skeleton is null || skeleton.TryGetBoneIndex(bone, out var index) == false)
		{
			local = default;
			return false;
		}

		_scratchPose ??= new Pose(skeleton.BoneCount);
		SampleClip(skeleton, _scratchPose);
		local = _scratchPose.Bones[index];
		return true;
	}

	/// <summary>
	/// Groups every bone edit until the scope is disposed into one undo step, for example a whole gizmo
	/// drag or one field interaction. Nests.
	/// </summary>
	public IDisposable BeginEdit(string description)
	{
		if (_editDepth == 0 && CanEditMetadata)
		{
			_documentTransaction = MetadataDocument!.BeginTransaction(description);
		}

		_editDepth++;
		return new EditScope(this);
	}

	/// <summary>Sets a bone's local transform: an unkeyed override, or a key at the playhead with Auto Key on.</summary>
	public bool SetBoneLocalTransform(string bone, BoneTransform local)
	{
		if (CanEdit == false)
		{
			return false;
		}

		using var edit = BeginEdit("Transform Bone");

		// The override shows the edit immediately; a key written mid-edit only reaches the compiled
		// clip once the edit commits, at which point the override is dropped.
		_overrides[bone] = local;
		_overrideRevision++;
		if (AutoKey)
		{
			Document!.SetKey(bone, CurrentFrame, local);
			_keyedOverrides.Add(bone);
		}

		return true;
	}

	/// <summary>Keys the bone's current local transform, including any unkeyed edit, at the playhead.</summary>
	public bool SetKey(string bone)
	{
		if (CanEdit == false || TryGetBoneLocal(bone, out var local) == false)
		{
			return false;
		}

		using var edit = BeginEdit("Set Key");

		Document!.SetKey(bone, CurrentFrame, local);
		if (_overrides.ContainsKey(bone))
		{
			_keyedOverrides.Add(bone);
		}

		return true;
	}

	public bool SetKeyOnSelectedBone() =>
		SelectedBone is { } bone && SetKey(bone);

	public void DiscardUnkeyedChanges()
	{
		if (_overrides.Count == 0)
		{
			return;
		}

		// Overrides backing keys written in an open edit stay until it commits, or the pose would flicker.
		if (_editDepth > 0)
		{
			foreach (var bone in _overrides.Keys.ToArray())
			{
				if (_keyedOverrides.Contains(bone) == false)
				{
					_overrides.Remove(bone);
				}
			}
		}
		else
		{
			_overrides.Clear();
			_keyedOverrides.Clear();
		}

		_overrideRevision++;
	}

	/// <summary>
	/// Keyed frames per bone name, ascending. The same instance is returned until the keys change, so
	/// callers can rebuild derived views only when it does.
	/// </summary>
	public IReadOnlyDictionary<string, int[]> GetBoneKeyFrames()
	{
		var revision = Document?.Revision ?? 0;
		if (_boneKeyFrames is not null && revision == _boneKeyFramesRevision)
		{
			return _boneKeyFrames;
		}

		_boneKeyFrames = Document is not null
			? CollectKeyFrames(Document)
			: CollectKeyFrames(_importedClip, FramesPerSecond);
		_boneKeyFramesRevision = revision;
		PruneKeySelection(_boneKeyFrames);
		return _boneKeyFrames;
	}

	public void SelectKeys(IEnumerable<AnimationKeyRef> keys, KeySelectionMode mode)
	{
		ArgumentNullException.ThrowIfNull(keys);

		var list = keys.ToList();
		switch (mode)
		{
			case KeySelectionMode.Replace:
				_selectedKeys.Clear();
				_selectedKeys.UnionWith(list);
				break;

			case KeySelectionMode.Add:
				_selectedKeys.UnionWith(list);
				break;

			case KeySelectionMode.Toggle:
				// A diamond can stand for several keys; toggling it turns them all off only when all were on.
				if (list.Count > 0 && list.All(_selectedKeys.Contains))
				{
					_selectedKeys.ExceptWith(list);
				}
				else
				{
					_selectedKeys.UnionWith(list);
				}

				break;
		}
	}

	public void ClearKeySelection()
	{
		_selectedKeys.Clear();
	}

	/// <summary>Limits a frame offset so every selected key stays inside the clip.</summary>
	public int ClampSelectionOffset(int frameDelta)
	{
		if (_selectedKeys.Count == 0)
		{
			return 0;
		}

		var first = _selectedKeys.Min(key => key.Frame);
		var last = _selectedKeys.Max(key => key.Frame);
		return Math.Clamp(frameDelta, -first, LengthFrames - last);
	}

	public bool DeleteSelectedKeys()
	{
		if (CanEdit == false || IsEditing || _selectedKeys.Count == 0)
		{
			return false;
		}

		var deleted = Document!.DeleteKeys(_selectedKeys.ToArray());
		_selectedKeys.Clear();
		return deleted;
	}

	public bool MoveSelectedKeys(int frameDelta)
	{
		if (CanEdit == false ||
		    IsEditing ||
		    _selectedKeys.Count == 0 ||
		    Document!.MoveKeys(_selectedKeys.ToArray(), frameDelta) == false)
		{
			return false;
		}

		ShiftSelection(frameDelta);
		return true;
	}

	/// <summary>Copies the selected keys by an offset and selects the copies.</summary>
	public bool DuplicateSelectedKeys(int frameDelta)
	{
		if (CanEdit == false ||
		    IsEditing ||
		    _selectedKeys.Count == 0 ||
		    Document!.DuplicateKeys(_selectedKeys.ToArray(), frameDelta) == false)
		{
			return false;
		}

		ShiftSelection(frameDelta);
		return true;
	}

	/// <summary>
	/// Copies the selected keys so the earliest lands on the playhead. With the playhead already on
	/// the earliest key, the copy goes straight after the selection instead, which is how holds and
	/// cycles are usually built.
	/// </summary>
	public bool DuplicateSelectedKeysToPlayhead()
	{
		if (_selectedKeys.Count == 0)
		{
			return false;
		}

		var first = _selectedKeys.Min(key => key.Frame);
		var last = _selectedKeys.Max(key => key.Frame);
		var delta = CurrentFrame - first;
		if (delta == 0)
		{
			delta = last - first + 1;
		}

		return DuplicateSelectedKeys(delta);
	}

	/// <inheritdoc />
	public bool DeleteSelection() => DeleteSelectedKeys();

	/// <inheritdoc />
	public bool DuplicateSelection() => DuplicateSelectedKeysToPlayhead();

	public bool SetFramesPerSecond(int framesPerSecond)
	{
		if (CanEdit == false || IsEditing)
		{
			return false;
		}

		Document!.SetFramesPerSecond(framesPerSecond);
		ClampPlayhead();
		return true;
	}

	/// <summary>Number of keys a shorter length would delete, so the inspector can ask first.</summary>
	public int CountKeysBeyond(int lengthFrames) =>
		Document?.CountKeysBeyond(lengthFrames) ?? 0;

	public bool SetLengthFrames(int lengthFrames)
	{
		if (CanEdit == false || IsEditing)
		{
			return false;
		}

		Document!.SetLengthFrames(lengthFrames);
		ClampPlayhead();
		return true;
	}

	public bool SetLoop(bool loop)
	{
		if (CanEdit == false || IsEditing)
		{
			return false;
		}

		Document!.SetLoop(loop);
		return true;
	}

	/// <inheritdoc />
	public bool Undo()
	{
		if (CanEditMetadata == false || IsEditing || MetadataDocument!.CanUndo == false)
		{
			return false;
		}

		MetadataDocument!.Undo();
		ClampPlayhead();
		SyncBinding();
		return true;
	}

	/// <inheritdoc />
	public bool Redo()
	{
		if (CanEditMetadata == false || IsEditing || MetadataDocument!.CanRedo == false)
		{
			return false;
		}

		MetadataDocument!.Redo();
		ClampPlayhead();
		SyncBinding();
		return true;
	}

	/// <inheritdoc />
	public bool Save()
	{
		if (CanEditMetadata == false || IsEditing)
		{
			return false;
		}

		// The file may have been renamed or moved since the clip was opened; never save to where it used to be.
		if (TryFollowAssetLocation() == false)
		{
			Diagnostic = $"Could not save '{ClipName}': its asset no longer exists in the project.";
			return false;
		}

		try
		{
			MetadataDocument!.Save();
			_project.RefreshAssetSource(_asset!.RelativeSourcePath);
			Diagnostic = null;
			return true;
		}
		catch (Exception exception)
		{
			Diagnostic = $"Could not save '{ClipName}': {exception.Message}";
			return false;
		}
	}

	/// <summary>The clip as the runtime would play it, rebuilt whenever the document changes.</summary>
	public AnimationClip? GetRuntimeClip()
	{
		if (Document is null)
		{
			if (_importedClip is null || _importedMetadataDocument is null) return _importedClip;

			if (_compiledClip is null || _compiledRevision != _importedMetadataDocument.Revision)
			{
				var source = _importedClip;
				var timeline = _importedMetadataDocument.Clip;
				_compiledClip = new AnimationClip(source.Name, source.Duration, source.FramesPerSecond, source.Loop,
					source.TransformTracks, source.PropertyTracks, source.SourceSkeletonName, source.SourceBindPoseLocal,
					timeline.Curves.Select(curve => curve.Clone()).ToArray(),
					timeline.Markers.Select(marker => marker.Clone()).ToArray());
				_compiledRevision = _importedMetadataDocument.Revision;
			}

			return _compiledClip;
		}

		if (_compiledClip is null || _compiledRevision != Document.Revision)
		{
			_compiledClip = Document.Clip
				.Compile(ClipName, Skeleton?.Name ?? string.Empty, Skeleton?.BindPoseLocal ?? [])
				.ToClip();
			_compiledRevision = Document.Revision;
		}

		return _compiledClip;
	}

	/// <summary>Creates the preview on first use and brings its pose up to date with the playhead.</summary>
	public AnimationClipPreviewScene? EnsurePreview()
	{
		if (HasClip == false || PreviewModelId == Guid.Empty)
		{
			return null;
		}

		if (_preview is null)
		{
			try
			{
				_preview = new AnimationClipPreviewScene(_host, _views, _viewUi, _pipeline, _project, PreviewModelId);
				_appliedPose = default;
			}
			catch (Exception exception)
			{
				Diagnostic = $"Could not create the preview: {exception.Message}";
				PreviewModelId = Guid.Empty;
				return null;
			}
		}

		UpdatePreviewPose(_preview);
		return _preview;
	}

	public void HidePreview()
	{
		if (_preview is not null)
		{
			_viewUi.PublishUiState(_preview.View, SceneViewportUiState.Hidden);
		}
	}

	public void Dispose()
	{
		EndAllEdits();
		DisposePreview();
	}

	private void SetPlayheadInternal(int frame)
	{
		var target = Math.Clamp(frame, 0, Math.Max(0, LengthFrames));
		if (target != CurrentFrame || PlayheadFrame != target)
		{
			DiscardUnkeyedChanges();
		}

		PlayheadFrame = target;
	}

	private void ClampPlayhead()
	{
		if (PlayheadFrame > LengthFrames)
		{
			PlayheadFrame = LengthFrames;
		}
	}

	private void ShiftSelection(int frameDelta)
	{
		var shifted = _selectedKeys
			.Select(key => key with { Frame = key.Frame + frameDelta })
			.ToArray();
		_selectedKeys.Clear();
		_selectedKeys.UnionWith(shifted);
	}

	private void PruneKeySelection(Dictionary<string, int[]> boneKeyFrames)
	{
		_selectedKeys.RemoveWhere(key =>
			boneKeyFrames.TryGetValue(key.Bone, out var frames) == false ||
			Array.BinarySearch(frames, key.Frame) < 0);
	}

	private static Dictionary<string, int[]> CollectKeyFrames(AnimationClipDocument document)
	{
		var result = new Dictionary<string, int[]>(StringComparer.Ordinal);
		foreach (var track in document.Clip.TransformTracks)
		{
			if (track.Kind != AnimationBindingKind.Bone || track.HasKeys() == false)
			{
				continue;
			}

			var frames = new SortedSet<int>(track.Position.Frames);
			frames.UnionWith(track.Rotation.Frames);
			frames.UnionWith(track.Scale.Frames);
			result[track.Path] = frames.ToArray();
		}

		return result;
	}

	/// <summary>Imported keys are in seconds; show each on its nearest frame.</summary>
	private static Dictionary<string, int[]> CollectKeyFrames(AnimationClip? clip, int framesPerSecond)
	{
		var result = new Dictionary<string, int[]>(StringComparer.Ordinal);
		if (clip is null)
		{
			return result;
		}

		foreach (var track in clip.TransformTracks)
		{
			if (track.IsBoneTrack == false)
			{
				continue;
			}

			var frames = new SortedSet<int>();
			AddFrames(frames, track.Position.Times, framesPerSecond);
			AddFrames(frames, track.Rotation.Times, framesPerSecond);
			AddFrames(frames, track.Scale.Times, framesPerSecond);
			if (frames.Count > 0)
			{
				result[track.Binding.Path] = frames.ToArray();
			}
		}

		return result;
	}

	private static void AddFrames(SortedSet<int> frames, float[] times, int framesPerSecond)
	{
		foreach (var time in times)
		{
			frames.Add((int)MathF.Round(time * framesPerSecond));
		}
	}

	private void EndEdit()
	{
		_editDepth--;
		if (_editDepth > 0)
		{
			return;
		}

		_documentTransaction?.Dispose();
		_documentTransaction = null;

		if (_keyedOverrides.Count == 0)
		{
			return;
		}

		foreach (var bone in _keyedOverrides)
		{
			_overrides.Remove(bone);
		}

		_keyedOverrides.Clear();
		_overrideRevision++;
	}

	private void EndAllEdits()
	{
		while (_editDepth > 0)
		{
			EndEdit();
		}
	}

	private void SampleClip(Skeleton skeleton, Pose pose)
	{
		var clip = GetRuntimeClip();
		if (clip is null)
		{
			pose.SetToBindPose(skeleton);
		}
		else
		{
			AnimationClipPoseSampler.SampleBones(clip, skeleton, PlayheadFrame / FramesPerSecond, pose);
		}
	}

	private void UpdatePreviewPose(AnimationClipPreviewScene preview)
	{
		var key = new PoseKey(GetRuntimeClip(), PlayheadFrame, _overrideRevision);
		if (key == _appliedPose)
		{
			return;
		}

		SampleClip(preview.Skeleton, preview.Pose);
		foreach (var (bone, local) in _overrides)
		{
			if (preview.Skeleton.TryGetBoneIndex(bone, out var index))
			{
				preview.Pose.Bones[index] = local;
			}
		}

		preview.ApplyPose();
		_appliedPose = key;
	}

	/// <summary>
	/// Looks the open clip up by its asset id and points its document at the source file's current
	/// location, so renames and moves in the asset browser are followed. Returns false when the asset
	/// is no longer in the project.
	/// </summary>
	private bool TryFollowAssetLocation()
	{
		if (HasClip == false || _project.TryGetAsset(AssetId, out var current) == false)
		{
			return false;
		}

		_asset = current;

		// Compared against the document rather than the cached entry, which the project may update in place.
		var path = _project.GetAbsoluteAssetPath(current.Id, current.RelativeSourcePath);
		if (MetadataDocument is not { } document || string.Equals(document.Path, path, StringComparison.Ordinal))
		{
			return true;
		}

		document.Relocate(path);
		ClipName = Document is not null
			? AuthoredAnimationClipFile.GetClipName(current.RelativeSourcePath)
			: current.Name;
		return true;
	}

	private void LoadClip(AssetDatabaseEntry asset)
	{
		AuthoredAnimationClipFile? authored = null;
		if (IsAuthoredClip(asset))
		{
			var path = _project.GetAbsoluteAssetPath(asset.Id, asset.RelativeSourcePath);
			if (_documents.TryGetValue(asset.Id, out var document))
			{
				document.Relocate(path);
			}
			else
			{
				document = AnimationClipDocument.Open(path);
				_documents.Add(asset.Id, document);
			}

			Document = document;
			authored = document.Clip;
			IsReadOnly = _project.IsAssetReadOnly(asset.Id);
		}
		else
		{
			_importedClip = AssetDatabase.GetInstance<AnimationClip>(asset.Id)
				?? throw new InvalidOperationException("The imported clip could not be loaded.");
			var path = _project.GetAbsoluteAssetPath(asset.Id, asset.RelativeSourcePath);
			if (_documents.TryGetValue(asset.Id, out var document))
			{
				document.Relocate(path);
			}
			else
			{
				var metadata = new AssetMetadataStore().Load(path + ".meta");
				var key = metadata.SubAssets.Single(entry => entry.NodeId == asset.Id).Key;
				document = AnimationClipDocument.OpenImported(path, key, _importedClip);
				_documents.Add(asset.Id, document);
			}

			_importedMetadataDocument = document;
			IsReadOnly = true;
		}

		AssetId = asset.Id;
		_asset = asset;
		ClipName = authored is not null
			? AuthoredAnimationClipFile.GetClipName(asset.RelativeSourcePath)
			: asset.Name;

		ResolveBinding();
	}

	/// <summary>Rebinds when the document's skeleton or preview model changed, including through undo.</summary>
	private void SyncBinding()
	{
		if (Document is null)
		{
			return;
		}

		if (Document.Clip.Skeleton.NodeId == _boundSkeletonId &&
		    Document.Clip.PreviewModelId == _boundPreviewModelId)
		{
			return;
		}

		DisposePreview();
		_compiledClip = null;
		_compiledRevision = -1;
		_scratchPose = null;
		ResolveBinding();
	}

	private void ResolveBinding()
	{
		var asset = _asset!;
		var authored = Document?.Clip;
		_boundSkeletonId = authored?.Skeleton.NodeId ?? Guid.Empty;
		_boundPreviewModelId = authored?.PreviewModelId ?? Guid.Empty;
		Skeleton = null;
		PreviewModelId = Guid.Empty;
		Diagnostic = null;

		if (AnimationClipBindingResolver.TryResolve(
			    asset,
			    authored,
			    _project.CurrentAssetDatabase.Assets,
			    _pipeline.GetDependencies(_project.ProjectRootPath!),
			    out var binding,
			    out var error) == false)
		{
			Diagnostic = CanEdit && authored!.Skeleton.IsValid == false
				? "Choose a skeleton for this clip in the Clip Inspector."
				: error;
			return;
		}

		if (authored is null)
		{
			_boundSkeletonId = binding.SkeletonId;
		}

		Skeleton = AssetDatabase.GetInstance<Skeleton>(binding.SkeletonId);
		PreviewModelId = binding.PreviewModelId;
		if (Skeleton is null)
		{
			Diagnostic = "The clip's skeleton could not be loaded.";
		}
	}

	/// <summary>
	/// Applies the unsaved-changes policy before the open clip is replaced or closed. Returns true when
	/// the action may go ahead now.
	/// </summary>
	private bool ResolveUnsavedChanges(UnsavedChangesPolicy policy, PendingAction action)
	{
		if (MetadataDocument is null || MetadataDocument.IsDirty == false || CanEditMetadata == false)
		{
			return true;
		}

		switch (policy)
		{
			case UnsavedChangesPolicy.Save:
				return Save();

			case UnsavedChangesPolicy.Discard:
				// Dropping the cached document makes the next open read the file as last saved.
				_documents.Remove(AssetId);
				return true;

			default:
				_pending = action;
				_openPrompt = true;
				return false;
		}
	}

	private void ShowWorkspace()
	{
		if (_workspaces.Workspaces.Any(workspace => workspace.Id == EditorWorkspaceService.AnimateWorkspaceId))
		{
			_workspaces.Activate(EditorWorkspaceService.AnimateWorkspaceId);
		}

		foreach (var windowId in EditorWindowIds.AnimationClipEditor)
		{
			_workspaces.OpenWindow(windowId);
		}
	}

	/// <summary>Selecting a clip in an Assets panel inside the Animate workspace opens it here.</summary>
	internal void FollowAssetSelection()
	{
		var selected = _selection.SelectedAssetId;
		if (selected == _lastSeenSelection)
		{
			return;
		}

		_lastSeenSelection = selected;
		if (selected is { } id &&
		    id != AssetId &&
		    _project.TryGetAsset(id, out var asset) &&
		    asset.Type == AssetType.AnimationClip)
		{
			Open(asset);
		}
	}

	private void CheckProject()
	{
		if (_projectPath == _project.ProjectRootPath)
		{
			return;
		}

		// The old project is gone, so its unsaved clip edits cannot be saved anywhere any more.
		ResetState();
		_documents.Clear();
		_pending = default;
		_projectPath = _project.ProjectRootPath;
	}

	private void DisposePreview()
	{
		_preview?.Dispose();
		_preview = null;
		_appliedPose = default;
	}

	private enum PendingActionKind
	{
		None,
		Open,
		Close
	}

	/// <summary>What the unsaved-changes prompt will do once answered.</summary>
	private readonly record struct PendingAction(PendingActionKind Kind, AssetDatabaseEntry? Asset);

	private sealed class EditScope : IDisposable
	{
		private AnimationClipEditorSession? _session;

		public EditScope(AnimationClipEditorSession session)
		{
			_session = session;
		}

		public void Dispose()
		{
			var session = _session;
			_session = null;
			session?.EndEdit();
		}
	}

	/// <summary>What the preview pose was last built from; a new clip instance means the document changed.</summary>
	private readonly record struct PoseKey(AnimationClip? Clip, float Frame, int OverrideRevision);
}

public enum KeySelectionMode
{
	Replace,
	Add,
	Toggle
}

/// <summary>What to do with unsaved clip edits when another clip opens or the clip closes.</summary>
public enum UnsavedChangesPolicy
{
	Prompt,
	Save,
	Discard
}
