using System.Numerics;
using ImGuiNET;
using WolfEngine.AssetPipeline;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

public sealed partial class AssetsWindow
{
	private Guid? _subAssetViewSource;
	private string _subAssetSearch = string.Empty;
	private AssetType? _subAssetType;
	private AssetsWindowSourceItem? _filteredSource;
	private string? _filteredSearch;
	private AssetType? _filteredType;
	private IReadOnlyList<AssetDatabaseEntry> _filteredSubAssets = [];
	private AssetsWindowSourceItem? _groupedSource;
	private (AssetType Type, int Count)[] _subAssetGroups = [];

	private void ToggleSubAssets(AssetsWindowSourceItem source)
	{
		_selection.ExpandedSourceId = AssetsWindowBrowserModelBuilder.ToggleExpandedSource(_selection.ExpandedSourceId, source.SourceId);
		if (_selection.ExpandedSourceId.HasValue)
		{
			var query = _assetSearchText.Trim();
			if (query.Length > 0 && source.SubAssets.Any(asset => asset.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
			{
				_subAssetViewSource = source.SourceId;
				_subAssetSearch = query;
				_subAssetType = null;
			}

			SelectAsset(source.PrimaryAsset, requestFocus: false, folderPath: source.FolderPath);
		}
	}

	private unsafe void DrawSubAssetPane(AssetsWindowSourceItem source, EditorScene scene, float height)
	{
		if (ReferenceEquals(_groupedSource, source) == false)
		{
			_groupedSource = source;
			_subAssetGroups = source.SubAssets.GroupBy(asset => asset.Type)
				.OrderBy(group => AssetBrowserAppearance.For(group.Key).Label, StringComparer.OrdinalIgnoreCase)
				.Select(group => (group.Key, group.Count()))
				.ToArray();
		}

		if (_subAssetViewSource != source.SourceId)
		{
			_subAssetViewSource = source.SourceId;
			_subAssetType = null;
			var query = _assetSearchText.Trim();
			_subAssetSearch = query.Length > 0 && source.SubAssets.Any(asset => asset.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
				? query
				: string.Empty;
		}

		ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8.0f, 5.0f));
		ImGui.PushStyleColor(
			ImGuiCol.ChildBg,
			EditorPalette.Tint(ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg], EditorPalette.Primary, 0.025f));
		ImGui.BeginChild("SubAssetPane", new Vector2(0.0f, height), ImGuiChildFlags.Borders);
		_browserControls["subAssetPane"] = BrowserRect(ImGui.GetWindowPos(), ImGui.GetWindowPos() + ImGui.GetWindowSize());
		if (ImGui.BeginTable("SubAssetHeading", 2, ImGuiTableFlags.SizingStretchProp))
		{
			ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthStretch);
			ImGui.TableSetupColumn("Close", ImGuiTableColumnFlags.WidthFixed, 24.0f);
			ImGui.TableNextRow();
			ImGui.TableSetColumnIndex(0);
			var bold = ImGuiUiSystem.PushBoldFont();
			ImGui.TextUnformatted("Sub-assets  /  " + AssetBrowserAppearance.Title(source.RelativeSourcePath));
			ImGuiUiSystem.PopFontIfPushed(bold);
			ImGui.TableSetColumnIndex(1);
			if (BrowserIconButton("CloseSubAssets", "close"))
			{
				_selection.ExpandedSourceId = null;
			}

			ImGui.EndTable();
		}

		var compact = height < 230.0f;
		if (compact == false)
		{
			DrawSubAssetFilters(source);
		}

		ImGui.SetNextItemWidth(MathF.Min(300.0f, MathF.Max(100.0f, ImGui.GetContentRegionAvail().X * 0.5f)));
		ImGui.InputTextWithHint("##SubAssetSearch", "Find sub-assets...", ref _subAssetSearch, 256);
		RecordBrowserControl("subAssetSearch");
		if (compact)
		{
			ImGui.SameLine();
			ImGui.SetNextItemWidth(MathF.Min(180.0f, MathF.Max(60.0f, ImGui.GetContentRegionAvail().X)));
			var label = _subAssetType.HasValue ? AssetBrowserAppearance.For(_subAssetType.Value).Label : "All types";
			if (ImGui.BeginCombo("##SubAssetType", label))
			{
				if (ImGui.Selectable("All types", _subAssetType is null))
				{
					_subAssetType = null;
				}

				foreach (var type in source.SubAssets.Select(asset => asset.Type).Distinct())
				{
					if (ImGui.Selectable(AssetBrowserAppearance.For(type).Label, _subAssetType == type))
					{
						_subAssetType = type;
					}
				}

				ImGui.EndCombo();
			}
		}

		if (ReferenceEquals(_filteredSource, source) == false || _filteredSearch != _subAssetSearch || _filteredType != _subAssetType)
		{
			_filteredSource = source;
			_filteredSearch = _subAssetSearch;
			_filteredType = _subAssetType;
			_filteredSubAssets = AssetBrowserAppearance.FilterSubAssets(source, _subAssetSearch, _subAssetType);
		}

