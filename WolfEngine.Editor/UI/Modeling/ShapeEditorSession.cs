using System.Numerics;
using ImGuiNET;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Modeling;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Shaders;
using WolfEngine.Rendering.UI;
using WolfEngine.Utility;

namespace WolfEngine.Editor.UI;

/// <summary>
/// State shared by every Model workspace panel: the open shape, the selected node, the background compile and the
/// preview. Panels read and drive this; none of them own shape state themselves. Mirrors
/// <see cref="AnimationClipEditorSession"/>: documents stay open per asset, an unsaved shape prompts before it is
/// replaced, Save writes and reimports, and the open shape reopens with the project.
/// </summary>
public sealed class ShapeEditorSession : IEditorReopenableDocument, IEditorDocumentEditor, IDisposable
{
	public const string DefaultName = "New Shape";
	public const string DefaultFolder = "Assets";

	private const int MaxPickSteps = 256;
	private const float PickHitDistance = 1.0e-4f;

	private readonly IEditorProjectService _project;
	private readonly IProjectAssetPipelineService _pipeline;
	private readonly IEditorWorkspaceService _workspaces;
	private readonly IAssetSelectionService _selection;
	private readonly RenderGraph _renderGraph;
	private readonly EditorRenderViews _views;
	private readonly EditorViewportStateBus _viewUi;
	private readonly GpuSdfGridSampler _sampler;
	private readonly ShapeCompileScheduler _compiles;

	// Documents stay open per asset so switching shapes never drops unsaved edits.
	private readonly Dictionary<Guid, ShapeDocument> _documents = new();

	private ShapePreviewScene? _preview;
	private bool _framePending;
	private Guid? _lastSeenSelection;
	private int _lastTickFrame = -1;
	private int _focusedFrame = int.MinValue;
	private string? _projectPath;
	private AssetDatabaseEntry? _asset;
	private PendingAction _pending;
	private bool _openPrompt;

	public ShapeEditorSession(
		IEditorProjectService project,
		IProjectAssetPipelineService pipeline,
		IEditorWorkspaceService workspaces,
		IAssetSelectionService selection,
		RenderGraph renderGraph,
		EditorRenderViews views,
		EditorViewportStateBus viewUi,
		IRenderer renderer,
		IShaderProvider shaders,
		IMainThreadDispatcher mainThread)
	{
		_project = project;
		_pipeline = pipeline;
		_workspaces = workspaces;
		_selection = selection;
		_renderGraph = renderGraph;
		_views = views;
		_viewUi = viewUi;
		_sampler = new GpuSdfGridSampler(renderer, shaders, mainThread);
		_compiles = new ShapeCompileScheduler(new ShapeCompiler(_sampler));
	}

	public bool HasShape => AssetId != Guid.Empty;
	public Guid AssetId { get; private set; }
	public string ShapeName { get; private set; } = string.Empty;
	public ShapeDocument? Document { get; private set; }
	public bool IsReadOnly { get; private set; }
	public bool CanEdit => Document is not null && IsReadOnly == false;
	public string? Diagnostic { get; private set; }

	public Guid? SelectedNodeId { get; private set; }

	/// <summary>Increments whenever the selection changes, so panels can reveal it once.</summary>
	public int SelectionRevision { get; private set; }

	public ShapePreviewScene? Preview => _preview;
	public bool IsCompiling => _compiles.IsRunning;
	public ShapeCompileStats? LastCompileStats => _compiles.LastStats;
	public string? CompileError => _compiles.LastError;
	public long AppliedCompileCount => _compiles.AppliedCompileCount;

	public bool HasShortcutFocus => HasShape && _focusedFrame >= ImGui.GetFrameCount() - 1;
	public bool IsPromptPending => _pending.Kind != PendingActionKind.None;

	public event Action<Guid>? OpenAssetChanged;

	public EditorDocumentKind Kind => EditorDocumentKind.Shape;
	public string? DisplayName => HasShape ? ShapeName : null;
	public bool IsDirty => Document?.IsDirty == true;

