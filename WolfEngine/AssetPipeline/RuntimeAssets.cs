using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WolfEngine.AssetPipeline;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RuntimeAssetAttribute : Attribute
{
	public RuntimeAssetAttribute(AssetType assetType, Type authoringType, Type resolverType)
	{
		AssetType = assetType;
		AuthoringType = authoringType ?? throw new ArgumentNullException(nameof(authoringType));
		ResolverType = resolverType ?? throw new ArgumentNullException(nameof(resolverType));
	}

	public AssetType AssetType { get; }
	public Type AuthoringType { get; }
	public Type ResolverType { get; }
}

public readonly record struct RuntimeAssetResolveContext(
	Guid AssetId,
	AssetDatabaseEntry Asset,
	Type RuntimeType,
	string ProjectRootPath,
	Func<Guid, Type, object?> ResolveAsset)
{
	public string GetAbsolutePath(string relativePath)
	{
		if (string.IsNullOrWhiteSpace(relativePath))
		{
			throw new ArgumentException("Relative path cannot be null or empty.", nameof(relativePath));
		}

		var normalizedPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
		return Path.GetFullPath(Path.Combine(ProjectRootPath, normalizedPath));
	}
}

public interface IRuntimeAssetResolver
{
	object? Resolve(RuntimeAssetResolveContext context);
}

public interface IDataAssetRuntimeResolver : IRuntimeAssetResolver
{
}

public interface ITerrainAssetRuntimeResolver : IRuntimeAssetResolver
{
}

public interface IMaterialRuntimeAssetResolver : IRuntimeAssetResolver
{
}

public interface ITextureRuntimeAssetResolver : IRuntimeAssetResolver
{
}

public interface IMeshRuntimeAssetResolver : IRuntimeAssetResolver
{
}

public interface IRuntimeArtifactTargetProvider
{
	string CurrentTarget { get; }
}

public sealed class RuntimeArtifactTargetProvider : IRuntimeArtifactTargetProvider
{
	public string CurrentTarget =>
		OperatingSystem.IsMacOS() ? "metal" :
		OperatingSystem.IsWindows() ? "d3d12" :
		"generic";
}

public static class RuntimeAssetDescriptor
{
	// A null value memoizes "this type has no RuntimeAssetAttribute" so the reflection lookup runs once.
	private static readonly Dictionary<Type, RuntimeAssetAttribute?> Cache = new();
	private static readonly object Sync = new();

	public static RuntimeAssetAttribute Get(Type runtimeType)
	{
		ArgumentNullException.ThrowIfNull(runtimeType);

		lock (Sync)
		{
			if (Cache.TryGetValue(runtimeType, out var descriptor))
			{
				return descriptor
					?? throw new InvalidOperationException($"Runtime asset type '{runtimeType.FullName}' is missing a RuntimeAssetAttribute.");
			}

			descriptor = runtimeType.GetCustomAttributes(typeof(RuntimeAssetAttribute), inherit: false)
				.OfType<RuntimeAssetAttribute>()
				.SingleOrDefault();
			Cache[runtimeType] = descriptor;

			return descriptor
				?? throw new InvalidOperationException($"Runtime asset type '{runtimeType.FullName}' is missing a RuntimeAssetAttribute.");
		}
	}

	public static void ClearCache()
	{
		lock (Sync)
		{
			Cache.Clear();
		}
	}
}
