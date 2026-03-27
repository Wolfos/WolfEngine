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
	public static Entity SelectedEntity;
	public static bool HasSelectedEntity = false;
	private static bool _componentsWindowFocusRequested;

	private readonly IMenuBar _menuBar;
	private readonly IEditorModeState _editorModeState;

	private readonly EntitiesWindow _entitiesWindow;
	private readonly AssetsWindow _assetsWindow;
	private readonly AssetEditorWindow _assetEditorWindow;
	private readonly ComponentsWindow _componentsWindow;
	private readonly ProfilerWindow _profilerWindow;
	private readonly SceneWindow _sceneWindow;

	public EditorGui(
		IMenuBar menuBar,
		IEditorModeState editorModeState,
		IServiceProvider serviceProvider)
	{
		_menuBar = menuBar;
		_editorModeState = editorModeState;

		_entitiesWindow = serviceProvider.GetRequiredService<EntitiesWindow>();
		_assetsWindow = serviceProvider.GetRequiredService<AssetsWindow>();
		_assetEditorWindow = serviceProvider.GetRequiredService<AssetEditorWindow>();
		_componentsWindow = serviceProvider.GetRequiredService<ComponentsWindow>();
		_profilerWindow = serviceProvider.GetRequiredService<ProfilerWindow>();
		_sceneWindow = serviceProvider.GetRequiredService<SceneWindow>();
	}

	public void Draw(EditorScene scene)
	{
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

		using (FrameProfiler.Instance.Measure("Preferences"))
		{
			EditorPreferencesMenu.Draw();
		}
	}

	public static void SelectEntity(Entity entity, World world)
	{
		HasSelectedEntity = true;
		SelectedEntity = entity;
		world.GetComponentTypes(entity, SelectedComponentTypes);
		_componentsWindowFocusRequested = true;
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

	public static void ClearEntitySelection()
	{
		HasSelectedEntity = false;
		SelectedEntity = default;
		SelectedComponentTypes.Clear();
		_componentsWindowFocusRequested = false;
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
