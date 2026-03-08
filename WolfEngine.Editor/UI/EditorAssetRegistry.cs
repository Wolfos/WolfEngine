using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.UI;

public sealed class EditorAssetCreateMenuItem
{
	public required string Label { get; init; }
	public Func<EditorAssetCreationResult>? CreateAction { get; init; }
	public IReadOnlyList<EditorAssetCreateMenuItem> Children { get; init; } = [];
}

public interface IEditorAssetHandler
{
	AssetType AssetType { get; }
	string DisplayName { get; }
	string ThumbnailLabel { get; }
	string GetSubtitle(AssetDatabaseEntry asset);
	IReadOnlyList<EditorAssetCreateMenuItem> GetCreateMenuItems();
	void DrawEditor(AssetDatabaseEntry asset);
}

public interface IEditorAssetHandlerRegistry
{
	IReadOnlyList<IEditorAssetHandler> GetAll();
	IReadOnlyList<EditorAssetCreateMenuItem> GetCreateMenuItems();
	bool TryGetHandler(AssetType assetType, out IEditorAssetHandler handler);
}

public sealed class EditorAssetHandlerRegistry : IEditorAssetHandlerRegistry
{
	private readonly IReadOnlyList<IEditorAssetHandler> _handlers;
	private readonly Dictionary<AssetType, IEditorAssetHandler> _handlersByType;

	public EditorAssetHandlerRegistry(IEnumerable<IEditorAssetHandler> handlers)
	{
		_handlers = handlers.OrderBy(handler => handler.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
		_handlersByType = _handlers.ToDictionary(handler => handler.AssetType);
	}

	public IReadOnlyList<IEditorAssetHandler> GetAll() => _handlers;

	public IReadOnlyList<EditorAssetCreateMenuItem> GetCreateMenuItems()
	{
		return _handlers
			.SelectMany(handler => handler.GetCreateMenuItems())
			.OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	public bool TryGetHandler(AssetType assetType, out IEditorAssetHandler handler)
	{
		return _handlersByType.TryGetValue(assetType, out handler!);
	}
}

public sealed class TextureEditorAssetHandler : IEditorAssetHandler
{
	private readonly TextureAssetEditor _editor;

	public TextureEditorAssetHandler(TextureAssetEditor editor)
	{
		_editor = editor ?? throw new ArgumentNullException(nameof(editor));
	}

	public AssetType AssetType => AssetType.Texture2D;
	public string DisplayName => "Texture";
	public string ThumbnailLabel => "TEX";

	public string GetSubtitle(AssetDatabaseEntry asset)
	{
		return asset.TextureSummary is null
			? "Texture"
			: $"Texture | {asset.TextureSummary.Width}x{asset.TextureSummary.Height} | {asset.TextureSummary.SourceExtension}";
	}

	public IReadOnlyList<EditorAssetCreateMenuItem> GetCreateMenuItems() => [];

	public void DrawEditor(AssetDatabaseEntry asset)
	{
		_editor.Draw(asset);
	}
}

public sealed class MaterialEditorAssetHandler : IEditorAssetHandler
{
	private readonly MaterialAssetEditor _editor;
	private readonly IMaterialAssetCreator _materialAssetCreator;

	public MaterialEditorAssetHandler(MaterialAssetEditor editor, IMaterialAssetCreator materialAssetCreator)
	{
		_editor = editor ?? throw new ArgumentNullException(nameof(editor));
		_materialAssetCreator = materialAssetCreator ?? throw new ArgumentNullException(nameof(materialAssetCreator));
	}

	public AssetType AssetType => AssetType.Material;
	public string DisplayName => "Material";
	public string ThumbnailLabel => "MAT";

	public string GetSubtitle(AssetDatabaseEntry asset)
	{
		return asset.MaterialSummary is null
			? "Material"
			: $"Material | {asset.MaterialSummary.MaterialType}";
	}

	public IReadOnlyList<EditorAssetCreateMenuItem> GetCreateMenuItems()
	{
		return
		[
			new EditorAssetCreateMenuItem
			{
				Label = "Material",
				CreateAction = () => _materialAssetCreator.CreateMaterial()
			}
		];
	}

	public void DrawEditor(AssetDatabaseEntry asset)
	{
		_editor.Draw(asset);
	}
}

public sealed class DataEditorAssetHandler : IEditorAssetHandler
{
	private readonly DataAssetEditor _editor;
	private readonly IDataAssetCreator _dataAssetCreator;
	private readonly IDataAssetTypeRegistry _dataAssetTypeRegistry;

	public DataEditorAssetHandler(
		DataAssetEditor editor,
		IDataAssetCreator dataAssetCreator,
		IDataAssetTypeRegistry dataAssetTypeRegistry)
	{
		_editor = editor ?? throw new ArgumentNullException(nameof(editor));
		_dataAssetCreator = dataAssetCreator ?? throw new ArgumentNullException(nameof(dataAssetCreator));
		_dataAssetTypeRegistry = dataAssetTypeRegistry ?? throw new ArgumentNullException(nameof(dataAssetTypeRegistry));
	}

	public AssetType AssetType => AssetType.DataAsset;
	public string DisplayName => "Data Asset";
	public string ThumbnailLabel => "DATA";

	public string GetSubtitle(AssetDatabaseEntry asset)
	{
		return asset.DataAssetSummary is null
			? "Data Asset"
			: $"Data Asset | {asset.DataAssetSummary.DisplayName}";
	}

	public IReadOnlyList<EditorAssetCreateMenuItem> GetCreateMenuItems()
	{
		var children = _dataAssetTypeRegistry.GetAll()
			.Select(descriptor => new EditorAssetCreateMenuItem
			{
				Label = descriptor.DisplayName,
				CreateAction = () => _dataAssetCreator.CreateDataAsset(descriptor.Type)
			})
			.ToList();

		return
		[
			new EditorAssetCreateMenuItem
			{
				Label = "Data Asset",
				Children = children
			}
		];
	}

	public void DrawEditor(AssetDatabaseEntry asset)
	{
		_editor.Draw(asset);
	}
}
