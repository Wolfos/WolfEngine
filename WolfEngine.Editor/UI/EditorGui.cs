using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using Microsoft.Extensions.DependencyInjection;
using WolfEngine.ECS;
using WolfEngine.Profiling;

namespace WolfEngine.Editor.UI;

public class EditorGui
{
	// TODO: Maybe not public?
	public static readonly List<Type> SelectedComponentTypes = new();
	public static readonly List<Entity> SelectedEntities = new();
	public static Entity SelectedEntity;
	public static bool HasSelectedEntity = false;
	public static Entity? SelectionRangeAnchor;
	private static Entity? _selectionRevealRequest;
	private static bool _componentsWindowFocusRequested;
	private readonly IMenuBar _menuBar;
	private readonly IEditorWorkspaceService _workspaces;
	private readonly EditorWindowRegistry _windowRegistry;
	private readonly IEditorInteractionState _interactionState;
	private readonly IEditorCommandService _commandService;
	private readonly IEditorOperationService _operationService;

	private readonly EntitiesWindow _entitiesWindow;
	private readonly AssetsWindow _assetsWindow;
	private readonly ComponentsWindow _componentsWindow;
	private readonly SceneWindow _sceneWindow;

	public EditorGui(
		IMenuBar menuBar,
		IEditorWorkspaceService workspaces,
		EditorWindowRegistry windowRegistry,
		IEditorInteractionState interactionState,
		IEditorCommandService commandService,
		IEditorOperationService operationService,
		EntitiesWindow entitiesWindow,
		AssetsWindow assetsWindow,
		ComponentsWindow componentsWindow,
		SceneWindow sceneWindow)
	{
		_menuBar = menuBar;
		_workspaces = workspaces;
		_windowRegistry = windowRegistry;
		_interactionState = interactionState;
		_commandService = commandService;
		_operationService = operationService;

		_entitiesWindow = entitiesWindow;
		_assetsWindow = assetsWindow;
		_componentsWindow = componentsWindow;
		_sceneWindow = sceneWindow;
		_commandService.BindDeletionHandlers(_entitiesWindow, _assetsWindow);
	}

	public void Draw(EditorScene scene)
	{
		if (_operationService.Current is { IsActive: true } operation && _commandService.LoadingSceneAssetId.HasValue == false)
		{
			_sceneWindow.OnHidden();
			DrawLoadingScreen(operation);
			return;
		}
		_interactionState.BeginFrame();
		_workspaces.LoadImGuiSettings();

		using (FrameProfiler.Instance.Measure("Menu Bar"))
		{
			_menuBar.Draw(scene);
		}
		DockSpaces();

		if (_componentsWindowFocusRequested) _windowRegistry.Open(EditorWindowIds.Components);
		_windowRegistry.DrawVisible(scene);
		if (!_workspaces.IsWindowOpen(EditorWindowIds.Scene)) _sceneWindow.OnHidden();
		if (_workspaces.ActiveWorkspace.OpenWindows.Count == 0) DrawEmptyWorkspaceHint();

		_commandService.ProcessShortcuts();
		_commandService.DrawPendingDialogs();

		_workspaces.SaveImGuiSettingsIfNeeded();
	}

	private static void DrawEmptyWorkspaceHint()
	{
		var viewport = ImGui.GetMainViewport();
		const string hint = "This workspace is empty. Open a panel from the Window menu.";
		var size = ImGui.CalcTextSize(hint);
		ImGui.GetForegroundDrawList().AddText(
			viewport.WorkPos + (viewport.WorkSize - size) * 0.5f,
			ImGui.GetColorU32(ImGuiCol.TextDisabled), hint);
	}