	// Saving or moving through history mid-gesture would split one edit in two.
	public bool CanSave => CanEdit && Document!.IsInTransaction == false;
	public bool CanUndo => CanSave && Document!.CanUndo;
	public bool CanRedo => CanSave && Document!.CanRedo;

	public ShapeNode? SelectedNode => SelectedNodeId is { } id ? Document?.Tree.Find(id) : null;

	public static bool IsShapeAsset(AssetDatabaseEntry asset)
	{
		return asset.Type == AssetType.Model3D && ShapeFile.IsShapePath(asset.RelativeSourcePath);
	}

	/// <summary>
	/// Opens a shape and brings up the Model workspace. If the open shape has unsaved edits,
	/// <paramref name="unsavedChanges"/> decides what happens to them; by default the user is asked, and this
	/// returns false until they answer.
	/// </summary>
	public bool Open(AssetDatabaseEntry asset, UnsavedChangesPolicy unsavedChanges = UnsavedChangesPolicy.Prompt)
	{
		return OpenShape(asset, unsavedChanges, showWorkspace: true);
	}

	public bool Reopen(AssetDatabaseEntry asset)
	{
		return OpenShape(asset, UnsavedChangesPolicy.Prompt, showWorkspace: false);
	}

	public bool Close(UnsavedChangesPolicy unsavedChanges = UnsavedChangesPolicy.Prompt)
	{
		if (HasShape == false)
		{
			return true;
		}

		if (ResolveUnsavedChanges(unsavedChanges, new PendingAction(PendingActionKind.Close, null)) == false)
		{
			return false;
		}

		ResetState();
		OpenAssetChanged?.Invoke(Guid.Empty);
		return true;
	}

	/// <summary>
	/// Writes a new shape into a folder, compiled, and imports it. The file holds the default rounded box and its
	/// compiled mesh, so it can be placed in a scene straight away.
	/// </summary>
	public AssetDatabaseEntry CreateAsset(string folder, string name)
	{
		CheckProject();
		var projectRoot = _project.ProjectRootPath
			?? throw new InvalidOperationException("Open a project to create shapes.");
		var relativePath = GetUniquePath(folder, name);
		var document = ShapeDocument.Create(_project.GetAbsolutePath(relativePath), ShapeAsset.CreateDefault());

		// A compile that runs for the new document must not leave state behind for the shape open now.
		var scheduler = new ShapeCompileScheduler(new ShapeCompiler(_sampler));
		if (scheduler.CompileNow(document) == false)
		{
			throw new InvalidOperationException($"The new shape did not compile: {scheduler.LastError}");
		}

		document.Save();
		_project.RefreshAssetSource(relativePath);
		if (_pipeline.TryGetPrimaryNodeIdForRelativeSourcePath(projectRoot, relativePath, out var assetId) == false ||
		    _project.TryGetAsset(assetId, out var asset) == false)
		{
			throw new InvalidOperationException($"'{relativePath}' was written but not imported.");
		}

		_documents[asset.Id] = document;
		return asset;
	}

	public bool CreateShape(string folder, string name, UnsavedChangesPolicy unsavedChanges = UnsavedChangesPolicy.Prompt)
	{
		try
		{
			return Open(CreateAsset(folder, name), unsavedChanges);
		}
		catch (Exception exception)
		{
			Diagnostic = $"Could not create the shape: {exception.Message}";
			return false;
		}
	}

