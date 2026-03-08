using System.Reflection;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

public sealed class DataAssetTypeDescriptor
{
	public required Type Type { get; init; }
	public required string DisplayName { get; init; }
	public required string TypeName { get; init; }
}

public interface IDataAssetTypeRegistry
{
	IReadOnlyList<DataAssetTypeDescriptor> GetAll();
	bool TryGetDescriptor(string typeName, out DataAssetTypeDescriptor descriptor);
}

public sealed class DataAssetTypeRegistry : IDataAssetTypeRegistry
{
	private readonly IReadOnlyList<DataAssetTypeDescriptor> _descriptors;
	private readonly Dictionary<string, DataAssetTypeDescriptor> _descriptorsByTypeName;

	public DataAssetTypeRegistry()
	{
		var descriptors = new List<DataAssetTypeDescriptor>();

		foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			foreach (var type in GetLoadableTypes(assembly))
			{
				if (IsDataAssetType(type) == false)
				{
					continue;
				}

				var typeName = type.AssemblyQualifiedName;
				if (string.IsNullOrWhiteSpace(typeName) || descriptors.Any(descriptor => descriptor.TypeName == typeName))
				{
					continue;
				}

				descriptors.Add(new DataAssetTypeDescriptor
				{
					Type = type,
					DisplayName = type.Name,
					TypeName = typeName
				});
			}
		}

		descriptors.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.DisplayName, right.DisplayName));
		_descriptors = descriptors;
		_descriptorsByTypeName = descriptors.ToDictionary(descriptor => descriptor.TypeName, StringComparer.Ordinal);
	}

	public IReadOnlyList<DataAssetTypeDescriptor> GetAll() => _descriptors;

	public bool TryGetDescriptor(string typeName, out DataAssetTypeDescriptor descriptor)
	{
		if (string.IsNullOrWhiteSpace(typeName))
		{
			descriptor = null!;
			return false;
		}

		return _descriptorsByTypeName.TryGetValue(typeName, out descriptor!);
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

	private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
	{
		try
		{
			return assembly.GetTypes();
		}
		catch (ReflectionTypeLoadException exception)
		{
			return exception.Types.Where(type => type is not null)!;
		}
	}
}
