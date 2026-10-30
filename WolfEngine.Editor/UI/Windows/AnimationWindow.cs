using System.Numerics;
using ImGuiNET;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

public sealed partial class AnimationWindow : EditorWindow, IDisposable
{
	private readonly IEditorProjectService _project;
	private readonly IProjectAssetPipelineService _pipeline;
	private readonly IAssetSelectionService _selection;
	private readonly IPropertyDrawerRegistry _propertyDrawerRegistry;
	private readonly IIconManager _icons;
	private readonly IEditorWorkspaceService _workspaces;
	private readonly IRenderViewHost _host;
	private readonly EditorRenderViews _views;
	private readonly EditorViewportStateBus _viewUi;
	private readonly Dictionary<Guid, AnimationDocument> _documents = new();
	private readonly Dictionary<Guid, GraphViewState> _graphViews = new();
	private readonly Dictionary<string, GraphRect> _toolbarBounds = new();
	private AnimationDocument? _document;
	private AnimationPreviewScene? _preview;
	private GraphViewState _view = new();
	private Guid _assetId;
	private Guid _selectedNode;
	private Guid _connectFrom;
	private Guid _modelId;
	private AssetRef<AnimationSet> _previewSet;
	private Guid _contextNode;
	private int _contextInput = -1;
	private Vector2 _contextCanvasPosition;
	private string? _gestureSnapshot;
	private string? _projectPath;
	private string? _diagnostic;
	private long _compiledRevision = -1;
	private float _scrub;
	private float _inspectorWidth = 360.0f;
	private Entity _liveEntity;
	private bool _arrangeRequested;
	private Guid _focusNode;
	private AnimationNodeKind? _createKind;
	private SidebarTab _activeTab;
	private SidebarTab? _requestedTab;
	private string _nodeSearch = string.Empty;
	public override string Name => "Animation";
	public AnimationDocument? Document => _document;
	public AnimationGraphInstance? PreviewInstance => _preview?.Instance;
	public string? Diagnostic => _diagnostic;
	public RenderViewId? PreviewView => _preview?.View;

	private enum SidebarTab
	{
		Node,
		Parameters,
		Preview,
		Live
	}

	private static readonly byte[][] SidebarTabLabels = ["Inspector\0"u8.ToArray(), "Parameters\0"u8.ToArray(), "Preview\0"u8.ToArray(), "Live\0"u8.ToArray()];
	private sealed class GraphViewState
	{
		public AnimationGraphCamera Camera = new(Vector2.Zero, 1.0f);
		public bool FitRequested = true;
		public bool AutoFrame = true;
		public Vector2 ViewportSize;
	}

	public AnimationWindow(
		IEditorProjectService project,
		IProjectAssetPipelineService pipeline,
		IAssetSelectionService selection,
		IEditorWorkspaceService workspaces,
		IRenderViewHost host,
		EditorRenderViews views,
		EditorViewportStateBus viewUi,
		IPropertyDrawerRegistry propertyDrawerRegistry,
		IIconManager icons)
	{
		_project = project;
		_pipeline = pipeline;
		_selection = selection;
		_workspaces = workspaces;
		_host = host;
		_views = views;
		_viewUi = viewUi;
		_propertyDrawerRegistry = propertyDrawerRegistry;
		_icons = icons;
	}

	public void Open(AssetDatabaseEntry asset)
	{
		CheckProject();
		if (AnimationAssetJson.GetAssetType(asset.RelativeSourcePath)is null)
		{
			return;
		}

		CommitGesture();
		if (_documents.TryGetValue(asset.Id, out _document) == false)
		{
			_document = new AnimationDocument(_project.GetAbsoluteAssetPath(asset.Id, asset.RelativeSourcePath));
			_documents.Add(asset.Id, _document);
		}
		else
		{
			_document.Relocate(_project.GetAbsoluteAssetPath(asset.Id, asset.RelativeSourcePath));
		}

		if (_graphViews.TryGetValue(asset.Id, out var view) == false)
		{
			view = new GraphViewState();
			_graphViews.Add(asset.Id, view);
		}

		_view = view;
		_assetId = asset.Id;
		_selectedNode = (_document.Asset as AnimationGraph)?.Output ?? Guid.Empty;
		_connectFrom = Guid.Empty;
		_dragNode = Guid.Empty;
		_compiledRevision = -1;
		_diagnostic = null;
		_requestedTab = SidebarTab.Node;
		if (_workspaces.Workspaces.Any(workspace => workspace.Id == EditorWorkspaceService.AnimationWorkspaceId))
		{
			_workspaces.Activate(EditorWorkspaceService.AnimationWorkspaceId);
		}

		_workspaces.OpenWindow(EditorWindowIds.Animation);
		RequestFocus();
	}