	public void DrawDialogs()
	{
		const string popupId = "Unsaved Shape Changes";
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
		ImGui.TextUnformatted($"'{ShapeName}' has unsaved changes. Save them before {action}?");

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

	public void MarkFocused()
	{
		_focusedFrame = ImGui.GetFrameCount();
	}

	public void Select(Guid? nodeId)
	{
		if (SelectedNodeId == nodeId)
		{
			return;
		}

		SelectedNodeId = nodeId;
		SelectionRevision++;
	}

	/// <summary>
	/// Follows renames and asset selection, applies finished compiles and starts the next one. Safe to call from
	/// several panels: it only runs once per UI frame.
	/// </summary>
	public void Tick()
	{
		var frame = ImGui.GetFrameCount();
		if (frame == _lastTickFrame)
		{
			return;
		}

		_lastTickFrame = frame;
		Update();
	}

	/// <summary>The per-frame work of <see cref="Tick"/>, without the once-per-UI-frame guard, for automation.</summary>
	internal void Update()
	{
		CheckProject();
		TryFollowAssetLocation();
		FollowAssetSelection();
		if (Document is null)
		{
			return;
		}

		_compiles.Update(Document);
		DropMissingSelection();
		SyncPreview();
	}

	// ---- Editing ----

	/// <summary>Groups edits into one undo step until disposed, such as a gizmo drag or a slider drag.</summary>
	public IDisposable BeginEdit(string description)
	{
		if (CanEdit == false)
		{
			return EmptyScope.Instance;
		}

		return Document!.BeginTransaction(description);
	}

	/// <summary>Changes one node. Returns false when the shape is read-only or the node is gone.</summary>
	public bool EditNode(Guid nodeId, string description, Action<ShapeNode> change)
	{
		if (CanEdit == false || Document!.Tree.Find(nodeId) is null)
		{
			return false;
		}

		Document.Edit(description, (tree, _) => change(tree.Find(nodeId)!));
		return true;
	}

	public bool EditSettings(string description, Action<ShapeCompileSettings> change)
	{
		if (CanEdit == false)
		{
			return false;
		}

		Document!.Edit(description, (_, settings) => change(settings));
		return true;
	}

	/// <summary>Adds a node next to the selection, inside it when it is a group, and selects it.</summary>
	public ShapeNode? AddNode(ShapeNode node)
	{
		ArgumentNullException.ThrowIfNull(node);
		if (CanEdit == false)
		{
			return null;
		}

		Document!.Edit($"Add {node.Name}", (tree, _) =>
		{
			var (parent, index) = GetInsertionPoint(tree);
			parent.Children.Insert(index, node);
		});
		Select(node.Id);
		return node;
	}

	public ShapeNode? AddPrimitive(ShapePrimitiveKind kind)
	{
		return AddNode(ShapeNode.CreatePrimitive(kind));
	}

	public ShapeNode? AddGroup()
	{
		return AddNode(ShapeNode.CreateGroup("Group"));
	}

	/// <summary>Moves a node into a group at an index. Refuses to move a group into itself or the root anywhere.</summary>
	public bool MoveNode(Guid nodeId, Guid newParentId, int index)
	{
		if (CanEdit == false)
		{
			return false;
		}

		var tree = Document!.Tree;
		var node = tree.Find(nodeId);
		var newParent = tree.Find(newParentId);
		var oldParent = tree.FindParent(nodeId);
		if (node is null ||
		    newParent is null ||
		    oldParent is null ||
		    newParent.IsGroup == false ||
		    node.Contains(newParent))
		{
			return false;
		}

		Document.Edit($"Move {node.Name}", (editedTree, _) =>
		{
			var editedNode = editedTree.Find(nodeId)!;
			var from = editedTree.FindParent(nodeId)!;
			var to = editedTree.Find(newParentId)!;
			var oldIndex = from.Children.IndexOf(editedNode);
			from.Children.RemoveAt(oldIndex);
			var target = index;
			if (ReferenceEquals(from, to) && oldIndex < target)
			{
				target--;
			}

			to.Children.Insert(Math.Clamp(target, 0, to.Children.Count), editedNode);
		});
		return true;
	}

	public bool DeleteSelection()
	{
		if (CanEdit == false || SelectedNodeId is not { } id)
		{
			return false;
		}

		var parent = Document!.Tree.FindParent(id);
		var node = Document.Tree.Find(id);
		if (parent is null || node is null)
		{
			return false;
		}

		Document.Edit($"Delete {node.Name}", (tree, _) => tree.FindParent(id)!.Children.RemoveAll(child => child.Id == id));
		Select(null);
		return true;
	}

	public bool DuplicateSelection()
	{
		if (CanEdit == false || SelectedNode is not { } node || Document!.Tree.FindParent(node.Id) is null)
		{
			return false;
		}

		var copy = node.Duplicate();
		copy.Name = $"{node.Name} Copy";
		var id = node.Id;
		Document.Edit($"Duplicate {node.Name}", (tree, _) =>
		{
			var parent = tree.FindParent(id)!;
			var index = parent.Children.FindIndex(child => child.Id == id);
			parent.Children.Insert(index + 1, copy);
		});
		Select(copy.Id);
		return true;
	}

	public bool Undo()
	{
		if (CanUndo == false)
		{
			return false;
		}

		Document!.Undo();
		DropMissingSelection();
		return true;
	}

	public bool Redo()
	{
		if (CanRedo == false)
		{
			return false;
		}

		Document!.Redo();
		DropMissingSelection();
		return true;
	}

	/// <summary>Waits for the compile to catch up, then writes the shape and reimports it.</summary>
	public bool Save()
	{
		if (CanSave == false)
		{
			return false;
		}

		// The file may have been renamed or moved since the shape was opened; never save to where it used to be.
		if (TryFollowAssetLocation() == false)
		{
			Diagnostic = $"Could not save '{ShapeName}': its asset no longer exists in the project.";
			return false;
		}

		// The saved mesh must be the one compiled from the saved tree; a file that pairs a tree with a stale mesh
		// would import geometry the shape no longer describes.
		if (_compiles.CompileNow(Document!) == false)
		{
			Diagnostic = $"Could not save '{ShapeName}': it does not compile ({_compiles.LastError}).";
			return false;
		}

		try
		{
			Document!.Save();
			_project.RefreshAssetSource(_asset!.RelativeSourcePath);
			Diagnostic = null;
			return true;
		}
		catch (Exception exception)
		{
			Diagnostic = $"Could not save '{ShapeName}': {exception.Message}";
			return false;
		}
	}

	/// <summary>Blocks until the compiled mesh matches the shape. Used by automation and by Save.</summary>
	public bool CompileNow()
	{
		if (Document is null)
		{
			return false;
		}

		var compiled = _compiles.CompileNow(Document);
		SyncPreview();
		return compiled;
	}

	/// <summary>Compiles from scratch, ignoring the previous mesh. The only way to discard the warm-start history.</summary>
	public bool RecompileCold()
	{
		if (CanEdit == false)
		{
			return false;
		}

		_compiles.RecompileCold(Document!);
		return true;
	}

	// ---- Viewport ----

	/// <summary>Creates the preview on first use and shows the latest compiled mesh in it.</summary>
	public ShapePreviewScene? EnsurePreview()
	{
		if (Document is null)
		{
			return null;
		}

		if (_preview is null)
		{
			try
			{
				_preview = new ShapePreviewScene(_renderGraph, _views, _viewUi);
				_framePending = true;
			}
			catch (Exception exception)
			{
				Diagnostic = $"Could not create the preview: {exception.Message}";
				return null;
			}
		}

		SyncPreview();
		return _preview;
	}

	public void HidePreview()
	{
		if (_preview is not null)
		{
			_viewUi.PublishUiState(_preview.View, SceneViewportUiState.Hidden);
		}
	}

	public void FrameAll()
	{
		if (_preview is null || Document is null)
		{
			return;
		}

		_preview.Frame(GetBounds());
	}

	public void FrameSelection()
	{
		if (_preview is null || Document is null)
		{
			return;
		}

		if (SelectedNode is not { } node || node.IsGroup)
		{
			FrameAll();
			return;
		}

		if (Document.Tree.TryGetShapeMatrix(node.Id, out var matrix) == false)
		{
			FrameAll();
			return;
		}

		var halfExtents = ShapePrimitives.GetLocalHalfExtents(node.Primitive, node.Dimensions);
		_preview.Frame(ShapeBounds.FromTransformedBox(halfExtents, matrix));
	}

	/// <summary>
	/// The primitive under a ray: ray-marches the whole field to the surface, then picks the primitive whose own
	/// surface is nearest the hit. Returns null when the ray misses.
	/// </summary>
	public Guid? Pick(Vector3 origin, Vector3 direction)
	{
		if (Document is null)
		{
			return null;
		}

		ShapeSdfProgram program;
		try
		{
			program = ShapeSdfProgram.Compile(Document.Tree);
		}
		catch (InvalidOperationException)
		{
			return null;
		}

		var bounds = program.Bounds;
		if (bounds.IsEmpty)
		{
			return null;
		}

		var maxDistance = Vector3.Distance(origin, bounds.Center) + bounds.Size.Length();
		var travelled = 0.0f;
		var hit = false;
		for (var step = 0; step < MaxPickSteps && travelled < maxDistance; step++)
		{
			var distance = program.Evaluate(origin + direction * travelled);
			if (distance < PickHitDistance * MathF.Max(1.0f, travelled))
			{
				hit = true;
				break;
			}

			travelled += MathF.Max(distance, PickHitDistance);
		}

		if (hit == false)
		{
			return null;
		}

		var point = origin + direction * travelled;
		var ids = program.InstructionNodeIds;
		var instructions = program.Instructions;
		Guid? best = null;
		var bestDistance = float.MaxValue;
		for (var i = 0; i < instructions.Length; i++)
		{
			if ((ShapeSdfOpcode)instructions[i].Opcode != ShapeSdfOpcode.Primitive)
			{
				continue;
			}

			var distance = MathF.Abs(program.EvaluateInstruction(i, point));
			if (distance < bestDistance)
			{
				bestDistance = distance;
				best = ids[i];
			}
		}

		return best;
	}

	public ShapeBounds GetBounds()
	{
		if (Document?.Mesh is { IsEmpty: false } mesh)
		{
			var bounds = ShapeBounds.Empty;
			foreach (var position in mesh.Positions)
			{
				bounds = bounds.Union(new ShapeBounds(position, position));
			}

			return bounds;
		}

		try
		{
			return Document is null ? ShapeBounds.Empty : ShapeSdfProgram.Compile(Document.Tree).Bounds;
		}
		catch (InvalidOperationException)
		{
			return ShapeBounds.Empty;
		}
	}

	public void Dispose()
	{
		DisposePreview();
		_sampler.Dispose();
	}

	// ---- Internals ----

	private bool OpenShape(AssetDatabaseEntry asset, UnsavedChangesPolicy unsavedChanges, bool showWorkspace)
	{
		ArgumentNullException.ThrowIfNull(asset);
		CheckProject();

		if (IsShapeAsset(asset) == false)
		{
			return false;
		}

		// Only a later change of asset selection should open a shape, not the selection that led here.
		_lastSeenSelection = _selection.SelectedAssetId;
		if (showWorkspace)
		{
			ShowWorkspace();
		}

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
			var path = _project.GetAbsoluteAssetPath(asset.Id, asset.RelativeSourcePath);
			if (_documents.TryGetValue(asset.Id, out var document))
			{
				document.Relocate(path);
			}
			else
			{
				document = ShapeDocument.Open(path);
				_documents.Add(asset.Id, document);
			}

			Document = document;
			IsReadOnly = _project.IsAssetReadOnly(asset.Id);
			AssetId = asset.Id;
			_asset = asset;
			ShapeName = ShapeFile.GetShapeName(asset.RelativeSourcePath);
			OpenAssetChanged?.Invoke(asset.Id);
			return true;
		}
		catch (Exception exception)
		{
			ResetState();
			Diagnostic = $"Could not open '{asset.Name}': {exception.Message}";
			return false;
		}
	}

