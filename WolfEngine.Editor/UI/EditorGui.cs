using System.Numerics;
using ImGuiNET;
using Microsoft.Extensions.DependencyInjection;
using WolfEngine.ECS;
using WolfEngine.Profiling;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

public class EditorGui
{
	// TODO: Maybe not public?
	public static readonly List<Type> SelectedComponentTypes = new();
	public static Entity SelectedEntity;
	public static bool HasSelectedEntity = false;

	private readonly IServiceProvider _serviceProvider;
	private readonly IComponentEditor _componentEditor;
	private readonly IMenuBar _menuBar;

	private readonly List<EditorWindow> _editorWindows = new();

	public EditorGui(
		IComponentEditor componentEditor,
		IMenuBar menuBar,
		IServiceProvider serviceProvider)
	{
		_componentEditor = componentEditor;
		_menuBar = menuBar;
		_serviceProvider = serviceProvider;
		
		NewWindow<EntitiesWindow>();
		NewWindow<AssetsWindow>();
		NewWindow<ProfilerWindow>();
		NewWindow<SceneWindow>(); }

	public void NewWindow<T>() where T : EditorWindow
	{
		var window = ActivatorUtilities.CreateInstance<T>(_serviceProvider);
		_editorWindows.Add(window);
	}

	public void Draw(EditorScene scene)
	{
		DockSpace();

		using (FrameProfiler.Instance.Measure("Menu Bar"))
		{
			_menuBar.Draw(scene);
		}

		foreach (var window in _editorWindows)
		{
			using (FrameProfiler.Instance.Measure(window.Name))
			{
				window.Draw(scene);
			}
		}

		// TODO: Refactor
		using (FrameProfiler.Instance.Measure("Components Window"))
		{
			ImGui.SetNextWindowPos(new Vector2(1041.0f, 0.0f), ImGuiCond.FirstUseEver);
			ImGui.SetNextWindowSize(new Vector2(239.0f, 720.0f), ImGuiCond.FirstUseEver);
			var pushedBoldTitle = ImGuiUiSystem.PushBoldFont();
			ImGui.Begin("Components");
			var pushedRegularContent = ImGuiUiSystem.PushRegularFont();
			if (HasSelectedEntity)
			{
				foreach (var componentType in SelectedComponentTypes)
				{
					_componentEditor.Draw(scene, SelectedEntity, componentType);
				}
			}

			ImGuiUiSystem.PopFontIfPushed(pushedRegularContent);
			ImGui.End();
			ImGuiUiSystem.PopFontIfPushed(pushedBoldTitle);
		}

		using (FrameProfiler.Instance.Measure("Preferences"))
		{
			EditorPreferencesMenu.Draw();
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
