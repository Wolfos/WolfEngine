using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Xml.Linq;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;

namespace WolfEngine.Editor.Projects;

public sealed class ProjectTypeDescriptor
{
	public required Type Type { get; init; }
	public required string DisplayName { get; init; }
	public required string QualifiedDisplayName { get; init; }
	public required string TypeName { get; init; }
	public required string StableTypeId { get; init; }
	public required bool IsGameplayType { get; init; }
}

public interface IProjectTypeCatalog
{
	IReadOnlyList<ProjectTypeDescriptor> GetAll();
	IReadOnlyList<ProjectTypeDescriptor> GetComponentTypes();
	IReadOnlyList<ProjectTypeDescriptor> GetDataAssetTypes();
	bool TryGetDescriptor(string typeName, out ProjectTypeDescriptor descriptor);
	void ClearCaches();
}

public interface IProjectTypeResolver
{
	string GetTypeName(Type type);
	string GetStableTypeId(Type type);
	bool TryResolveType(string typeName, out Type type);
	bool TryResolveStableTypeId(string stableTypeId, out Type type);
}

public sealed class ProjectTypeCatalog : IProjectTypeCatalog, IProjectTypeResolver
{
	private readonly Func<IEditorProjectService> _projectServiceAccessor;
	private readonly IGameplayAssemblyHost? _gameplayAssemblyHost;
	private readonly object _sync = new();
	private string? _loadedProjectRootPath;
	private long _loadedGameplayGeneration = -1;
	private IReadOnlyList<ProjectTypeDescriptor>? _allDescriptors;
	private IReadOnlyList<ProjectTypeDescriptor>? _componentDescriptors;
	private IReadOnlyList<ProjectTypeDescriptor>? _dataAssetDescriptors;
	private Dictionary<string, ProjectTypeDescriptor>? _descriptorsByTypeName;
	private Dictionary<string, ProjectTypeDescriptor>? _descriptorsByStableTypeId;

	public ProjectTypeCatalog(Func<IEditorProjectService> projectServiceAccessor, IGameplayAssemblyHost? gameplayAssemblyHost = null)
	{
		_projectServiceAccessor = projectServiceAccessor ?? throw new ArgumentNullException(nameof(projectServiceAccessor));
		_gameplayAssemblyHost = gameplayAssemblyHost;
	}

	public IReadOnlyList<ProjectTypeDescriptor> GetAll()
	{
		EnsureCatalogLoaded();
		return _allDescriptors!;
	}

	public IReadOnlyList<ProjectTypeDescriptor> GetComponentTypes()
	{
		EnsureCatalogLoaded();
		return _componentDescriptors!;
	}

	public IReadOnlyList<ProjectTypeDescriptor> GetDataAssetTypes()
	{
		EnsureCatalogLoaded();
		return _dataAssetDescriptors!;
	}

	public bool TryGetDescriptor(string typeName, out ProjectTypeDescriptor descriptor)
	{
		EnsureCatalogLoaded();
		if (string.IsNullOrWhiteSpace(typeName))
		{
			descriptor = null!;
			return false;
		}

		return _descriptorsByTypeName!.TryGetValue(typeName, out descriptor!);
	}

	public void ClearCaches()
	{
		lock (_sync)
		{
			ResetCache(currentProjectRoot: null, currentGameplayGeneration: -1);
		}
	}

	public string GetTypeName(Type type)
	{
		return ProjectTypeResolverUtility.GetTypeName(type);
	}

	public string GetStableTypeId(Type type)
	{
		return ProjectTypeResolverUtility.GetStableTypeId(type);
	}

	public bool TryResolveType(string typeName, out Type type)
	{
		if (TryGetDescriptor(typeName, out var descriptor))
		{
			type = descriptor.Type;
			return true;
		}

		type = null!;
		return false;
	}

	public bool TryResolveStableTypeId(string stableTypeId, out Type type)
	{
		EnsureCatalogLoaded();
		if (string.IsNullOrWhiteSpace(stableTypeId))
		{
			type = null!;
			return false;
		}

		if (_descriptorsByStableTypeId!.TryGetValue(stableTypeId, out var descriptor))
		{
			type = descriptor.Type;
			return true;
		}

		type = null!;
		return false;
	}

