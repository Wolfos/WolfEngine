using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Serialization;
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
	private static readonly MethodInfo RemoveGenericMethod = typeof(RuntimeComponentAccessor)
		.GetMethod(nameof(RemoveGeneric), BindingFlags.Static | BindingFlags.NonPublic)!;
	private static readonly ConcurrentDictionary<Type, Action<World, Entity>> AddDefaultDelegates = new();
	private static readonly ConcurrentDictionary<Type, Func<World, Entity, object>> ReadBoxedDelegates = new();
	private static readonly ConcurrentDictionary<Type, Action<World, Entity, object>> WriteBoxedDelegates = new();
	private static readonly ConcurrentDictionary<Type, Action<World, Entity>> RemoveDelegates = new();

	public static void AddDefault(World world, Entity entity, Type componentType)
	{
		ArgumentNullException.ThrowIfNull(world);
		ValidateComponentType(componentType);
		if (IsCollectibleComponentType(componentType))
		{
			AddDefaultGenericMethod.MakeGenericMethod(componentType).Invoke(null, [world, entity]);
			return;
		}

		AddDefaultDelegates.GetOrAdd(componentType, CreateAddDefaultDelegate)(world, entity);
	}

	public static object ReadBoxed(World world, Entity entity, Type componentType)
	{
		ArgumentNullException.ThrowIfNull(world);
		ValidateComponentType(componentType);
		if (IsCollectibleComponentType(componentType))
		{
			return ReadBoxedGenericMethod.MakeGenericMethod(componentType).Invoke(null, [world, entity])
			       ?? throw new InvalidOperationException($"Failed to read component '{componentType.FullName}'.");
		}

		return ReadBoxedDelegates.GetOrAdd(componentType, CreateReadBoxedDelegate)(world, entity);
	}

	public static void WriteBoxed(World world, Entity entity, Type componentType, object componentValue)
	{
		ArgumentNullException.ThrowIfNull(world);
		ArgumentNullException.ThrowIfNull(componentValue);
		ValidateComponentType(componentType);
		if (IsCollectibleComponentType(componentType))
		{
			WriteBoxedGenericMethod.MakeGenericMethod(componentType).Invoke(null, [world, entity, componentValue]);
			return;
		}

		WriteBoxedDelegates.GetOrAdd(componentType, CreateWriteBoxedDelegate)(world, entity, componentValue);
	}

	public static void Remove(World world, Entity entity, Type componentType)
	{
		ArgumentNullException.ThrowIfNull(world);
		ValidateComponentType(componentType);
		if (IsCollectibleComponentType(componentType))
		{
			RemoveGenericMethod.MakeGenericMethod(componentType).Invoke(null, [world, entity]);
			return;
		}

		RemoveDelegates.GetOrAdd(componentType, CreateRemoveDelegate)(world, entity);
	}

	public static void ClearCachedDelegates()
	{
		AddDefaultDelegates.Clear();
		ReadBoxedDelegates.Clear();
		WriteBoxedDelegates.Clear();
		RemoveDelegates.Clear();
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

	private static bool IsCollectibleComponentType(Type componentType)
	{
		return AssemblyLoadContext.GetLoadContext(componentType.Assembly)?.IsCollectible == true;
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

	private static Action<World, Entity> CreateRemoveDelegate(Type componentType)
	{
		var method = RemoveGenericMethod.MakeGenericMethod(componentType);
		return (Action<World, Entity>)Delegate.CreateDelegate(typeof(Action<World, Entity>), method);
	}

	private static void AddDefaultGeneric<T>(World world, Entity entity) where T : struct, IEntityComponent
	{
		var component = default(T);
		var parameterlessDefaultMethod = typeof(T).GetMethod(
			nameof(IEntityComponent.ApplyDefaultValues),
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
			Type.EmptyTypes);
		if (parameterlessDefaultMethod is not null)
		{
			var boxedComponent = (object)component;
			parameterlessDefaultMethod.Invoke(boxedComponent, null);
			component = (T)boxedComponent;
		}
		else
		{
			component.ApplyDefaultValues(world, entity);
		}

		world.AddComponent(entity, component);
	}

	private static object ReadBoxedGeneric<T>(World world, Entity entity) where T : struct, IEntityComponent
	{
		return world.GetComponent<T>(entity);
	}

	private static void WriteBoxedGeneric<T>(World world, Entity entity, object componentValue) where T : struct, IEntityComponent
	{
		var typedValue = (T)componentValue;
		if (typedValue is IJsonOnDeserialized callback)
		{
			callback.OnDeserialized();
			typedValue = (T)callback;
		}

		world.AddComponent(entity, typedValue);
	}

	private static void RemoveGeneric<T>(World world, Entity entity) where T : struct, IEntityComponent
	{
		world.RemoveComponent<T>(entity);
	}
}