	private static void DrawLoadingScreen(EditorOperationSnapshot operation)
	{
		var viewport = ImGui.GetMainViewport();
		ImGui.SetNextWindowPos(viewport.WorkPos);
		ImGui.SetNextWindowSize(viewport.WorkSize);
		ImGui.SetNextWindowViewport(viewport.ID);
		const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
			ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoBringToFrontOnFocus;
		ImGui.Begin("Editor Loading", flags);
		var textSize = ImGui.CalcTextSize(operation.Title);
		ImGui.SetCursorPos(new Vector2(Math.Max(24.0f, (viewport.WorkSize.X - textSize.X) * 0.5f), viewport.WorkSize.Y * 0.42f));
		ImGui.TextUnformatted(operation.Title);
		DrawLoadingSpinner(viewport);
		ImGui.SetCursorPosX(Math.Max(24.0f, (viewport.WorkSize.X - 360.0f) * 0.5f));
		ImGui.TextDisabled(operation.Detail);
		if (operation.Elapsed >= TimeSpan.FromSeconds(2))
		{
			ImGui.SetCursorPosX(Math.Max(24.0f, (viewport.WorkSize.X - 360.0f) * 0.5f));
			ImGui.TextDisabled($"{operation.Elapsed.TotalSeconds:F0}s elapsed");
		}
		ImGui.End();
	}

	public static void DrawLoadingSpinner(ImGuiViewportPtr viewport)
	{
		var center = new Vector2(viewport.WorkPos.X + viewport.WorkSize.X * 0.5f, viewport.WorkPos.Y + viewport.WorkSize.Y * 0.50f);
		DrawLoadingSpinner(ImGui.GetWindowDrawList(), center);
		ImGui.Dummy(new Vector2(0.0f, 32.0f));
	}

	public static void DrawLoadingSpinner(ImDrawListPtr drawList, Vector2 center)
	{
		const int dotCount = 12;
		const float radius = 12.0f;
		const float dotRadius = 2.5f;
		var time = (float)ImGui.GetTime();
		for (var i = 0; i < dotCount; i++)
		{
			var phase = (i / (float)dotCount + time * 1.5f) % 1.0f;
			var alpha = 0.15f + 0.85f * phase;
			var angle = phase * MathF.Tau;
			var position = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
			drawList.AddCircleFilled(position, dotRadius, ImGui.ColorConvertFloat4ToU32(new Vector4(0.85f, 0.88f, 0.95f, alpha)));
		}
	}

	public void PrepareForGameplayReload()
	{
		ClearEntitySelection();
		_componentsWindow.ResetCachedTypes();
	}

	public static void SelectEntity(Entity entity, World world, bool requestFocus = true)
	{
		ReplaceEntitySelection(entity, world, requestFocus);
	}

	public static void ReplaceEntitySelection(Entity entity, World world, bool requestFocus = true)
	{
		SelectedEntities.Clear();
		SelectedEntities.Add(entity);
		SelectionRangeAnchor = entity;
		RefreshSelectedEntity(world, requestFocus);
	}

	public static void AddEntitySelection(Entity entity, World world, bool requestFocus = true)
	{
		if (SelectedEntities.Contains(entity) == false)
		{
			SelectedEntities.Add(entity);
		}

		SelectionRangeAnchor = entity;
		RefreshSelectedEntity(world, requestFocus);
	}

	/// <summary>
	/// Adds <paramref name="entity"/> to the selection, or removes it when it is already selected.
	/// </summary>
	public static void ToggleEntitySelection(Entity entity, World world, bool requestFocus = true)
	{
		if (SelectedEntities.Remove(entity))
		{
			SelectionRangeAnchor = SelectedEntities.Count > 0 ? SelectedEntities[^1] : null;
			RefreshSelectedEntity(world, requestFocus);
			return;
		}

		AddEntitySelection(entity, world, requestFocus);
	}

	public static void AddEntitySelectionRange(IReadOnlyList<Entity> visibleEntities, Entity clickedEntity, World world, bool requestFocus = true)
	{
		var anchor = SelectionRangeAnchor is { } candidate && visibleEntities.Contains(candidate)
			? candidate
			: HasSelectedEntity ? SelectedEntity : clickedEntity;
		var anchorIndex = IndexOf(visibleEntities, anchor);
		var clickedIndex = IndexOf(visibleEntities, clickedEntity);
		if (anchorIndex < 0 || clickedIndex < 0)
		{
			AddEntitySelection(clickedEntity, world, requestFocus);
			return;
		}

		var start = Math.Min(anchorIndex, clickedIndex);
		var end = Math.Max(anchorIndex, clickedIndex);
		for (var i = start; i <= end; i++)
		{
			if (SelectedEntities.Contains(visibleEntities[i]) == false)
			{
				SelectedEntities.Add(visibleEntities[i]);
			}
		}

		SelectionRangeAnchor = clickedEntity;
		RefreshSelectedEntity(world, requestFocus);
	}