	private void CheckProject()
	{
		if (_projectPath == _project.ProjectRootPath)
		{
			return;
		}

		DisposePreview();
		_documents.Clear();
		_graphViews.Clear();
		_document = null;
		_projectPath = _project.ProjectRootPath;
		_assetId = Guid.Empty;
		_modelId = Guid.Empty;
		_previewSet = default;
		_liveEntity = default;
		_gestureSnapshot = null;
	}

	public override void OnHidden()
	{
		CheckProject();
		HidePreview();
	}

	private void HidePreview()
	{
		if (_preview is not null)
		{
			_viewUi.PublishUiState(_preview.View, SceneViewportUiState.Hidden);
		}
	}

	public void Dispose() => DisposePreview();
	private void DisposePreview()
	{
		_preview?.Dispose();
		_preview = null;
	}

	public override void Draw(EditorScene scene)
	{
		CheckProject();
		Begin();
		try
		{
			if (ImGui.IsWindowDocked() && IsSelectedTab == false)
			{
				OnHidden();
				return;
			}

			if (_project.HasOpenProject == false)
			{
				ImGui.TextUnformatted("Open a project to edit animation.");
				return;
			}

			if (_selection.SelectedAssetId is { } selected && selected != _assetId && _project.TryGetAsset(selected, out var asset) && AnimationAssetJson.GetAssetType(asset.RelativeSourcePath)is not null)
			{
				try
				{
					Open(asset);
				}
				catch (Exception exception)
				{
					_diagnostic = exception.Message;
				}
			}

			if (_document?.Asset is not AnimationGraph graph)
			{
				ImGui.TextUnformatted("Open an animation graph from Assets.");
				DrawLive(scene);
				return;
			}

			if (_project.TryGetAsset(_assetId, out var currentAsset))
			{
				_document.Relocate(_project.GetAbsoluteAssetPath(_assetId, currentAsset.RelativeSourcePath));
			}

			var readOnly = _project.IsAssetReadOnly(_assetId);
			DrawToolbar(graph, readOnly);
			// Undo replaces the document object, so resolve it again after toolbar actions.
			graph = (AnimationGraph)_document.Asset;
			var before = _document.Snapshot();
			var available = ImGui.GetContentRegionAvail();
			const float divider = 6.0f;
			var minimumSidebar = MathF.Min(300.0f, available.X * 0.4f);
			var sidebar = Math.Clamp(
				_inspectorWidth,
				minimumSidebar,
				MathF.Max(minimumSidebar, MathF.Min(520.0f, available.X * 0.65f)));
			var canvasWidth = MathF.Max(1.0f, available.X - sidebar - divider);
			DrawCanvas(graph, new Vector2(canvasWidth, MathF.Max(1.0f, available.Y)), readOnly);
			ImGui.SameLine(0.0f, 0.0f);
			ImGui.InvisibleButton("##GraphSidebarDivider", new Vector2(divider, MathF.Max(1.0f, available.Y)));
			if (ImGui.IsItemHovered() || ImGui.IsItemActive())
			{
				ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
			}

			if (ImGui.IsItemActive())
			{
				_inspectorWidth = sidebar - ImGui.GetIO().MouseDelta.X;
			}

			ImGui.SameLine(0.0f, 0.0f);
			ImGui.BeginChild("Graph sidebar", new Vector2(0.0f, available.Y), ImGuiChildFlags.Borders);
			try
			{
				DrawSidebar(graph, scene, readOnly);
			}
			finally
			{
				ImGui.EndChild();
			}

			if (readOnly == false)
			{
				if (ImGui.IsAnyItemActive())
				{
					_gestureSnapshot ??= before;
				}
				else
				{
					_document.Commit(_gestureSnapshot ?? before);
					_gestureSnapshot = null;
				}
			}
		}
		finally
		{
			ImGui.End();
		}
	}

