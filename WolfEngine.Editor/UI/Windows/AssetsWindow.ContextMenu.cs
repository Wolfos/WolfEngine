using ImGuiNET;

namespace WolfEngine.Editor.UI;

public sealed partial class AssetsWindow
{
	private const string BrowserContextMenuId = "AssetBrowserContextMenu";
	private BrowserContextTarget? _browserContextTarget;
	private bool _openBrowserContextMenu;
	private bool _browserContextMenuVisible;

	private void RequestBrowserContextMenu(BrowserContextTarget target)
	{
		_browserContextTarget = target;
		_openBrowserContextMenu = true;
	}

	private void HandleFolderBackgroundContextMenu()
	{
		// Item and background menus share the press edge. A popup must not turn the release
		// into a new background click merely because it now blocks the item's hover state.
		if (_openBrowserContextMenu == false &&
			ImGui.IsMouseClicked(ImGuiMouseButton.Right) &&
			ImGui.IsWindowHovered() &&
			ImGui.IsAnyItemHovered() == false)
		{
			RequestBrowserContextMenu(BrowserContextTarget.ForCurrentFolder(_selection.SelectedFolderPath));
		}
	}

	private void DrawBrowserContextMenu(EditorScene scene)
	{
		_browserContextMenuVisible = false;
		if (_openBrowserContextMenu)
		{
			// Open at the Assets window scope so virtualization and folder-tree ID stacks
			// cannot change the menu's identity or its captured target on subsequent frames.
			ImGui.OpenPopup(BrowserContextMenuId);
			_openBrowserContextMenu = false;
		}

		if (ImGui.BeginPopup(BrowserContextMenuId) == false) return;

		_browserContextMenuVisible = true;
		try
		{
			if (_browserContextTarget is not { } target) return;

			if (target.Kind is BrowserContextKind.CurrentFolder or BrowserContextKind.Folder)
			{
				DrawFolderScopedContextMenu(target.FolderPath);
			}
			else if (target.SourceId is { } sourceId &&
				_lastBrowserModel?.SourcesBySourceId.TryGetValue(sourceId, out var source) == true)
			{
				var subAsset = target.Kind == BrowserContextKind.SubAsset;
				DrawSourceContextMenu(
					scene,
					source,
					target,
					subAsset ? "Rename Source Asset" : "Rename",
					subAsset ? "Delete Source Asset" : "Delete");
			}
			else
			{
				ImGui.CloseCurrentPopup();
			}
		}
		finally
		{
			ImGui.EndPopup();
		}
	}
}
