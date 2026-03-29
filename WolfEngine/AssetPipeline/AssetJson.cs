using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using WolfEngine.ECS;

namespace WolfEngine.AssetPipeline;

public static class AssetJson
{
	public static readonly JsonSerializerOptions SerializerOptions = new()
	{
		IncludeFields = true,
		TypeInfoResolver = CreateTypeInfoResolver(),
		WriteIndented = true,
		Converters = { new JsonStringEnumConverter() }
	};

	public static JsonSerializerOptions GetSerializerOptions(Type? runtimeType)
	{
		if (runtimeType is not null && AssemblyLoadContext.GetLoadContext(runtimeType.Assembly) != AssemblyLoadContext.Default)
		{
			return new JsonSerializerOptions(SerializerOptions);
		}

		return SerializerOptions;
	}

	private static IJsonTypeInfoResolver CreateTypeInfoResolver()
	{
		var resolver = new DefaultJsonTypeInfoResolver();
		resolver.Modifiers.Add(static typeInfo =>
		{
			if (typeInfo.Kind != JsonTypeInfoKind.Object)
			{
				return;
			}

			for (var i = typeInfo.Properties.Count - 1; i >= 0; i--)
			{
				var propertyInfo = typeInfo.Properties[i];
				if (propertyInfo.AttributeProvider is not MemberInfo member ||
				    Attribute.IsDefined(member, typeof(NotSerializedAttribute)) == false)
				{
					continue;
				}

				typeInfo.Properties.RemoveAt(i);
			}
		});
		return resolver;
	}
}
