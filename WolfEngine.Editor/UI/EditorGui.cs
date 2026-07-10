using System;
using System.Collections.Generic;
using System.Numerics;
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
	private static bool _componentsWindowFocusRequested;

	private readonly IMenuBar _menuBar;
	private readonly IEditorModeState _editorModeState;
	private readonly IEditorInteractionState _interactionState;
	private readonly IEditorCommandService _commandService;

	private readonly EntitiesWindow _entitiesWindow;
	private readonly AssetsWindow _assetsWindow;
	private readonly AssetEditorWindow _assetEditorWindow;
	private readonly MaterialImporterWindow _materialImporterWindow;
	private readonly ComponentsWindow _componentsWindow;
	private readonly ProfilerWindow _profilerWindow;
	private readonly SceneWindow _sceneWindow;

	public EditorGui(
		IMenuBar menuBar,
		IEditorModeState editorModeState,
		IEditorInteractionState interactionState,
		IEditorCommandService commandService,
		IServiceProvider serviceProvider)
	{
		_menuBar = menuBar;
		_editorModeState = editorModeState;
		_interactionState = interactionState;
		_commandService = commandService;

		_entitiesWindow = serviceProvider.GetRequiredService<EntitiesWindow>();
		_assetsWindow = serviceProvider.GetRequiredService<AssetsWindow>();
		_assetEditorWindow = serviceProvider.GetRequiredService<AssetEditorWindow>();
		_materialImporterWindow = serviceProvider.GetRequiredService<MaterialImporterWindow>();
		_componentsWindow = serviceProvider.GetRequiredService<ComponentsWindow>();
		_profilerWindow = serviceProvider.GetRequiredService<ProfilerWindow>();
		_sceneWindow = serviceProvider.GetRequiredService<SceneWindow>();
		_commandService.BindDeletionHandlers(_entitiesWindow, _assetsWindow);
	}

	public void Draw(EditorScene scene)
	{
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

		ImGui.DockSpace(ImGui.GetID("MainDockSpace"), Vector2.Zero, ImGuiDockNodeFlags.PassthruCentralNode);
		ImGui.End();
	}
}
