using System.Text.Json.Serialization;

namespace WolfEngine.Audio;

/// <summary>
/// Source-generated JSON for the cooked audio artifact header, which a built game reads for every clip. The options
/// reproduce the editor's <c>AssetJson</c> options the header has always been written with.
/// </summary>
[JsonSourceGenerationOptions(
	IncludeFields = true,
	UseStringEnumConverter = true,
	WriteIndented = true)]
[JsonSerializable(typeof(AudioArtifactHeader))]
internal sealed partial class AudioJsonContext : JsonSerializerContext
{
}
