using System.Numerics;
using ImGuiNET;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.UI;

public sealed partial class AssetsWindow
{
	private float _folderTreeWidth = 240.0f;
	private float _subAssetPaneHeight = 320.0f;
	private const float PaneSplitterSize = 6.0f;

	private void DrawFolderTreeSplitter()
	{
		ImGui.InvisibleButton(
			"##AssetTreeSplitter",
			new Vector2(PaneSplitterSize, MathF.Max(1.0f, ImGui.GetContentRegionAvail().Y)));
		RecordBrowserControl("treeSplitter");
		if (ImGui.IsItemHovered() || ImGui.IsItemActive())
		{
			ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
		}

		if (ImGui.IsItemActive())
		{
			_folderTreeWidth = Math.Clamp(
				_folderTreeWidth + ImGui.GetIO().MouseDelta.X,
				160.0f,
				MathF.Max(160.0f, ImGui.GetWindowSize().X * 0.45f));
		}
	}

	private void DrawContentArea(AssetsWindowBrowserModel model, AssetsWindowFolderNode selectedFolder, EditorScene scene)
	{
		ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 0.0f));
		ImGui.BeginGroup();
		DrawBreadcrumbRow(selectedFolder.RelativePath);
		AssetsWindowDrawing.DrawHorizontalPaneSeparator(PaneSeparatorThickness);
		var contents = AssetsWindowBrowserModelBuilder.GetFolderContents(selectedFolder, _assetSearchText);
		_lastContents = contents;
		AssetsWindowSourceItem? expanded = null;
		if (_selection.ExpandedSourceId is { } sourceId && model.SourcesBySourceId.TryGetValue(sourceId, out var source))
		{
			expanded = source;
		}

		var available = ImGui.GetContentRegionAvail();
		var showSubAssets = expanded is not null;
		var focusSubAssets = showSubAssets && available.Y < 360.0f;
		var subHeight = focusSubAssets
			? MathF.Max(1.0f, available.Y - 24.0f)
			: showSubAssets
				? Math.Clamp(_subAssetPaneHeight, 180.0f, MathF.Max(180.0f, available.Y * 0.65f))
				: 0.0f;
		var gridHeight = MathF.Max(1.0f, available.Y - 24.0f - (showSubAssets ? subHeight + PaneSplitterSize : 0.0f));
		if (focusSubAssets == false)
		{
			AssetsWindowDrawing.PushPaneStyle();
			ImGui.PushStyleColor(ImGuiCol.ChildBg, ImGui.GetStyle().Colors[(int)ImGuiCol.PopupBg]);
			ImGui.BeginChild("AssetsContentPane", new Vector2(0.0f, gridHeight), ImGuiChildFlags.None);
			_browserControls["grid"] = BrowserRect(ImGui.GetWindowPos(), ImGui.GetWindowPos() + ImGui.GetWindowSize());
			DrawCurrentFolderContents(contents, scene);
			if (IsReadOnlyBrowserFolder(_selection.SelectedFolderPath) == false)
			{
				_dragDrop.RegisterContentPaneDropTarget(_selection.SelectedFolderPath);
			}

			HandleFolderBackgroundContextMenu();

			ImGui.EndChild();
			ImGui.PopStyleColor(3);
		}

		if (showSubAssets)
		{
			if (focusSubAssets == false)
			{
				ImGui.InvisibleButton("##SubAssetSplitter", new Vector2(MathF.Max(1.0f, available.X), PaneSplitterSize));
				RecordBrowserControl("subAssetSplitter");
				if (ImGui.IsItemHovered() || ImGui.IsItemActive())
				{
					ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNS);
				}

				if (ImGui.IsItemActive())
				{
					_subAssetPaneHeight = subHeight - ImGui.GetIO().MouseDelta.Y;
				}
			}

			DrawSubAssetPane(expanded!, scene, subHeight);
		}

		var status = $"{contents.Folders.Count} folders  /  {contents.Sources.Count} assets";
		ImGui.TextDisabled(status + (contents.IsSearchActive ? "  /  Search includes subfolders" : string.Empty));
		ImGui.EndGroup();
		ImGui.PopStyleVar();
	}

	private void DrawBreadcrumbRow(string relativeFolderPath)
	{
		if (ImGui.BeginTable("AssetNavigation", 2, ImGuiTableFlags.SizingStretchProp) == false)
		{
			return;
		}

		var searchWidth = Math.Clamp(ImGui.GetContentRegionAvail().X * 0.3f, 130.0f, 280.0f);
		ImGui.TableSetupColumn("Location", ImGuiTableColumnFlags.WidthStretch);
		ImGui.TableSetupColumn("Search", ImGuiTableColumnFlags.WidthFixed, searchWidth);
		ImGui.TableNextRow();
		ImGui.TableSetColumnIndex(0);
		DrawBreadcrumbs(relativeFolderPath);
		ImGui.TableSetColumnIndex(1);
		ImGui.SetNextItemWidth(MathF.Max(40.0f, ImGui.GetContentRegionAvail().X - 30.0f));
		ImGui.InputTextWithHint("##AssetsSearch", "Search assets...", ref _assetSearchText, 256);
		RecordBrowserControl("search");
		ImGui.SameLine();
		if (BrowserIconButton("ClearAssetSearch", "close"))
		{
			_assetSearchText = string.Empty;
		}

		ImGui.EndTable();
	}

	private void DrawBreadcrumbs(string relativeFolderPath)
	{
		var parts = relativeFolderPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
		var fullWidth = parts.Sum(part => ImGui.CalcTextSize(part).X + 38.0f);
		var compact = parts.Length > 3 && fullWidth > ImGui.GetContentRegionAvail().X;
		for (var index = 0; index < parts.Length; index++)
		{
			if (index > 0)
			{
				ImGui.SameLine(0.0f, 4.0f);
				ImGui.TextDisabled(">");
				ImGui.SameLine(0.0f, 4.0f);
			}

			if (compact && index == 1)
			{
				if (ImGui.SmallButton("...##AncestorFolders"))
				{
					ImGui.OpenPopup("AncestorFolders");
				}

				if (ImGui.BeginPopup("AncestorFolders"))
				{
					for (var ancestor = 1; ancestor < parts.Length - 2; ancestor++)
					{
						if (ImGui.MenuItem(parts[ancestor]))
						{
							SelectFolder(string.Join('/', parts.Take(ancestor + 1)));
						}
					}

					ImGui.EndPopup();
				}

				index = parts.Length - 3;
				continue;
			}

			var path = string.Join('/', parts.Take(index + 1));
			ImGui.PushID(path);
			if (index == parts.Length - 1)
			{
				ImGui.PushStyleColor(ImGuiCol.Text, EditorPalette.Primary);
			}

			var label = AssetsWindowDrawing.ClipTextToWidth(parts[index], MathF.Max(40.0f, MathF.Min(180.0f, ImGui.GetContentRegionAvail().X - 12.0f)));
			if (ImGui.SmallButton(label))
			{
				SelectFolder(path);
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip(path);
			}

			if (index == parts.Length - 1)
			{
				ImGui.PopStyleColor();
			}

			ImGui.PopID();
		}
	}

	private bool BrowserIconButton(string id, string icon)
	{
		ImGui.InvisibleButton(id, new Vector2(24.0f));
		var min = ImGui.GetItemRectMin();
		if (ImGui.IsItemHovered())
		{
			ImGui.GetWindowDrawList().AddRectFilled(min, ImGui.GetItemRectMax(), ImGui.GetColorU32(ImGuiCol.HeaderHovered), 4.0f);
		}

		DrawBrowserIcon(
			ImGui.GetWindowDrawList(),
			icon,
			min,
			24.0f,
			ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
		RecordBrowserControl(id);
		return ImGui.IsItemClicked();
	}
}
