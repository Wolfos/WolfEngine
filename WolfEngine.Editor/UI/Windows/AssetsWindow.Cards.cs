using System.Numerics;
using ImGuiNET;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.UI;

public sealed partial class AssetsWindow
{
	private const float CardHeight = 160.0f;
	private const float CardHeaderHeight = 132.0f;
	private const float GridMinItemWidth = 164.0f;
	private unsafe void DrawCurrentFolderContents(AssetsWindowFolderContents contents, EditorScene scene)
	{
		if (contents.Folders.Count == 0 && contents.Sources.Count == 0)
		{
			ImGui.TextDisabled(contents.IsSearchActive ? "No assets match the search." : "This folder is empty.");
			return;
		}

		var columns = Math.Max(1, (int)(ImGui.GetContentRegionAvail().X / GridMinItemWidth));
		ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(6.0f));
		ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8.0f, 4.0f));
		if (ImGui.BeginTable("AssetsGrid", columns, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.PadOuterX))
		{
			var count = contents.Folders.Count + contents.Sources.Count;
			var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
			try
			{
				clipper.Begin((count + columns - 1) / columns);
				while (clipper.Step())
				{
					for (var row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
					{
						ImGui.TableNextRow();
						for (var column = 0; column < columns; column++)
						{
							var index = row * columns + column;
							if (index >= count)
							{
								break;
							}

							ImGui.TableSetColumnIndex(column);
							if (index < contents.Folders.Count)
							{
								DrawFolderCard(contents.Folders[index]);
							}
							else
							{
								DrawSourceCard(contents.Sources[index - contents.Folders.Count], scene);
							}
						}
					}
				}
			}
			finally
			{
				clipper.Destroy();
				ImGui.EndTable();
			}
		}

		ImGui.PopStyleVar(2);
	}

	private void DrawFolderCard(AssetsWindowFolderNode folder)
	{
		ImGui.PushID(folder.RelativePath);
		ImGui.InvisibleButton("FolderCardButton", new Vector2(ImGui.GetContentRegionAvail().X, CardHeight));
		var min = ImGui.GetItemRectMin();
		var max = ImGui.GetItemRectMax();
		var hovered = ImGui.IsItemHovered();
		var leftClicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
		var rightClicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);
		var doubleClicked = leftClicked && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
		var readOnly = IsReadOnlyBrowserFolder(folder.RelativePath);
		if (leftClicked && readOnly == false)
		{
			_dragDrop.Press(AssetBrowserDragTarget.ForFolder(folder.RelativePath));
		}

		if (readOnly == false)
		{
			_dragDrop.RegisterFolderDropTarget(folder.RelativePath);
		}

		var accent = AssetBrowserAppearance.FolderColor;
		var draw = ImGui.GetWindowDrawList();
		DrawCardSurface(draw, min, max, accent, false, hovered);
		DrawTypePreview(draw, min + new Vector2(8.0f), new Vector2(max.X - 8.0f, min.Y + 72.0f), accent, "folder");
		AssetsWindowDrawing.DrawCardTitle(draw, folder.Name, min + new Vector2(10.0f, 82.0f), max.X - min.X - 20.0f);
		draw.AddText(min + new Vector2(10.0f, 118.0f), ImGui.GetColorU32(accent), "Folder");
		if (_dragDrop.IsActiveDropTarget(folder.RelativePath))
		{
			draw.AddRect(min, max, ImGui.GetColorU32(EditorPalette.Primary), 6.0f, ImDrawFlags.None, 2.0f);
		}

		RecordBrowserItem(folder.RelativePath, null, min, max);
		if (hovered && ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
		{
			ImGui.SetTooltip(folder.RelativePath);
		}

		if (doubleClicked)
		{
			_interactionState.SetFocusedWindow(EditorFocusedWindow.Assets);
			SelectFolder(folder.RelativePath);
		}

		if (rightClicked)
		{
			_interactionState.SetFocusedWindow(EditorFocusedWindow.Assets);
			RequestBrowserContextMenu(BrowserContextTarget.ForFolder(folder.RelativePath));
		}


		ImGui.PopID();
	}

	private void DrawSourceCard(AssetsWindowSourceItem source, EditorScene scene)
	{
		ImGui.PushID(source.SourceId.ToString());
		ImGui.BeginGroup();
		var width = ImGui.GetContentRegionAvail().X;
		var hasChildren = source.SubAssets.Count > 0;
		ImGui.InvisibleButton("SourceHeaderButton", new Vector2(width, hasChildren ? CardHeaderHeight : CardHeight));
		var min = ImGui.GetItemRectMin();
		var max = min + new Vector2(width, CardHeight);
		var leftClicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
		var rightClicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);
		var doubleClicked = leftClicked && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
		var hovered = ImGui.IsItemHovered();
		var appearance = AssetBrowserAppearance.For(source.PrimaryAsset.Type);
		var selected = _assetSelectionService.SelectedAssetId is { } id && (source.PrimaryAsset.Id == id || source.SubAssets.Any(asset => asset.Id == id));
		var expanded = _selection.ExpandedSourceId == source.SourceId;
		var draw = ImGui.GetWindowDrawList();
		DrawCardSurface(draw, min, max, appearance.Color, selected || expanded, hovered);
		var previewMin = min + new Vector2(8.0f);
		var previewMax = new Vector2(max.X - 8.0f, min.Y + 72.0f);
		DrawAssetThumbnail(draw, previewMin, previewMax, source.PrimaryAsset);
		AssetsWindowDrawing.DrawCardTitle(
			draw,
			AssetBrowserAppearance.Title(source.RelativeSourcePath),
			min + new Vector2(10.0f, 82.0f),
			width - 20.0f);
		draw.AddText(min + new Vector2(10.0f, 118.0f), ImGui.GetColorU32(appearance.Color), appearance.Label);
		RecordBrowserItem(source.RelativeSourcePath, source.PrimaryAsset.Id, min, max);
		if (hovered && ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
		{
			DrawAssetTooltip(source.PrimaryAsset);
		}

		if (leftClicked)
		{
			if (source.IsReadOnly == false)
			{
				_dragDrop.Press(AssetBrowserDragTarget.ForSource(source.RelativeSourcePath));
			}

			_interactionState.SetFocusedWindow(EditorFocusedWindow.Assets);
			SelectAsset(source.PrimaryAsset, folderPath: source.FolderPath);
			if (_selection.ExpandedSourceId.HasValue && expanded == false)
			{
				_selection.ExpandedSourceId = hasChildren ? source.SourceId : null;
			}

			if (doubleClicked)
			{
				OpenAsset(source.PrimaryAsset, scene);
			}
		}

		if (rightClicked)
		{
			_interactionState.SetFocusedWindow(EditorFocusedWindow.Assets);
			SelectAsset(source.PrimaryAsset, requestFocus: false, folderPath: source.FolderPath);
			RequestBrowserContextMenu(BrowserContextTarget.ForSource(source.RelativeSourcePath, source.SourceId, source.PrimaryAsset.Id));
		}


		if (hasChildren)
		{
			ImGui.InvisibleButton("SubAssets", new Vector2(width, 24.0f));
			var buttonMin = ImGui.GetItemRectMin();
			var buttonMax = ImGui.GetItemRectMax();
			var buttonColor = expanded ? EditorPalette.Primary : appearance.Color;
			var surface = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg];
			draw.AddRectFilled(
				buttonMin + new Vector2(1.0f, 0.0f),
				buttonMax - Vector2.One,
				ImGui.GetColorU32(EditorPalette.Tint(surface, buttonColor, ImGui.IsItemHovered() ? 0.22f : 0.10f)),
				5.0f);
			DrawBrowserIcon(draw, expanded ? "asset_expand_more" : "asset_chevron_right", buttonMin, 24.0f, buttonColor);
			draw.AddText(
				buttonMin + new Vector2(27.0f, 4.0f),
				ImGui.GetColorU32(buttonColor),
				$"{source.SubAssets.Count} sub-assets");
			_browserControls[$"expand:{source.SourceId}"] = BrowserRect(buttonMin, buttonMax);
			if (ImGui.IsItemClicked())
			{
				ToggleSubAssets(source);
			}
		}

		ImGui.EndGroup();
		ImGui.PopID();
	}

	private void DrawCardSurface(ImDrawListPtr draw, Vector2 min, Vector2 max, Vector4 accent, bool selected, bool hovered)
	{
		var surface = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg];
		var fill = EditorPalette.Tint(surface, selected ? EditorPalette.Primary : Vector4.One, selected ? 0.10f : 0.025f);
		draw.AddRectFilled(min, max, ImGui.GetColorU32(fill), 6.0f);
		var border = selected ? EditorPalette.Primary : EditorPalette.Tint(surface, accent, hovered ? 0.65f : 0.23f);
		draw.AddRect(min, max, ImGui.GetColorU32(border), 6.0f, ImDrawFlags.None, selected ? 1.5f : 1.0f);
	}

	private void DrawBrowserIcon(ImDrawListPtr draw, string icon, Vector2 min, float size, Vector4 color)
	{
		if (_icons.TryGet(icon, out var texture))
		{
			draw.AddImage(texture, min, min + new Vector2(size), Vector2.Zero, Vector2.One, ImGui.GetColorU32(color));
		}
		else
		{
			draw.AddRect(min + new Vector2(4.0f), min + new Vector2(size - 4.0f), ImGui.GetColorU32(color), 3.0f);
		}
	}

	private void DrawTypePreview(ImDrawListPtr draw, Vector2 min, Vector2 max, Vector4 color, string? icon)
	{
		var surface = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg];
		draw.AddRectFilled(min, max, ImGui.GetColorU32(EditorPalette.Tint(surface, color, 0.08f)), 4.0f);
		var iconMin = (min + max) * 0.5f - new Vector2(12.0f);
		if (icon is not null)
		{
			DrawBrowserIcon(draw, icon, iconMin, 24.0f, color);
		}
	}

	private void DrawAssetThumbnail(ImDrawListPtr draw, Vector2 min, Vector2 max, AssetDatabaseEntry asset)
	{
		var appearance = AssetBrowserAppearance.For(asset.Type);
		nint texture = 0;
		var state = asset.Type == AssetType.Texture2D ? _assetThumbnailLoader.GetTextureThumbnailState(asset, out texture) : AssetThumbnailState.Unavailable;
		var loading = state == AssetThumbnailState.Loading || (asset.Type == AssetType.Scene && _commandService.LoadingSceneAssetId == asset.Id);
		DrawTypePreview(
			draw,
			min,
			max,
			appearance.Color,
			loading || state == AssetThumbnailState.Ready ? null : appearance.Icon);
		if (loading)
		{
			EditorGui.DrawLoadingSpinner(draw, (min + max) * 0.5f);
			return;
		}

		if (asset.Type != AssetType.Texture2D)
		{
			return;
		}

		if (state == AssetThumbnailState.Ready)
		{
			var size = max - min;
			if (asset.TryGetSummary<TextureAssetSummary>(out var summary) && summary.Width > 0 && summary.Height > 0)
			{
				var scale = MathF.Min(size.X / summary.Width, size.Y / summary.Height);
				size = new Vector2(summary.Width, summary.Height) * scale;
			}

			var imageMin = (min + max - size) * 0.5f;
			draw.AddImage(texture, imageMin, imageMin + size);
		}
	}

	private void DrawAssetTooltip(AssetDatabaseEntry asset)
	{
		ImGui.BeginTooltip();
		ImGui.TextUnformatted(asset.Name);
		ImGui.TextColored(AssetBrowserAppearance.For(asset.Type).Color, AssetBrowserAppearance.For(asset.Type).Label);
		ImGui.TextDisabled(asset.RelativeSourcePath);
		if (_assetHandlerRegistry.TryGetHandler(asset.Type, out var handler))
		{
			ImGui.TextUnformatted(handler.GetSubtitle(asset));
		}

		ImGui.EndTooltip();
	}
}