	private void ResetState()
	{
		DisposePreview();
		_compiles.Reset();
		AssetId = Guid.Empty;
		ShapeName = string.Empty;
		Document = null;
		IsReadOnly = false;
		Diagnostic = null;
		_asset = null;
		Select(null);
	}

	private (ShapeNode Parent, int Index) GetInsertionPoint(ShapeTree tree)
	{
		if (SelectedNodeId is { } id && tree.Find(id) is { } selected)
		{
			if (selected.IsGroup)
			{
				return (selected, selected.Children.Count);
			}

			if (tree.FindParent(id) is { } parent)
			{
				return (parent, parent.Children.IndexOf(selected) + 1);
			}
		}

		return (tree.Root, tree.Root.Children.Count);
	}

	private void DropMissingSelection()
	{
		if (SelectedNodeId is { } id && Document?.Tree.Find(id) is null)
		{
			Select(null);
		}
	}

	private void SyncPreview()
	{
		if (_preview is null || Document is null)
		{
			return;
		}

		_preview.SetMesh(Document.Mesh, Document.Settings.MaterialId);
		if (_framePending && Document.Mesh is not null)
		{
			FrameAll();
			_framePending = false;
		}
	}

	private bool TryFollowAssetLocation()
	{
		if (HasShape == false || _project.TryGetAsset(AssetId, out var current) == false)
		{
			return false;
		}

		_asset = current;
		var path = _project.GetAbsoluteAssetPath(current.Id, current.RelativeSourcePath);
		if (Document is not { } document || string.Equals(document.Path, path, StringComparison.Ordinal))
		{
			return true;
		}

		document.Relocate(path);
		ShapeName = ShapeFile.GetShapeName(current.RelativeSourcePath);
		return true;
	}

