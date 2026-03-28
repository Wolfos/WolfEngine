using System.Collections.Concurrent;
using System.Reflection;
using WolfEngine.ECS;

namespace WolfEngine.Editor.UI;

public static class RuntimeComponentAccessor
{
	private static readonly MethodInfo AddDefaultGenericMethod = typeof(RuntimeComponentAccessor)
		.GetMethod(nameof(AddDefaultGeneric), BindingFlags.Static | BindingFlags.NonPublic)!;
	private static readonly MethodInfo ReadBoxedGenericMethod = typeof(RuntimeComponentAccessor)
		.GetMethod(nameof(ReadBoxedGeneric), BindingFlags.Static | BindingFlags.NonPublic)!;
	private static readonly MethodInfo WriteBoxedGenericMethod = typeof(RuntimeComponentAccessor)
		.GetMethod(nameof(WriteBoxedGeneric), BindingFlags.Static | BindingFlags.NonPublic)!;
	private static readonly ConcurrentDictionary<Type, Action<World, Entity>> AddDefaultDelegates = new();
	private static readonly ConcurrentDictionary<Type, Func<World, Entity, object>> ReadBoxedDelegates = new();
	private static readonly ConcurrentDictionary<Type, Action<World, Entity, object>> WriteBoxedDelegates = new();

	public static void AddDefault(World world, Entity entity, Type componentType)
	{
		ArgumentNullException.ThrowIfNull(world);
		ValidateComponentType(componentType);
		AddDefaultDelegates.GetOrAdd(componentType, CreateAddDefaultDelegate)(world, entity);
	}

	public static object ReadBoxed(World world, Entity entity, Type componentType)
	{
		ArgumentNullException.ThrowIfNull(world);
		ValidateComponentType(componentType);
		return ReadBoxedDelegates.GetOrAdd(componentType, CreateReadBoxedDelegate)(world, entity);
	}

	public static void WriteBoxed(World world, Entity entity, Type componentType, object componentValue)
	{
		ArgumentNullException.ThrowIfNull(world);
		ArgumentNullException.ThrowIfNull(componentValue);
		ValidateComponentType(componentType);
		WriteBoxedDelegates.GetOrAdd(componentType, CreateWriteBoxedDelegate)(world, entity, componentValue);
	}

	private static void ValidateComponentType(Type? componentType)
	{
		if (componentType is null ||
		    componentType.IsValueType == false ||
		    typeof(IEntityComponent).IsAssignableFrom(componentType) == false)
		{
			throw new InvalidOperationException($"'{componentType?.FullName ?? "<null>"}' is not a valid entity component type.");
		}
	}

	private static Action<World, Entity> CreateAddDefaultDelegate(Type componentType)
	{
		var method = AddDefaultGenericMethod.MakeGenericMethod(componentType);
		return (Action<World, Entity>)Delegate.CreateDelegate(typeof(Action<World, Entity>), method);
	}

	private static Func<World, Entity, object> CreateReadBoxedDelegate(Type componentType)
	{
		var method = ReadBoxedGenericMethod.MakeGenericMethod(componentType);
		return (Func<World, Entity, object>)Delegate.CreateDelegate(typeof(Func<World, Entity, object>), method);
	}

	private static Action<World, Entity, object> CreateWriteBoxedDelegate(Type componentType)
	{
		var method = WriteBoxedGenericMethod.MakeGenericMethod(componentType);
		return (Action<World, Entity, object>)Delegate.CreateDelegate(typeof(Action<World, Entity, object>), method);
	}

	private static void AddDefaultGeneric<T>(World world, Entity entity) where T : struct, IEntityComponent
	{
		world.AddComponent<T>(entity);
	}

	private static object ReadBoxedGeneric<T>(World world, Entity entity) where T : struct, IEntityComponent
	{
		return world.GetComponent<T>(entity);
	}

	private static void WriteBoxedGeneric<T>(World world, Entity entity, object componentValue) where T : struct, IEntityComponent
	{
		var typedValue = (T)componentValue;
		world.AddComponent(entity, typedValue);
	}
}
