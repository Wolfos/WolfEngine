using System;
using System.IO;
using System.Linq;
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
		return string.IsNullOrWhiteSpace(assemblyPath) ? null : LoadManagedAssembly(assemblyPath);
	}

	protected override nint LoadUnmanagedDll(string unmanagedDllName)
	{
		var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
		return string.IsNullOrWhiteSpace(libraryPath) ? 0 : LoadUnmanagedDllFromPath(libraryPath);
	}

	public Assembly LoadManagedAssembly(string assemblyPath)
	{
		using var assemblyStream = File.OpenRead(assemblyPath);
		var symbolsPath = Path.ChangeExtension(assemblyPath, ".pdb");
		if (File.Exists(symbolsPath) == false)
		{
			return LoadFromStream(assemblyStream);
		}

		using var symbolsStream = File.OpenRead(symbolsPath);
		return LoadFromStream(assemblyStream, symbolsStream);
	}
}