		if (compact == false)
		{
			ImGui.SameLine();
			ImGui.TextDisabled($"{_filteredSubAssets.Count} of {source.SubAssets.Count}");
		}

		var showTypeColumn = ImGui.GetContentRegionAvail().X >= 480.0f;
		if (ImGui.BeginTable(
			"SubAssetRows",
			showTypeColumn ? 2 : 1,
			ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp,
			new Vector2(0.0f, MathF.Max(1.0f, ImGui.GetContentRegionAvail().Y))))
		{
			ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
			if (showTypeColumn)
			{
				ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 145.0f);
			}
			ImGui.TableSetupScrollFreeze(0, 1);
			ImGui.TableHeadersRow();
			var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
			try
			{
				clipper.Begin(_filteredSubAssets.Count);
				while (clipper.Step())
				{
					for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
					{
						DrawSubAssetRow(source, _filteredSubAssets[index], scene, showTypeColumn);
					}
				}
			}
			finally
			{
				clipper.Destroy();
				ImGui.EndTable();
			}
		}

		ImGui.EndChild();
		ImGui.PopStyleColor();
		ImGui.PopStyleVar();
	}

	private void DrawSubAssetFilters(AssetsWindowSourceItem source)
	{
		DrawSubAssetFilter(null, "All", source.SubAssets.Count);
		foreach (var (type, count) in _subAssetGroups)
		{
			var label = AssetBrowserAppearance.For(type).Label;
			var width = ImGui.CalcTextSize($"{label} {count}").X + ImGui.GetStyle().FramePadding.X * 2.0f;
			if (ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + width < ImGui.GetWindowPos().X + ImGui.GetWindowSize().X - ImGui.GetStyle().WindowPadding.X)
			{
				ImGui.SameLine();
			}

			DrawSubAssetFilter(type, label, count);
		}
	}

	private void DrawSubAssetFilter(AssetType? type, string label, int count)
	{
		var selected = _subAssetType == type;
		var color = type.HasValue ? AssetBrowserAppearance.For(type.Value).Color : EditorPalette.Primary;
		ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 13.0f);
		ImGui.PushStyleColor(ImGuiCol.Text, selected ? color : ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
		ImGui.PushStyleColor(
			ImGuiCol.Button,
			selected ? EditorPalette.Tint(ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg], color, 0.20f) : ImGui.GetStyle().Colors[(int)ImGuiCol.Button]);
		if (ImGui.Button($"{label} {count}##Filter{type?.ToString() ?? "All"}", new Vector2(0.0f, 26.0f)))
		{
			_subAssetType = type;
		}

		RecordBrowserControl("filter:" + (type?.ToString() ?? "All"));
		ImGui.PopStyleColor(2);
		ImGui.PopStyleVar();
	}

	private void DrawSubAssetRow(AssetsWindowSourceItem source, AssetDatabaseEntry asset, EditorScene scene, bool showTypeColumn)
	{
		ImGui.PushID(asset.Id.ToString());
		ImGui.TableNextRow();
		ImGui.TableSetColumnIndex(0);
		var selected = _assetSelectionService.SelectedAssetId == asset.Id;
		var appearance = AssetBrowserAppearance.For(asset.Type);
		var nameWidth = ImGui.GetContentRegionAvail().X;
		if (ImGui.Selectable("##SubAsset", selected, ImGuiSelectableFlags.SpanAllColumns, new Vector2(0.0f, 30.0f)))
		{
			_interactionState.SetFocusedWindow(EditorFocusedWindow.Assets);
			SelectAsset(asset, folderPath: source.FolderPath);
		}

		var min = ImGui.GetItemRectMin();
		var max = ImGui.GetItemRectMax();
		var doubleClicked = ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
		var rightClicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);
		var draw = ImGui.GetWindowDrawList();
		DrawBrowserIcon(draw, appearance.Icon, min + new Vector2(6.0f, 3.0f), 24.0f, appearance.Color);
		draw.AddText(
			min + new Vector2(38.0f, 7.0f),
			ImGui.GetColorU32(ImGuiCol.Text),
			AssetsWindowDrawing.ClipTextToWidth(asset.Name, MathF.Max(1.0f, nameWidth - 44.0f)));
		RecordBrowserItem(source.RelativeSourcePath, asset.Id, min, max, true);
		if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
		{
			DrawAssetTooltip(asset);
		}

		if (doubleClicked)
		{
			OpenAsset(asset, scene);
		}

		if (rightClicked)
		{
			SelectAsset(asset, requestFocus: false, folderPath: source.FolderPath);
			RequestBrowserContextMenu(BrowserContextTarget.ForSubAsset(source.RelativeSourcePath, source.SourceId, asset.Id));
		}


		if (showTypeColumn)
		{
			ImGui.TableSetColumnIndex(1);
			ImGui.TextColored(appearance.Color, appearance.Label);
		}
		ImGui.PopID();
	}
}