	private void EnsureCatalogLoaded()
	{
		var currentProjectRoot = GetCurrentProjectRootPath();
		var currentGameplayGeneration = _gameplayAssemblyHost?.CurrentGeneration ?? 0;
		lock (_sync)
		{
			if (string.Equals(currentProjectRoot, _loadedProjectRootPath, StringComparison.OrdinalIgnoreCase) == false ||
			    currentGameplayGeneration != _loadedGameplayGeneration)
			{
				ResetCache(currentProjectRoot, currentGameplayGeneration);
			}

			if (_allDescriptors is not null)
			{
				return;
			}

			var descriptors = new List<ProjectTypeDescriptor>();
			var descriptorsByTypeName = new Dictionary<string, ProjectTypeDescriptor>(StringComparer.Ordinal);
			var descriptorsByStableTypeId = new Dictionary<string, ProjectTypeDescriptor>(StringComparer.Ordinal);
			foreach (var assembly in GetAssembliesForCatalog())
			{
				foreach (var type in ProjectTypeResolverUtility.GetLoadableTypes(assembly))
				{
					var typeName = type.AssemblyQualifiedName;
					if (string.IsNullOrWhiteSpace(typeName) || descriptorsByTypeName.ContainsKey(typeName))
					{
						continue;
					}

					var descriptor = new ProjectTypeDescriptor
					{
						Type = type,
						DisplayName = type.Name,
						QualifiedDisplayName = string.IsNullOrWhiteSpace(type.FullName) ? type.Name : type.FullName,
						TypeName = typeName,
						StableTypeId = ProjectTypeResolverUtility.GetStableTypeId(type),
						IsGameplayType = ProjectTypeResolverUtility.IsGameplayType(type)
					};
					descriptors.Add(descriptor);
					descriptorsByTypeName.Add(typeName, descriptor);
					descriptorsByStableTypeId.TryAdd(descriptor.StableTypeId, descriptor);
				}
			}

			descriptors.Sort(static (left, right) =>
			{
				var displayNameComparison = StringComparer.OrdinalIgnoreCase.Compare(left.DisplayName, right.DisplayName);
				if (displayNameComparison != 0)
				{
					return displayNameComparison;
				}

				return StringComparer.OrdinalIgnoreCase.Compare(left.QualifiedDisplayName, right.QualifiedDisplayName);
			});

			_allDescriptors = descriptors;
			_componentDescriptors = descriptors.Where(descriptor => IsComponentType(descriptor.Type)).ToList();
			_dataAssetDescriptors = descriptors.Where(descriptor => IsDataAssetType(descriptor.Type)).ToList();
			_descriptorsByTypeName = descriptorsByTypeName;
			_descriptorsByStableTypeId = descriptorsByStableTypeId;
		}
	}

	private string? GetCurrentProjectRootPath()
	{
		var projectService = _projectServiceAccessor();
		return projectService.HasOpenProject && string.IsNullOrWhiteSpace(projectService.ProjectRootPath) == false
			? Path.GetFullPath(projectService.ProjectRootPath)
			: null;
	}

	private void ResetCache(string? currentProjectRoot, long currentGameplayGeneration)
	{
		_loadedProjectRootPath = currentProjectRoot;
		_loadedGameplayGeneration = currentGameplayGeneration;
		_allDescriptors = null;
		_componentDescriptors = null;
		_dataAssetDescriptors = null;
		_descriptorsByTypeName = null;
		_descriptorsByStableTypeId = null;
	}

	private IEnumerable<Assembly> GetAssembliesForCatalog()
	{
		foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			if (AssemblyLoadContext.GetLoadContext(assembly) != AssemblyLoadContext.Default)
			{
				continue;
			}

			yield return assembly;
		}

