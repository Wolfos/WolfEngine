using ImGuiNET;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.UI;

internal sealed class AssetsWindowDragDropState
{
	private readonly float _previewRounding;
	private AssetBrowserDragTarget? _pressedTarget;
	private AssetBrowserDragTarget? _activeTarget;
	private string? _hoveredDropFolderPath;

	public AssetsWindowDragDropState(float previewRounding)
	{
		_previewRounding = previewRounding;
	}

	public void BeginFrame()
	{
		_hoveredDropFolderPath = null;
	}

	public void Press(AssetBrowserDragTarget dragTarget)
	{
		_pressedTarget = dragTarget;
	}

	public void RegisterFolderDropTarget(string folderPath)
	{
		if (_activeTarget is null || CanDropOnFolder(_activeTarget, folderPath) == false)
		{
			return;
		}

		if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem | ImGuiHoveredFlags.AllowWhenBlockedByPopup))
		{
			_hoveredDropFolderPath = folderPath;
		}
	}

	public void RegisterContentPaneDropTarget(string selectedFolderPath)
	{
		if (_activeTarget is null || ImGui.IsAnyItemHovered())
		{
			return;
		}

		if (CanDropOnFolder(_activeTarget, selectedFolderPath) &&
		    ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem | ImGuiHoveredFlags.AllowWhenBlockedByPopup))
		{
			_hoveredDropFolderPath = selectedFolderPath;
		}
	}

	public bool IsActiveDropTarget(string folderPath)
	{
		return _activeTarget is not null &&
		       _hoveredDropFolderPath is not null &&
		       string.Equals(_hoveredDropFolderPath, folderPath, StringComparison.OrdinalIgnoreCase);
	}

	public void Complete(
		IEditorProjectService projectService,
		Action<AssetBrowserDragTarget, string> onMoved,
		Action<string> showError)
	{
		if (_pressedTarget is not null && _activeTarget is null && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
		{
			_activeTarget = _pressedTarget;
		}

		if (_activeTarget is null)
		{
			if (ImGui.IsMouseDown(ImGuiMouseButton.Left) == false)
			{
				_pressedTarget = null;
			}

			return;
		}

		if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
		{
			ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
			AssetsWindowDrawing.DrawDragPreview(_activeTarget, _previewRounding);
			return;
		}

		var dragTarget = _activeTarget;
		var dropFolderPath = _hoveredDropFolderPath;
		_pressedTarget = null;
		_activeTarget = null;
		_hoveredDropFolderPath = null;

		if (dropFolderPath is null)
		{
			return;
		}

		try
		{
			var movedPath = MoveDragTarget(projectService, dragTarget, dropFolderPath);
			if (movedPath is not null)
			{
				onMoved(dragTarget, movedPath);
			}
		}
		catch (Exception ex)
		{
			showError($"Failed to move '{Path.GetFileName(dragTarget.RelativePath)}': {ex.Message}");
		}
	}

	public static string? MoveDragTarget(
		IEditorProjectService projectService,
		AssetBrowserDragTarget dragTarget,
		string targetFolderPath)
	{
		return dragTarget.Kind switch
		{
			DragTargetKind.Source => projectService.MoveAssetSourceToFolder(dragTarget.RelativePath, targetFolderPath),
			DragTargetKind.Folder => projectService.MoveFolderToFolder(dragTarget.RelativePath, targetFolderPath),
			_ => null
		};
	}

	private static bool CanDropOnFolder(AssetBrowserDragTarget dragTarget, string targetFolderPath)
	{
		return dragTarget.Kind != DragTargetKind.Folder ||
		       ProjectPathUtility.IsSameOrDescendant(targetFolderPath, dragTarget.RelativePath) == false;
	}
}
