using System.Text.Json;
using System.Text.Json.Serialization;

namespace WolfEngine.AssetPipeline;

public static class AssetJson
{
	public static readonly JsonSerializerOptions SerializerOptions = new()
	{
		WriteIndented = true,
		Converters = { new JsonStringEnumConverter() }
	};
}
