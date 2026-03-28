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
}

public interface IProjectTypeCatalog
{
	IReadOnlyList<ProjectTypeDescriptor> GetAll();
	IReadOnlyList<ProjectTypeDescriptor> GetComponentTypes();
	IReadOnlyList<ProjectTypeDescriptor> GetDataAssetTypes();
	bool TryGetDescriptor(string typeName, out ProjectTypeDescriptor descriptor);
}

public interface IProjectTypeResolver
{
	string GetTypeName(Type type);
	bool TryResolveType(string typeName, out Type type);
}

public sealed class ProjectTypeCatalog : IProjectTypeCatalog, IProjectTypeResolver
{
	private static readonly ConcurrentDictionary<string, byte> KnownGameplayAssemblyPaths = new(StringComparer.OrdinalIgnoreCase);
	private readonly Func<IEditorProjectService> _projectServiceAccessor;
	private readonly object _sync = new();
	private string? _loadedProjectRootPath;
	private bool _gameplayAssemblyLoadAttempted;
	private Assembly? _currentGameplayAssembly;
	private IReadOnlyList<ProjectTypeDescriptor>? _allDescriptors;
	private IReadOnlyList<ProjectTypeDescriptor>? _componentDescriptors;
	private IReadOnlyList<ProjectTypeDescriptor>? _dataAssetDescriptors;
	private Dictionary<string, ProjectTypeDescriptor>? _descriptorsByTypeName;

	public ProjectTypeCatalog(Func<IEditorProjectService> projectServiceAccessor)
	{
		_projectServiceAccessor = projectServiceAccessor ?? throw new ArgumentNullException(nameof(projectServiceAccessor));
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

	public string GetTypeName(Type type)
	{
		ArgumentNullException.ThrowIfNull(type);
		return type.AssemblyQualifiedName
		       ?? throw new InvalidOperationException($"Type '{type.FullName}' does not have an assembly-qualified name.");
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

	private void EnsureCatalogLoaded()
	{
		var currentProjectRoot = GetCurrentProjectRootPath();
		lock (_sync)
		{
			if (string.Equals(currentProjectRoot, _loadedProjectRootPath, StringComparison.OrdinalIgnoreCase) == false)
			{
				ResetCache(currentProjectRoot);
			}

			if (_allDescriptors is not null)
			{
				return;
			}

			EnsureGameplayAssemblyLoaded();

			var descriptors = new List<ProjectTypeDescriptor>();
			var descriptorsByTypeName = new Dictionary<string, ProjectTypeDescriptor>(StringComparer.Ordinal);
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
						TypeName = typeName
					};
					descriptors.Add(descriptor);
					descriptorsByTypeName.Add(typeName, descriptor);
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
		}
	}

	private string? GetCurrentProjectRootPath()
	{
		var projectService = _projectServiceAccessor();
		return projectService.HasOpenProject && string.IsNullOrWhiteSpace(projectService.ProjectRootPath) == false
			? Path.GetFullPath(projectService.ProjectRootPath)
			: null;
	}

	private void ResetCache(string? currentProjectRoot)
	{
		_loadedProjectRootPath = currentProjectRoot;
		_gameplayAssemblyLoadAttempted = false;
		_currentGameplayAssembly = null;
		_allDescriptors = null;
		_componentDescriptors = null;
		_dataAssetDescriptors = null;
		_descriptorsByTypeName = null;
	}

	private void EnsureGameplayAssemblyLoaded()
	{
		if (_gameplayAssemblyLoadAttempted)
		{
			return;
		}

		_gameplayAssemblyLoadAttempted = true;
		var projectService = _projectServiceAccessor();
		var gameplayProjectPath = projectService.GameplayProjectPath;
		if (string.IsNullOrWhiteSpace(gameplayProjectPath) || File.Exists(gameplayProjectPath) == false)
		{
			return;
		}

		var gameplayAssemblyPath = TryFindGameplayAssemblyPath(gameplayProjectPath);
		if (string.IsNullOrWhiteSpace(gameplayAssemblyPath))
		{
			return;
		}

		try
		{
			_currentGameplayAssembly = ReuseOrLoadGameplayAssembly(gameplayAssemblyPath);
			KnownGameplayAssemblyPaths.TryAdd(Path.GetFullPath(gameplayAssemblyPath), 0);
		}
		catch (Exception exception)
		{
			Console.WriteLine($"Failed to load gameplay assembly '{gameplayAssemblyPath}': {exception}");
		}
	}

	private IEnumerable<Assembly> GetAssembliesForCatalog()
	{
		foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			if (_currentGameplayAssembly is not null && ReferenceEquals(assembly, _currentGameplayAssembly))
			{
				yield return assembly;
				continue;
			}

			if (string.IsNullOrWhiteSpace(assembly.Location) == false &&
			    KnownGameplayAssemblyPaths.ContainsKey(Path.GetFullPath(assembly.Location)))
			{
				continue;
			}

			yield return assembly;
		}
	}

	private static Assembly ReuseOrLoadGameplayAssembly(string assemblyPath)
	{
		var fullAssemblyPath = Path.GetFullPath(assemblyPath);
		foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			if (string.IsNullOrWhiteSpace(assembly.Location))
			{
				continue;
			}

			if (string.Equals(Path.GetFullPath(assembly.Location), fullAssemblyPath, StringComparison.OrdinalIgnoreCase))
			{
				return assembly;
			}
		}

		var assemblyName = AssemblyName.GetAssemblyName(fullAssemblyPath);
		foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			if (AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), assemblyName))
			{
				return assembly;
			}
		}

		return AssemblyLoadContext.Default.LoadFromAssemblyPath(fullAssemblyPath);
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

	public static string GetTypeName(Type type)
	{
		ArgumentNullException.ThrowIfNull(type);
		return TypeNames.GetOrAdd(type, static candidate =>
			candidate.AssemblyQualifiedName
			?? throw new InvalidOperationException($"Type '{candidate.FullName}' does not have an assembly-qualified name."));
	}

	public static bool TryResolveFromLoadedAssemblies(string typeName, out Type type)
	{
		if (string.IsNullOrWhiteSpace(typeName))
		{
			type = null!;
			return false;
		}

		foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
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
