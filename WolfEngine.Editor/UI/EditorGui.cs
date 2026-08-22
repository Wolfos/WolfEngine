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
	private static bool _defaultDockLayoutApplied;

	private readonly IMenuBar _menuBar;
	private readonly IEditorModeState _editorModeState;
	private readonly IEditorInteractionState _interactionState;
	private readonly IEditorCommandService _commandService;
	private readonly IEditorOperationService _operationService;

	private readonly EntitiesWindow _entitiesWindow;
	private readonly AssetsWindow _assetsWindow;
	private readonly AssetEditorWindow _assetEditorWindow;
	private readonly MaterialImporterWindow _materialImporterWindow;
	private readonly ComponentsWindow _componentsWindow;
	private readonly ProfilerWindow _profilerWindow;
	private readonly SceneWindow _sceneWindow;
	private readonly ProjectSettingsWindow _projectSettingsWindow;

	public EditorGui(
		IMenuBar menuBar,
		IEditorModeState editorModeState,
		IEditorInteractionState interactionState,
		IEditorCommandService commandService,
		IEditorOperationService operationService,
		IServiceProvider serviceProvider)
	{
		_menuBar = menuBar;
		_editorModeState = editorModeState;
		_interactionState = interactionState;
		_commandService = commandService;
		_operationService = operationService;

		_entitiesWindow = serviceProvider.GetRequiredService<EntitiesWindow>();
		_assetsWindow = serviceProvider.GetRequiredService<AssetsWindow>();
		_assetEditorWindow = serviceProvider.GetRequiredService<AssetEditorWindow>();
		_materialImporterWindow = serviceProvider.GetRequiredService<MaterialImporterWindow>();
		_componentsWindow = serviceProvider.GetRequiredService<ComponentsWindow>();
		_profilerWindow = serviceProvider.GetRequiredService<ProfilerWindow>();
		_sceneWindow = serviceProvider.GetRequiredService<SceneWindow>();
		_projectSettingsWindow = serviceProvider.GetRequiredService<ProjectSettingsWindow>();
		_commandService.BindDeletionHandlers(_entitiesWindow, _assetsWindow);
	}

	public void Draw(EditorScene scene)
	{
		if (_operationService.Current is { IsActive: true } operation && _commandService.LoadingSceneAssetId.HasValue == false)
		{
			DrawLoadingScreen(operation);
			return;
		}
		_interactionState.BeginFrame();
		DockSpace();

		using (FrameProfiler.Instance.Measure("Menu Bar"))
		{
			_menuBar.Draw(scene);
		}

		switch (_editorModeState.CurrentMode)
		{
			case EditorMode.Scene:
				DrawWindow(_entitiesWindow, scene);
				DrawWindow(_sceneWindow, scene);
				DrawWindow(_componentsWindow, scene);
				DrawWindow(_assetEditorWindow, scene);
				break;
			case EditorMode.Assets:
				DrawWindow(_assetsWindow, scene);
				DrawWindow(_componentsWindow, scene);
				DrawWindow(_assetEditorWindow, scene);
				break;
			case EditorMode.Animation:
				break;
		}

		DrawWindow(_profilerWindow, scene);
		DrawWindow(_materialImporterWindow, scene);

		_commandService.ProcessShortcuts();
		_commandService.DrawPendingDialogs();

		using (FrameProfiler.Instance.Measure("Preferences"))
		{
			EditorPreferencesMenu.Draw();
		}
		_projectSettingsWindow.Draw();
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

	private static void DrawWindow(EditorWindow window, EditorScene scene)
	{
		using (FrameProfiler.Instance.Measure(window.Name))
		{
			window.Draw(scene);
		}
	}

	private static void DockSpace()
	{
		var viewport = ImGui.GetMainViewport();
		ImGui.SetNextWindowPos(viewport.WorkPos);
		ImGui.SetNextWindowSize(viewport.WorkSize);
		ImGui.SetNextWindowViewport(viewport.ID);

		ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0.0f);
		ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0.0f);
		ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

		const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDocking
		                               | ImGuiWindowFlags.NoTitleBar
		                               | ImGuiWindowFlags.NoCollapse
		                               | ImGuiWindowFlags.NoResize
		                               | ImGuiWindowFlags.NoMove
		                               | ImGuiWindowFlags.NoBringToFrontOnFocus
		                               | ImGuiWindowFlags.NoNavFocus
		                               | ImGuiWindowFlags.NoBackground;

		ImGui.Begin("DockSpace", flags);
		ImGui.PopStyleVar(3);

		var dockspaceId = ImGui.GetID("MainDockSpace");
		ImGui.DockSpace(dockspaceId, Vector2.Zero, ImGuiDockNodeFlags.PassthruCentralNode);
		ApplyDefaultDockLayout(dockspaceId);
		ImGui.End();
	}

	private static void ApplyDefaultDockLayout(uint dockspaceId)
	{
		if (_defaultDockLayoutApplied || HasSavedDockLayout())
		{
			_defaultDockLayoutApplied = true;
			return;
		}

		// ImGui.NET 1.91 does not expose DockBuilder even though the cimgui library
		// bundled with the engine does. Build a layout once, then let ImGui persist
		// all future user changes in its normal ini file.
		NativeDockBuilder.RemoveNode(dockspaceId);
		NativeDockBuilder.AddNode(dockspaceId, NativeDockBuilder.DockSpaceFlag);
		NativeDockBuilder.SetNodeSize(dockspaceId, ImGui.GetWindowSize());

		NativeDockBuilder.SplitNode(dockspaceId, ImGuiDir.Left, 0.18f, out var leftId, out var centerAndRightId);
		NativeDockBuilder.SplitNode(centerAndRightId, ImGuiDir.Right, 0.20f, out var rightId, out var centerId);

		NativeDockBuilder.DockWindow("Entities", leftId);
		NativeDockBuilder.DockWindow("Scene", centerId);
		NativeDockBuilder.DockWindow("Assets", centerId);
		NativeDockBuilder.DockWindow("Components", rightId);
		NativeDockBuilder.DockWindow("Asset Editor", rightId);
		NativeDockBuilder.Finish(dockspaceId);
		_defaultDockLayoutApplied = true;
	}

	private static bool HasSavedDockLayout()
	{
		var settings = ImGui.SaveIniSettingsToMemory();
		return settings.Contains("DockNode", StringComparison.Ordinal);
	}

	private static class NativeDockBuilder
	{
		private const string CImGuiLibrary = "cimgui";
		// DockSpace is an internal Dear ImGui flag (1 << 10) and is intentionally
		// omitted from ImGui.NET's public ImGuiDockNodeFlags enum.
		public const int DockSpaceFlag = 1 << 10;

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