	private void DrawToolbar(AnimationGraph graph, bool readOnly)
	{
		_toolbarBounds.Clear();
		var sourceName = Path.GetFileName(_document!.Path);
		var name = sourceName.EndsWith(AnimationGraph.Extension, StringComparison.OrdinalIgnoreCase) ? sourceName[..^AnimationGraph.Extension.Length] : Path.GetFileNameWithoutExtension(sourceName);
		var bold = ImGuiUiSystem.PushBoldFont();
		ImGui.TextUnformatted(name);
		ImGuiUiSystem.PopFontIfPushed(bold);
		ImGui.SameLine();
		ImGui.TextDisabled(_document.Dirty ? "Animation graph  /  Unsaved changes" : "Animation graph");
		ImGui.BeginDisabled(readOnly);
		if (ToolbarButton("Save", "save"))
		{
			Save();
		}

		ImGui.SameLine();
		ImGui.BeginDisabled(_document.CanUndo == false && _gestureSnapshot is null);
		if (ToolbarButton("Undo", "undo"))
		{
			CommitGesture();
			_document.Undo();
			if (_view.AutoFrame)
			{
				_view.FitRequested = true;
			}
		}

		ImGui.EndDisabled();
		ImGui.SameLine();
		ImGui.BeginDisabled(_document.CanRedo == false);
		if (ToolbarButton("Redo", "redo"))
		{
			_document.Redo();
			if (_view.AutoFrame)
			{
				_view.FitRequested = true;
			}
		}

		ImGui.EndDisabled();
		ImGui.EndDisabled();
		ImGui.SameLine(0.0f, 18.0f);
		if (ToolbarButton("Fit all", "fit"))
		{
			_view.FitRequested = true;
		}

		ImGui.SameLine();
		ImGui.BeginDisabled(graph.Nodes.All(node => node.Id != _selectedNode));
		if (ToolbarButton("Focus", "focus"))
		{
			_focusNode = _selectedNode;
		}

		ImGui.EndDisabled();
		ImGui.SameLine();
		if (ToolbarButton("100%", "resetZoom"))
		{
			_view.Camera = _view.Camera.ZoomAt(_canvasRect.Size * 0.5f, 1.0f / _view.Camera.Zoom);
			_view.AutoFrame = false;
		}

		ImGui.SameLine();
		ImGui.BeginDisabled(readOnly);
		if (ToolbarButton("Arrange", "arrange"))
		{
			_arrangeRequested = true;
		}

		ImGui.SameLine();
		if (ToolbarButton("+ Node", "add"))
		{
			ImGui.OpenPopup("Add graph node");
		}

		if (ImGui.BeginPopup("Add graph node"))
		{
			foreach (var kind in Enum.GetValues<AnimationNodeKind>())
			{
				if (ImGui.MenuItem(AnimationGraphLayout.KindName(kind)))
				{
					_createKind = kind;
				}
			}

			ImGui.EndPopup();
		}

		ImGui.EndDisabled();
		ImGui.SameLine();
		if (ToolbarButton("Find node", "find"))
		{
			_nodeSearch = string.Empty;
			ImGui.OpenPopup("Find graph node");
		}

		if (ImGui.BeginPopup("Find graph node"))
		{
			if (ImGui.IsWindowAppearing())
			{
				ImGui.SetKeyboardFocusHere();
			}

			ImGui.InputTextWithHint("##search", "Search nodes...", ref _nodeSearch, 128);
			foreach (var node in graph.Nodes)
			{
				if (AnimationGraphLayout.Title(node).Contains(_nodeSearch, StringComparison.OrdinalIgnoreCase) == false)
				{
					continue;
				}

				if (ImGui.Selectable($"{AnimationGraphLayout.Title(node)}##{node.Id}"))
				{
					SelectNode(node.Id, focus: true);
					ImGui.CloseCurrentPopup();
				}
			}

			ImGui.EndPopup();
		}
	}

	private bool ToolbarButton(string label, string id)
	{
		var clicked = ImGui.Button(label);
		_toolbarBounds[id] = new GraphRect(ImGui.GetItemRectMin(), ImGui.GetItemRectSize());
		return clicked;
	}

