using System.Reflection;
using System.Runtime.Loader;

namespace WolfEngine.Editor.Projects;

internal sealed class GameplayAssemblyLoadContext : AssemblyLoadContext
{
	private readonly AssemblyDependencyResolver _resolver;

	public GameplayAssemblyLoadContext(string mainAssemblyPath)
		: base($"Gameplay:{Path.GetFileNameWithoutExtension(mainAssemblyPath)}", isCollectible: true)
	{
		_resolver = new AssemblyDependencyResolver(mainAssemblyPath);
	}

	protected override Assembly? Load(AssemblyName assemblyName)
	{
		if (string.Equals(assemblyName.Name, "WolfEngine", StringComparison.Ordinal) ||
		    string.Equals(assemblyName.Name, "WolfEngine.ECS", StringComparison.Ordinal))
		{
			return AssemblyLoadContext.Default.Assemblies.FirstOrDefault(candidate =>
				AssemblyName.ReferenceMatchesDefinition(candidate.GetName(), assemblyName));
		}

		var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
		return string.IsNullOrWhiteSpace(assemblyPath) ? null : LoadFromAssemblyPath(assemblyPath);
	}

	protected override nint LoadUnmanagedDll(string unmanagedDllName)
	{
		var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
		return string.IsNullOrWhiteSpace(libraryPath) ? 0 : LoadUnmanagedDllFromPath(libraryPath);
	}
}
