using System.Collections;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

/// <summary>Discovers persistent asset references in serialized authoring state.</summary>
internal static class AssetDependencyCollector
{
	// Weak keys allow gameplay assemblies and their serializer metadata to unload after hot reload.
	private static readonly ConditionalWeakTable<Type, TraversalPlan> Plans = new();

	public static IReadOnlyList<Guid> Collect(object asset)
	{
		ArgumentNullException.ThrowIfNull(asset);

		var dependencies = new HashSet<Guid>();
		var visited = new Dictionary<object, HashSet<Type>>(ReferenceEqualityComparer.Instance);
		var pending = new Stack<(object Value, Type Type)>();
		pending.Push((asset, asset.GetType()));

		while (pending.TryPop(out var entry))
		{
			var type = Nullable.GetUnderlyingType(entry.Type) ?? entry.Type;
			if (type == typeof(object))
			{
				type = entry.Value.GetType();
			}

			var plan = Plans.GetValue(type, CreatePlan);
			if (plan.TypeInfo.PolymorphismOptions is { } polymorphism)
			{
				var runtimeType = entry.Value.GetType();
				if (polymorphism.DerivedTypes.Any(derived => derived.DerivedType == runtimeType))
				{
					type = runtimeType;
					plan = Plans.GetValue(type, CreatePlan);
				}
			}

			if (plan.AssetIdGetter is { } assetIdGetter)
			{
				AddDependency((Guid)assetIdGetter(entry.Value)!);
				continue;
			}

			if (plan.TypeInfo.Kind == JsonTypeInfoKind.None) continue;

			if (type.IsValueType == false)
			{
				if (visited.TryGetValue(entry.Value, out var visitedTypes) == false)
				{
					visitedTypes = [];
					visited.Add(entry.Value, visitedTypes);
				}

				if (visitedTypes.Add(type) == false) continue;
			}

			AddAnimationDependencies(entry.Value, AddDependency);

			switch (plan.TypeInfo.Kind)
			{
				case JsonTypeInfoKind.Object:
					foreach (var property in plan.TypeInfo.Properties)
					{
						if (property.Get is null) continue;

						var value = property.Get(entry.Value);
						if (value is null || property.ShouldSerialize?.Invoke(entry.Value, value) == false) continue;

						pending.Push((value, property.PropertyType));
					}

					break;

				case JsonTypeInfoKind.Enumerable:
					foreach (var value in (IEnumerable)entry.Value)
					{
						if (value is not null)
						{
							pending.Push((value, plan.TypeInfo.ElementType!));
						}
					}

					break;

				case JsonTypeInfoKind.Dictionary:
					foreach (var pair in (IEnumerable)entry.Value)
					{
						var value = pair is DictionaryEntry dictionaryEntry
							? dictionaryEntry.Value
							: plan.DictionaryValueGetter!(pair);
						if (value is not null)
						{
							pending.Push((value, plan.TypeInfo.ElementType!));
						}
					}

					break;
			}
		}

		return dependencies.Order().ToArray();

		void AddDependency(Guid id)
		{
			if (id != Guid.Empty)
			{
				dependencies.Add(id);
			}
		}
	}

	private static TraversalPlan CreatePlan(Type type)
	{
		// Each weakly cached plan owns its options; a global serializer cache would retain gameplay types.
		var options = new JsonSerializerOptions(AssetJson.GetSerializerOptions(type));
		var resolver = new DefaultJsonTypeInfoResolver();
		foreach (var modifier in ((DefaultJsonTypeInfoResolver)options.TypeInfoResolver!).Modifiers)
		{
			resolver.Modifiers.Add(modifier);
		}

		options.TypeInfoResolver = resolver;
		options.MakeReadOnly();
		var typeInfo = options.GetTypeInfo(type);
		Func<object, object?>? assetIdGetter = null;
		Func<object, object?>? dictionaryValueGetter = null;

		if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(AssetRef<>))
		{
			assetIdGetter = typeInfo.Properties.Single(property => property.Name == nameof(AssetRef<IDataAsset>.NodeId)).Get;
		}

		if (typeInfo.Kind == JsonTypeInfoKind.Dictionary && typeInfo.KeyType is not null)
		{
			var pairType = typeof(KeyValuePair<,>).MakeGenericType(typeInfo.KeyType, typeInfo.ElementType!);
			dictionaryValueGetter = pairType.GetProperty("Value")!.GetValue;
		}

		return new TraversalPlan(typeInfo, assetIdGetter, dictionaryValueGetter);
	}

	private static void AddAnimationDependencies(object value, Action<Guid> addDependency)
	{
		// These authoring formats predate AssetRef<T>. Keep their GUID representation compatible.
		switch (value)
		{
			case AnimationSet set:
				addDependency(set.SkeletonId);
				foreach (var clipId in set.Clips.Values)
				{
					addDependency(clipId);
				}

				break;

			case AnimationSequence sequence:
				addDependency(sequence.ClipId);
				break;

			case BoneMask mask:
				addDependency(mask.SkeletonId);
				break;

			case AnimationNode node:
				addDependency(node.MaskId);
				break;
		}
	}

	private sealed record TraversalPlan(
		JsonTypeInfo TypeInfo,
		Func<object, object?>? AssetIdGetter,
		Func<object, object?>? DictionaryValueGetter);
}