	/// <summary>
	/// Takes the entity the hierarchy should unfold to and scroll into view, if a selection has been
	/// made since the last call. Requests are raised by every selection path, so a selection made
	/// anywhere — the viewport, an undo, a newly instantiated prefab — becomes visible in the tree
	/// without each of those callers knowing the hierarchy exists.
	/// </summary>
	public static bool ConsumeSelectionRevealRequest(out Entity entity)
	{
		if (_selectionRevealRequest is not { } requested)
		{
			entity = default;
			return false;
		}

		_selectionRevealRequest = null;
		entity = requested;
		return true;
	}

	/// <summary>
	/// Drops a pending reveal. The hierarchy calls this for selections it made itself: the entity is
	/// already on screen under the cursor, and scrolling it to centre would yank the list out from
	/// under the click.
	/// </summary>
	public static void DiscardSelectionRevealRequest()
	{
		_selectionRevealRequest = null;
	}

	public static bool ConsumeComponentsWindowFocusRequest()
	{
		if (_componentsWindowFocusRequested == false)
		{
			return false;
		}

		_componentsWindowFocusRequested = false;
		return true;
	}

	public static void RefreshSelectedEntity(World world, bool requestFocus = false)
	{
		for (var i = SelectedEntities.Count - 1; i >= 0; i--)
		{
			if (world.IsAlive(SelectedEntities[i]) == false)
			{
				SelectedEntities.RemoveAt(i);
			}
		}

		if (SelectedEntities.Count == 0)
		{
			ClearEntitySelection();
			return;
		}

		HasSelectedEntity = true;
		SelectedEntity = SelectedEntities[0];

		_selectionRevealRequest = SelectionRangeAnchor is { } anchor && SelectedEntities.Contains(anchor)
			? anchor
			: SelectedEntity;
		SelectedComponentTypes.Clear();
		var componentTypes = new List<Type>();
		for (var i = 0; i < SelectedEntities.Count; i++)
		{
			world.GetComponentTypes(SelectedEntities[i], componentTypes);
			for (var componentIndex = 0; componentIndex < componentTypes.Count; componentIndex++)
			{
				var componentType = componentTypes[componentIndex];
				if (SelectedComponentTypes.Contains(componentType) == false)
				{
					SelectedComponentTypes.Add(componentType);
				}
			}
		}
		_componentsWindowFocusRequested = requestFocus;
	}

	public static void ClearEntitySelection()
	{
		HasSelectedEntity = false;
		SelectedEntity = default;
		SelectedEntities.Clear();
		SelectionRangeAnchor = null;
		SelectedComponentTypes.Clear();
		_selectionRevealRequest = null;
		_componentsWindowFocusRequested = false;
	}

	private static int IndexOf(IReadOnlyList<Entity> entities, Entity entity)
	{
		for (var i = 0; i < entities.Count; i++)
		{
			if (entities[i] == entity)
			{
				return i;
			}
		}

		return -1;
	}

	private void DockSpaces()
	{
		var activeId = _workspaces.ActiveWorkspace.Id;
		foreach (var workspace in _workspaces.Workspaces)
		{
			if (workspace.Id != activeId) DockSpace(workspace, false);
		}
		DockSpace(_workspaces.ActiveWorkspace, true);
	}