	private bool ResolveUnsavedChanges(UnsavedChangesPolicy policy, PendingAction action)
	{
		if (Document is null || Document.IsDirty == false || CanEdit == false)
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
		if (_workspaces.Workspaces.Any(workspace => workspace.Id == EditorWorkspaceService.ModelWorkspaceId))
		{
			_workspaces.Activate(EditorWorkspaceService.ModelWorkspaceId);
		}

		foreach (var windowId in EditorWindowIds.ShapeEditor)
		{
			_workspaces.OpenWindow(windowId);
		}
	}

	/// <summary>Selecting a shape in an Assets panel while the Model workspace is active opens it here.</summary>
	private void FollowAssetSelection()
	{
		var selected = _selection.SelectedAssetId;
		if (selected == _lastSeenSelection)
		{
			return;
		}

		_lastSeenSelection = selected;
		if (_workspaces.ActiveWorkspace.OwnedDocument != EditorDocumentKind.Shape)
		{
			return;
		}

		if (selected is { } id &&
		    id != AssetId &&
		    _project.TryGetAsset(id, out var asset) &&
		    IsShapeAsset(asset))
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

		// The old project is gone, so its unsaved shape edits cannot be saved anywhere any more.
		ResetState();
		_documents.Clear();
		_pending = default;
		_projectPath = _project.ProjectRootPath;
	}