		var gameplayAssembly = _gameplayAssemblyHost?.EnsureLoaded().Assembly;
		if (gameplayAssembly is not null)
		{
			yield return gameplayAssembly;
		}
	}

	internal static string? TryFindGameplayAssemblyPath(string gameplayProjectPath)
	{
		if (string.IsNullOrWhiteSpace(gameplayProjectPath) || File.Exists(gameplayProjectPath) == false)
		{
			return null;
		}

		var gameplayProjectDirectory = Path.GetDirectoryName(gameplayProjectPath);
		if (string.IsNullOrWhiteSpace(gameplayProjectDirectory))
		{
			return null;
		}

		var binDirectory = Path.Combine(gameplayProjectDirectory, "bin");
		if (Directory.Exists(binDirectory) == false)
		{
			return null;
		}

		var assemblyName = TryReadAssemblyName(gameplayProjectPath);
		if (string.IsNullOrWhiteSpace(assemblyName))
		{
			return null;
		}

		return Directory.EnumerateFiles(binDirectory, $"{assemblyName}.dll", SearchOption.AllDirectories)
			.Where(path => File.Exists(Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}.deps.json")))
			.OrderByDescending(File.GetLastWriteTimeUtc)
			.FirstOrDefault();
	}

	private static string? TryReadAssemblyName(string gameplayProjectPath)
	{
		try
		{
			var document = XDocument.Load(gameplayProjectPath);
			var assemblyNameElement = document
				.Descendants()
				.FirstOrDefault(element => string.Equals(element.Name.LocalName, "AssemblyName", StringComparison.OrdinalIgnoreCase));
			if (string.IsNullOrWhiteSpace(assemblyNameElement?.Value) == false)
			{
				return assemblyNameElement.Value.Trim();
			}
		}
		catch (Exception exception)
		{
			Console.WriteLine($"Failed to read gameplay project assembly name from '{gameplayProjectPath}': {exception}");
		}

		return Path.GetFileNameWithoutExtension(gameplayProjectPath);
	}

	private static bool IsComponentType(Type? type)
	{
		return type is not null
		       && type.IsValueType
		       && type.IsGenericTypeDefinition == false
		       && type.ContainsGenericParameters == false
		       && typeof(IEntityComponent).IsAssignableFrom(type);
	}

	private static bool IsDataAssetType(Type? type)
	{
		return type is not null
		       && typeof(IDataAsset).IsAssignableFrom(type)
		       && type.IsClass
		       && type.IsAbstract == false
		       && type.IsInterface == false
		       && type.IsGenericTypeDefinition == false
		       && type.ContainsGenericParameters == false
		       && type.GetConstructor(Type.EmptyTypes) is not null;
	}
}

internal static class ProjectTypeResolverUtility
{
	private static readonly ConcurrentDictionary<Type, string> TypeNames = new();
	private static readonly ConcurrentDictionary<Type, string> StableTypeIds = new();
	private const string GameplayTypeIdPrefix = "gameplay:";

	public static string GetTypeName(Type type)
	{
		ArgumentNullException.ThrowIfNull(type);
		return TypeNames.GetOrAdd(type, static candidate =>
			candidate.AssemblyQualifiedName
			?? throw new InvalidOperationException($"Type '{candidate.FullName}' does not have an assembly-qualified name."));
	}

	public static string GetStableTypeId(Type type)
	{
		ArgumentNullException.ThrowIfNull(type);
		return StableTypeIds.GetOrAdd(type, static candidate =>
		{
			if (IsGameplayType(candidate))
			{
				var fullName = candidate.FullName ?? candidate.Name;
				return $"{GameplayTypeIdPrefix}{fullName}";
			}

			return GetTypeName(candidate);
		});
	}

	public static bool IsGameplayType(Type type)
	{
		ArgumentNullException.ThrowIfNull(type);
		var loadContext = AssemblyLoadContext.GetLoadContext(type.Assembly);
		return loadContext is not null && ReferenceEquals(loadContext, AssemblyLoadContext.Default) == false;
	}

	public static void ClearCaches()
	{
		TypeNames.Clear();
		StableTypeIds.Clear();
	}

	public static bool TryResolveFromLoadedAssemblies(string typeName, out Type type)
	{
		if (string.IsNullOrWhiteSpace(typeName))
		{
			type = null!;
			return false;
		}

		foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(candidate => AssemblyLoadContext.GetLoadContext(candidate) == AssemblyLoadContext.Default))
		{
			foreach (var candidate in GetLoadableTypes(assembly))
			{
				if (string.Equals(candidate.AssemblyQualifiedName, typeName, StringComparison.Ordinal))
				{
					type = candidate;
					return true;
				}
			}
		}

		type = null!;
		return false;
	}

	public static bool IsGameplayStableTypeId(string stableTypeId)
	{
		return string.IsNullOrWhiteSpace(stableTypeId) == false &&
		       stableTypeId.StartsWith(GameplayTypeIdPrefix, StringComparison.Ordinal);
	}

	public static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
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
