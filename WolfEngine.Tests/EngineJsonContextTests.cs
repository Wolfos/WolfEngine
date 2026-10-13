using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;

namespace WolfEngine.Tests;

/// <summary>
/// The editor writes the engine's file formats with reflection-based <see cref="AssetJson"/> options, and a built game
/// reads them through the source-generated <see cref="EngineJsonContext"/>. These tests hold the two to the same JSON.
/// </summary>
[TestFixture]
public sealed class EngineJsonContextTests
{
	/// <summary>
	/// Every engine type the generated context exposes, nested types included, since the generator adds metadata
	/// for every type it reaches from the roots.
	/// </summary>
	private static IEnumerable<Type> ContextTypes()
	{
		return typeof(EngineJsonContext)
			.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
			.Select(property => property.PropertyType)
			.Where(propertyType => propertyType.IsGenericType &&
			                       propertyType.GetGenericTypeDefinition() == typeof(JsonTypeInfo<>))
			.Select(propertyType => propertyType.GetGenericArguments()[0])
			.Where(IsEngineType)
			.OrderBy(type => type.FullName, StringComparer.Ordinal);
	}

	private static bool IsEngineType(Type type)
	{
		if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
		{
			return IsEngineType(type.GetGenericArguments()[0]);
		}

		return type.IsEnum == false &&
		       type.IsArray == false &&
		       type.Namespace?.StartsWith("WolfEngine", StringComparison.Ordinal) == true &&
		       (type.IsValueType || type.GetConstructor(Type.EmptyTypes) is not null);
	}

	[Test]
	public void ContextCoversTheRuntimeFormats()
	{
		var types = ContextTypes().ToArray();

		Assert.That(types, Does.Contain(typeof(MaterialAsset)));
		Assert.That(types, Does.Contain(typeof(WolfBootstrapManifest)));
		Assert.That(types, Does.Contain(typeof(Animation.AnimationGraph)));
		Assert.That(types, Does.Contain(typeof(MaterialTextureAssignments)), "Nested types are covered too.");
	}

	[TestCaseSource(nameof(ContextTypes))]
	public void DefaultValues_WriteTheSameJsonAsTheEditor(Type type)
	{
		var value = CreateValue(type);

		Assert.That(
			JsonSerializer.Serialize(value, ContextInfo(type)),
			Is.EqualTo(JsonSerializer.Serialize(value, type, AssetJson.SerializerOptions)));
	}

	/// <summary>
	/// JSON the editor wrote, with every number and bool moved off its default, must read back to the same value
	/// through the context, so a member read differently cannot hide behind a default.
	/// </summary>
	[TestCaseSource(nameof(ContextTypes))]
	public void EditorJsonWithChangedValues_ReadsTheSameThroughTheContext(Type type)
	{
		var json = JsonSerializer.SerializeToNode(CreateValue(type), type, AssetJson.SerializerOptions)!;
		ChangeEveryValue(json);
		var editorJson = json.ToJsonString();

		var editorValue = JsonSerializer.Deserialize(editorJson, type, AssetJson.SerializerOptions);
		var contextValue = JsonSerializer.Deserialize(editorJson, ContextInfo(type));

		Assert.That(
			JsonSerializer.Serialize(contextValue, type, AssetJson.SerializerOptions),
			Is.EqualTo(JsonSerializer.Serialize(editorValue, type, AssetJson.SerializerOptions)));
	}

	[Test]
	public void BuiltInMaterialFile_ReadsTheSameThroughTheContext()
	{
		var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "BuiltInContent", "Assets", "Materials", "Default.mat.json");
		var json = File.ReadAllBytes(path);

		var editorMaterial = JsonSerializer.Deserialize<MaterialAsset>(json, AssetJson.SerializerOptions);
		var contextMaterial = JsonSerializer.Deserialize(json, EngineJsonContext.Default.MaterialAsset);

		Assert.That(
			JsonSerializer.Serialize(contextMaterial, AssetJson.SerializerOptions),
			Is.EqualTo(JsonSerializer.Serialize(editorMaterial, AssetJson.SerializerOptions)));
	}

	/// <summary>
	/// <see cref="AssetJson"/> drops [NotSerialized] members through a reflection modifier that source generation
	/// cannot reproduce. While no type the context covers uses the attribute, the two agree.
	/// </summary>
	[Test]
	public void ContextTypes_HaveNoNotSerializedMembers()
	{
		var visited = new HashSet<Type>();
		var offenders = new List<string>();
		foreach (var type in ContextTypes())
		{
			FindNotSerializedMembers(type, visited, offenders);
		}

		Assert.That(offenders, Is.Empty);
	}

	private static JsonTypeInfo ContextInfo(Type type)
	{
		return EngineJsonContext.Default.GetTypeInfo(type)
		       ?? throw new AssertionException($"The context has no metadata for '{type.FullName}'.");
	}

	private static object CreateValue(Type type)
	{
		if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
		{
			var list = (System.Collections.IList)Activator.CreateInstance(type)!;
			list.Add(Activator.CreateInstance(type.GetGenericArguments()[0]));

			return list;
		}

		return Activator.CreateInstance(type)!;
	}

	private static void FindNotSerializedMembers(Type type, HashSet<Type> visited, List<string> offenders)
	{
		if (type.IsPrimitive || type == typeof(string) || type.IsEnum || visited.Add(type) == false)
		{
			return;
		}

		if (type.IsArray)
		{
			FindNotSerializedMembers(type.GetElementType()!, visited, offenders);
			return;
		}

		foreach (var argument in type.IsGenericType ? type.GetGenericArguments() : [])
		{
			FindNotSerializedMembers(argument, visited, offenders);
		}

		if (type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true)
		{
			return;
		}

		var members = type.GetMembers(BindingFlags.Instance | BindingFlags.Public)
			.Where(member => member is FieldInfo or PropertyInfo);
		foreach (var member in members)
		{
			if (Attribute.IsDefined(member, typeof(NotSerializedAttribute)))
			{
				offenders.Add($"{type.FullName}.{member.Name}");
			}

			var memberType = member is FieldInfo field ? field.FieldType : ((PropertyInfo)member).PropertyType;
			FindNotSerializedMembers(memberType, visited, offenders);
		}
	}

	private static void ChangeEveryValue(JsonNode node)
	{
		if (node is JsonObject obj)
		{
			foreach (var property in obj.ToArray())
			{
				if (property.Value is JsonObject or JsonArray)
				{
					ChangeEveryValue(property.Value);
				}
				else
				{
					obj[property.Key] = ChangedValue(property.Value);
				}
			}
		}
		else if (node is JsonArray array)
		{
			for (var i = 0; i < array.Count; i++)
			{
				if (array[i] is JsonObject or JsonArray)
				{
					ChangeEveryValue(array[i]!);
				}
				else
				{
					array[i] = ChangedValue(array[i]);
				}
			}
		}
	}

	/// <summary>A changed copy of a leaf value. Containers are changed in place by <see cref="ChangeEveryValue"/>.</summary>
	private static JsonNode? ChangedValue(JsonNode? value)
	{
		if (value is not JsonValue leaf)
		{
			return value;
		}

		if (leaf.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
		{
			return JsonValue.Create(leaf.GetValue<bool>() == false);
		}

		// Strings may be enum names, ids or other formats, so only numbers and bools are changed.
		if (leaf.GetValueKind() != JsonValueKind.Number)
		{
			return value;
		}

		var number = leaf.GetValue<double>();
		return JsonValue.Create(number == Math.Floor(number) ? number + 3 : number + 2.5);
	}
}
