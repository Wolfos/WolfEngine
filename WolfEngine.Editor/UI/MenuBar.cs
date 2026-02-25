using System.Runtime.InteropServices;
using ImGuiNET;
using WolfEngine.ECS;
using WolfEngine.Utility;

namespace WolfEngine.Editor.UI;

public interface IMenuBar
{
	public void Draw(EditorScene scene);
}
public class MenuBar: IMenuBar
{
	private readonly IFileDialogService _fileDialogService;
	private readonly ISceneBuilder _sceneBuilder;
	private readonly FramerateTool _framerateTool;

	public MenuBar(IFileDialogService fileDialogService, ISceneBuilder sceneBuilder, FramerateTool framerateTool)
	{
		_fileDialogService = fileDialogService;
		_sceneBuilder = sceneBuilder;
		_framerateTool = framerateTool;
	}

	public void Draw(EditorScene scene)
	{
		if (ImGui.BeginMainMenuBar() == false) return;

		if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
		{
			const float macTitlebarButtonInset = 70.0f;
			if (ImGui.GetCursorPosX() < macTitlebarButtonInset)
			{
				ImGui.SetCursorPosX(macTitlebarButtonInset);
			}
		}
		
		if (ImGui.BeginMenu("File")) {
			if (ImGui.MenuItem("Preferences"))
			{
				EditorPreferencesMenu.Open();
			}
			ImGui.EndMenu();
		}
		if (ImGui.BeginMenu("Edit")) {
			ImGui.EndMenu();
		}
		if (ImGui.BeginMenu("Import")) {
			if (ImGui.MenuItem("Import 3D file"))
			{
				var path = _fileDialogService.OpenFile(new FileDialogOptions
				{
					Title = "Import 3D file",
					AllowedExtensions = ["gltf", "glb", "fbx"]
				});
				if (string.IsNullOrEmpty(path) == false)
				{
					_sceneBuilder.Import3DScene(path, scene.World);
				}
			}
			ImGui.EndMenu();
		}

		_framerateTool.DrawRightAlignedInMenuBar();

		ImGui.EndMainMenuBar();
	}
}