	private void DisposePreview()
	{
		_preview?.Dispose();
		_preview = null;
	}

	private string GetUniquePath(string folder, string name)
	{
		var baseName = SanitizeFileName(string.IsNullOrWhiteSpace(name) ? DefaultName : name.Trim());
		var directory = string.IsNullOrWhiteSpace(folder) ? DefaultFolder : folder.Replace('\\', '/').TrimEnd('/');
		var path = $"{directory}/{baseName}{ShapeFile.Extension}";
		var suffix = 1;
		while (File.Exists(_project.GetAbsolutePath(path)))
		{
			path = $"{directory}/{baseName} {suffix}{ShapeFile.Extension}";
			suffix++;
		}

		return path;
	}

	private static string SanitizeFileName(string name)
	{
		var invalid = Path.GetInvalidFileNameChars();
		var characters = name.ToCharArray();
		for (var i = 0; i < characters.Length; i++)
		{
			if (Array.IndexOf(invalid, characters[i]) >= 0 || characters[i] == '/' || characters[i] == '\\')
			{
				characters[i] = '_';
			}
		}

		return new string(characters);
	}

	private enum PendingActionKind
	{
		None,
		Open,
		Close
	}

	private readonly record struct PendingAction(PendingActionKind Kind, AssetDatabaseEntry? Asset);

	private sealed class EmptyScope : IDisposable
	{
		public static readonly EmptyScope Instance = new();

		public void Dispose()
		{
		}
	}
}
