using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WolfEngine.AssetPipeline;

public static class AssetJson
{
	public static readonly JsonSerializerOptions SerializerOptions = new()
	{
		IncludeFields = true,
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
}
