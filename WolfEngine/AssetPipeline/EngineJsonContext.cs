using System.Text.Json.Serialization;
using WolfEngine.Animation;

namespace WolfEngine.AssetPipeline;

/// <summary>
/// Source-generated JSON for the engine's own file formats that a built game reads: the pack table and manifest,
/// runtime settings, scene manifests, materials and animation assets. A NativeAOT game cannot use reflection-based
/// JSON, so every runtime read of these formats goes through this context.
/// </summary>
/// <remarks>
/// The options reproduce <see cref="AssetJson.SerializerOptions"/>, which the editor writes these files with, so both
/// read and write the same JSON. <see cref="AssetJson"/> also drops members marked <c>[NotSerialized]</c>, which source
/// generation cannot see; none of these types have one, and a test keeps it that way.
/// </remarks>
[JsonSourceGenerationOptions(
	IncludeFields = true,
	UseStringEnumConverter = true,
	WriteIndented = true)]
[JsonSerializable(typeof(List<WolfPackEntry>))]
[JsonSerializable(typeof(WolfBootstrapManifest))]
[JsonSerializable(typeof(CookedRuntimeSettings))]
[JsonSerializable(typeof(SceneManifestDocument))]
[JsonSerializable(typeof(MaterialAsset))]
[JsonSerializable(typeof(AnimationGraph))]
[JsonSerializable(typeof(AnimationSet))]
[JsonSerializable(typeof(BoneMask))]
public sealed partial class EngineJsonContext : JsonSerializerContext
{
}
