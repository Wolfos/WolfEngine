using System.Collections.Concurrent;
using System.Reflection;

namespace WolfEngine.Serialization;

/// <summary>
/// Every component and data asset type that can be cooked to binary. Generated code registers each assembly's
/// types from a module initializer, so a type is available as soon as its assembly is loaded and touched.
/// </summary>
/// <remarks>
/// Types from collectible assemblies are never registered. The editor hot-reloads gameplay code into collectible
/// load contexts and never reads cooked data, and holding those types here would keep every unloaded gameplay
/// assembly alive.
/// </remarks>
public static class SerializedTypeRegistry
{
	private static readonly ConcurrentDictionary<string, SerializedType> TypesByKey = new(StringComparer.Ordinal);
	private static readonly ConcurrentDictionary<Type, SerializedType> TypesByType = new();

	/// <summary>Called by generated code. A later registration under the same key replaces the earlier one.</summary>
	public static void Register(SerializedType type)
	{
		ArgumentNullException.ThrowIfNull(type);

		if (type.Type.Assembly.IsCollectible)
		{
			return;
		}

		TypesByKey[type.Key] = type;
		TypesByType[type.Type] = type;
	}

	/// <summary>
	/// Runs the registration code of an assembly that was loaded by path or from bytes. An assembly's module
	/// initializer only runs once something in it is used, and a loader may need its types before that.
	/// </summary>
	public static void EnsureRegistered(Assembly assembly)
	{
		ArgumentNullException.ThrowIfNull(assembly);

		System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
	}

	public static bool TryGet(string key, out SerializedType type)
	{
		return TypesByKey.TryGetValue(key, out type!);
	}

	public static bool TryGet(Type runtimeType, out SerializedType type)
	{
		return TypesByType.TryGetValue(runtimeType, out type!);
	}

	public static SerializedComponentType GetComponent(string key)
	{
		if (TryGet(key, out var type) && type is SerializedComponentType component)
		{
			return component;
		}

		throw new InvalidDataException(
			$"Cooked data refers to component '{key}', which is not a registered entity component in this build.");
	}

	public static SerializedComponentType GetComponent(Type runtimeType)
	{
		if (TryGet(runtimeType, out var type) && type is SerializedComponentType component)
		{
			return component;
		}

		throw new InvalidOperationException(
			$"Component '{runtimeType.FullName}' has no generated serializer. Components must be structs implementing " +
			"IEntityComponent in an assembly that references the WolfEngine source generator.");
	}

	public static SerializedType GetDataAsset(string key)
	{
		if (TryGet(key, out var type) && type is not SerializedComponentType)
		{
			return type;
		}

		throw new InvalidDataException(
			$"Cooked data refers to data asset type '{key}', which is not registered in this build.");
	}

	public static SerializedType GetDataAsset(Type runtimeType)
	{
		if (TryGet(runtimeType, out var type) && type is not SerializedComponentType)
		{
			return type;
		}

		throw new InvalidOperationException(
			$"Data asset '{runtimeType.FullName}' has no generated serializer. Data assets must be classes implementing " +
			"IDataAsset in an assembly that references the WolfEngine source generator.");
	}
}