	private void DrawSidebar(AnimationGraph graph, EditorScene scene, bool readOnly)
	{
		if (ImGui.BeginTabBar("Graph sidebar tabs"))
		{
			foreach (var tab in Enum.GetValues<SidebarTab>())
			{
				var flags = _requestedTab == tab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
				if (BeginSidebarTab(tab, flags) == false)
				{
					continue;
				}

				_activeTab = tab;
				ImGui.BeginDisabled(readOnly && tab is SidebarTab.Node or SidebarTab.Parameters);
				switch (tab)
				{
					case SidebarTab.Node:
						var selected = graph.Nodes.FirstOrDefault(node => node.Id == _selectedNode);
						if (selected is null)
						{
							ImGui.TextUnformatted("Select a node to inspect it.");
							ImGui.TextWrapped("Drag nodes to move them. Connect a pose output to an input on another node.");
						}
						else
						{
							DrawNode(graph, selected);
						}

						break;
					case SidebarTab.Parameters:
						DrawParameters(graph);
						break;
					case SidebarTab.Preview:
						DrawPreview();
						break;
					case SidebarTab.Live:
						DrawLive(scene);
						break;
				}

				ImGui.EndDisabled();
				ImGui.EndTabItem();
			}

			ImGui.EndTabBar();
		}

		_requestedTab = null;
		if (_activeTab != SidebarTab.Preview)
		{
			HidePreview();
		}

		if (_diagnostic is not null)
		{
			ImGui.SeparatorText("Preview diagnostic");
			ImGui.TextWrapped(_diagnostic);
		}
	}

	private static unsafe bool BeginSidebarTab(SidebarTab tab, ImGuiTabItemFlags flags)
	{
		// ImGui.NET's flags overload requires a close-button pointer. These fixed
		// tabs use the native null pointer so selecting a tab does not add a close button.
		fixed (byte* label = SidebarTabLabels[(int)tab])
		{
			return ImGuiNative.igBeginTabItem(label, null, flags) != 0;
		}
	}

	private void SelectNode(Guid id, bool focus = false)
	{
		_selectedNode = id;
		_requestedTab = SidebarTab.Node;
		if (focus)
		{
			_focusNode = id;
		}
	}

	private void CommitGesture()
	{
		if (_gestureSnapshot is not null && _document is not null)
		{
			_document.Commit(_gestureSnapshot);
			_gestureSnapshot = null;
		}
	}

	public void Save()
	{
		if (_document is null || _project.IsAssetReadOnly(_assetId))
		{
			return;
		}

		CommitGesture();
		try
		{
			_document.Save();
			if (_project.TryGetAsset(_assetId, out var asset))
			{
				_project.RefreshAssetSource(asset.RelativeSourcePath);
			}
		}
		catch (Exception exception)
		{
			_diagnostic = exception.Message;
		}
	}

	private bool DrawAssetRef<T>(string label, ref AssetRef<T> value)
	{
		var selectionButton = new AssetLinkSelectionButton(_icons.Get("search"), assetId => _selection.Select(assetId));
		return _propertyDrawerRegistry.DrawAssetRef(label, ref value, selectionButton);
	}

	internal static AnimationNode CreateNode(AnimationGraph graph, AnimationNodeKind kind, Vector2 position)
	{
		var node = new AnimationNode
		{
			Kind = kind,
			Name = AnimationGraphLayout.KindName(kind)
		};
		var count = kind switch
		{
			AnimationNodeKind.Blend or AnimationNodeKind.MaskedBlend or AnimationNodeKind.Locomotion1D => 2,
			AnimationNodeKind.Output or AnimationNodeKind.CurveRemap or AnimationNodeKind.Select or AnimationNodeKind.StateMachine => 1,
			_ => 0
		};
		for (var index = 0; index < count; index++)
		{
			node.Inputs.Add(Guid.Empty);
		}

		if (kind == AnimationNodeKind.Locomotion1D)
		{
			node.Thresholds = [0.0f, 1.0f];
		}

		graph.Nodes.Add(node);
		graph.Layout.Add(new AnimationNodeLayout { NodeId = node.Id, X = position.X, Y = position.Y });
		if (kind == AnimationNodeKind.Output)
		{
			graph.Output = node.Id;
		}

		return node;
	}

	internal static void DeleteNode(AnimationGraph graph, Guid nodeId)
	{
		graph.Nodes.RemoveAll(node => node.Id == nodeId);
		graph.Layout.RemoveAll(layout => layout.NodeId == nodeId);
		foreach (var node in graph.Nodes)
		{
			for (var index = 0; index < node.Inputs.Count; index++)
			{
				if (node.Inputs[index] == nodeId)
				{
					node.Inputs[index] = Guid.Empty;
				}
			}
		}

		if (graph.Output == nodeId)
		{
			graph.Output = graph.Nodes.FirstOrDefault(node => node.Kind == AnimationNodeKind.Output)?.Id ?? Guid.Empty;
		}
	}
}
