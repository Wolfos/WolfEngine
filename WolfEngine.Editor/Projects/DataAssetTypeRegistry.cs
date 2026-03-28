using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

public sealed class DataAssetTypeDescriptor
{
	public required Type Type { get; init; }
	public required string DisplayName { get; init; }
	public required string QualifiedDisplayName { get; init; }
	public required string TypeName { get; init; }
}

public interface IDataAssetTypeRegistry
{
	IReadOnlyList<DataAssetTypeDescriptor> GetAll();
	bool TryGetDescriptor(string typeName, out DataAssetTypeDescriptor descriptor);
}

public sealed class DataAssetTypeRegistry : IDataAssetTypeRegistry
{
	private readonly IProjectTypeCatalog _projectTypeCatalog;

	public DataAssetTypeRegistry(IProjectTypeCatalog projectTypeCatalog)
	{
		_projectTypeCatalog = projectTypeCatalog ?? throw new ArgumentNullException(nameof(projectTypeCatalog));
	}

	public IReadOnlyList<DataAssetTypeDescriptor> GetAll()
	{
		return _projectTypeCatalog.GetDataAssetTypes()
			.Select(CreateDescriptor)
			.ToList();
	}

	public bool TryGetDescriptor(string typeName, out DataAssetTypeDescriptor descriptor)
	{
		if (_projectTypeCatalog.TryGetDescriptor(typeName, out var projectTypeDescriptor) == false || IsDataAssetType(projectTypeDescriptor.Type) == false)
		{
			descriptor = null!;
			return false;
		}

		descriptor = CreateDescriptor(projectTypeDescriptor);
		return true;
	}

	private static bool IsDataAssetType(Type? type)
	{
		if (type is null ||
		    typeof(IDataAsset).IsAssignableFrom(type) == false ||
		    type.IsClass == false ||
		    type.IsAbstract ||
		    type.IsInterface ||
		    type.IsGenericTypeDefinition ||
		    type.ContainsGenericParameters)
		{
			return false;
		}

		return type.GetConstructor(Type.EmptyTypes) is not null;
	}

	private static DataAssetTypeDescriptor CreateDescriptor(ProjectTypeDescriptor descriptor)
	{
		return new DataAssetTypeDescriptor
		{
			Type = descriptor.Type,
			DisplayName = descriptor.DisplayName,
			QualifiedDisplayName = descriptor.QualifiedDisplayName,
			TypeName = descriptor.TypeName
		};
	}
}