	private void DockSpace(EditorWorkspace workspace, bool active)
	{
		var viewport = ImGui.GetMainViewport();
		ImGui.SetNextWindowPos(viewport.WorkPos);
		ImGui.SetNextWindowSize(viewport.WorkSize);
		ImGui.SetNextWindowViewport(viewport.ID);

		ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0.0f);
		ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0.0f);
		ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

		var flags = ImGuiWindowFlags.NoDocking
		                               | ImGuiWindowFlags.NoTitleBar
		                               | ImGuiWindowFlags.NoCollapse
		                               | ImGuiWindowFlags.NoResize
		                               | ImGuiWindowFlags.NoMove
		                               | ImGuiWindowFlags.NoBringToFrontOnFocus
		                               | ImGuiWindowFlags.NoNavFocus
		                               | ImGuiWindowFlags.NoBackground;
		if (!active) flags |= ImGuiWindowFlags.NoInputs;

		ImGui.Begin($"DockSpace###DockSpace-{workspace.Id:N}", flags);
		ImGui.PopStyleVar(3);

		var dockspaceId = ImGui.GetID($"MainDockSpace-{workspace.Id:N}");
		ApplyDefaultDockLayout(workspace, dockspaceId);
		var dockFlags = active
			? ImGuiDockNodeFlags.PassthruCentralNode
			: ImGuiDockNodeFlags.KeepAliveOnly;
		ImGui.DockSpace(dockspaceId, Vector2.Zero, dockFlags);
		ImGui.End();
	}

	private void ApplyDefaultDockLayout(EditorWorkspace workspace, uint dockspaceId)
	{
		if (NativeDockBuilder.GetNode(dockspaceId) != IntPtr.Zero) return;
		if (workspace.Id != EditorWorkspaceService.SceneWorkspaceId && workspace.Id != EditorWorkspaceService.AssetsWorkspaceId) return;

		// ImGui.NET 1.91 does not expose DockBuilder even though the cimgui library
		// bundled with the engine does. Build a layout once, then let the workspace
		// service persist all future changes inside editor preferences.
		NativeDockBuilder.RemoveNode(dockspaceId);
		NativeDockBuilder.AddNode(dockspaceId, NativeDockBuilder.DockSpaceFlag);
		NativeDockBuilder.SetNodeSize(dockspaceId, ImGui.GetWindowSize());

		NativeDockBuilder.SplitNode(dockspaceId, ImGuiDir.Left, 0.18f, out var leftId, out var centerAndRightId);
		NativeDockBuilder.SplitNode(centerAndRightId, ImGuiDir.Right, 0.20f, out var rightId, out var centerId);
		NativeDockBuilder.SplitNode(centerId, ImGuiDir.Down, 0.25f, out var bottomId, out var centerTopId);

		if (workspace.Id == EditorWorkspaceService.SceneWorkspaceId)
		{
			Dock(EditorWindowIds.Entities, leftId);
			Dock(EditorWindowIds.Scene, centerTopId);
		}
		else
		{
			Dock(EditorWindowIds.Assets, centerTopId);
		}
		Dock(EditorWindowIds.Log, bottomId);
		Dock(EditorWindowIds.Components, rightId);
		Dock(EditorWindowIds.AssetEditor, rightId);
		NativeDockBuilder.Finish(dockspaceId);

		void Dock(string windowId, uint nodeId)
		{
			var descriptor = _windowRegistry.Get(windowId);
			NativeDockBuilder.DockWindow(descriptor.Window.GetWorkspaceImGuiName(workspace.Id, windowId), nodeId);
		}
	}

	private static class NativeDockBuilder
	{
		private const string CImGuiLibrary = "cimgui";
		// DockSpace is an internal Dear ImGui flag (1 << 10) and is intentionally
		// omitted from ImGui.NET's public ImGuiDockNodeFlags enum.
		public const int DockSpaceFlag = 1 << 10;

		[DllImport(CImGuiLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "igDockBuilderGetNode")]
		public static extern IntPtr GetNode(uint nodeId);

		[DllImport(CImGuiLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "igDockBuilderRemoveNode")]
		public static extern void RemoveNode(uint nodeId);

		[DllImport(CImGuiLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "igDockBuilderAddNode")]
		public static extern void AddNode(uint nodeId, int flags);

		[DllImport(CImGuiLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "igDockBuilderSetNodeSize")]
		public static extern void SetNodeSize(uint nodeId, Vector2 size);

		[DllImport(CImGuiLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "igDockBuilderSplitNode")]
		public static extern void SplitNode(
			uint nodeId,
			ImGuiDir direction,
			float sizeRatio,
			out uint nodeAtDirection,
			out uint nodeAtOppositeDirection);

		[DllImport(CImGuiLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "igDockBuilderDockWindow")]
		public static extern void DockWindow([MarshalAs(UnmanagedType.LPUTF8Str)] string windowName, uint nodeId);

		[DllImport(CImGuiLibrary, CallingConvention = CallingConvention.Cdecl, EntryPoint = "igDockBuilderFinish")]
		public static extern void Finish(uint nodeId);
	}
}
