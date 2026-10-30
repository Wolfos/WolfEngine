using System.Numerics;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.UI;

internal readonly record struct AssetBrowserTypeStyle(string Label, string Icon, Vector4 Color);

internal static class AssetBrowserAppearance
{
	private static readonly string[] SourceSuffixes =
	[
		".animgraph.json",
		".animset.data.json",
		".bonemask.data.json",
		".animset.json",
		".bonemask.json",
		".anim.json",
		".scene.json",
		".prefab.json",
		".mat.json",
		".data.json"
	];

	public static Vector4 FolderColor => new(0.93f, 0.77f, 0.49f, 1.0f);

	public static AssetBrowserTypeStyle For(AssetType type)
	{
		return type switch
		{
			AssetType.Model3D => new("3D model", "box", new(0.48f, 0.78f, 0.91f, 1.0f)),
			AssetType.Mesh => new("Mesh", "asset_grid_on", new(0.48f, 0.78f, 0.91f, 1.0f)),
			AssetType.Material => new("Material", "asset_palette", new(0.83f, 0.65f, 0.91f, 1.0f)),
			AssetType.Texture2D => new("Texture", "asset_image", new(0.95f, 0.65f, 0.73f, 1.0f)),
			AssetType.Scene or AssetType.SceneCell => new("Scene", "asset_landscape", new(0.66f, 0.78f, 0.98f, 1.0f)),
			AssetType.Prefab => new("Prefab", "prefab", new(0.66f, 0.78f, 0.98f, 1.0f)),
			AssetType.Terrain => new("Terrain", "terrain", new(0.66f, 0.81f, 0.48f, 1.0f)),
			AssetType.AudioClip => new("Audio clip", "asset_graphic_eq", new(0.95f, 0.75f, 0.46f, 1.0f)),
			AssetType.AnimationClip => new("Animation clip", "asset_movie", new(0.50f, 0.84f, 0.68f, 1.0f)),
			AssetType.AnimationGraph => new("Animation graph", "asset_device_hub", new(0.50f, 0.84f, 0.68f, 1.0f)),
			AssetType.AnimationSet => new("Animation set", "asset_video_library", new(0.50f, 0.84f, 0.68f, 1.0f)),
			AssetType.AnimationSequence => new("Animation sequence", "asset_video_library", new(0.50f, 0.84f, 0.68f, 1.0f)),
			AssetType.Skeleton => new("Skeleton", "asset_accessibility", new(0.96f, 0.64f, 0.53f, 1.0f)),
			AssetType.BoneMask => new("Bone mask", "asset_accessibility", new(0.96f, 0.64f, 0.53f, 1.0f)),
			AssetType.ColorLookupTable => new("Color lookup", "asset_gradient", new(0.95f, 0.65f, 0.73f, 1.0f)),
			AssetType.Font => new("Font", "asset_text_fields", new(0.83f, 0.65f, 0.91f, 1.0f)),
			AssetType.ComputeShader => new("Compute shader", "asset_code", new(0.48f, 0.78f, 0.91f, 1.0f)),
			_ => new("Data asset", "asset_description", new(0.93f, 0.77f, 0.49f, 1.0f))
		};
	}

	public static string Title(string sourcePath)
	{
		var name = Path.GetFileName(sourcePath);

		foreach (var suffix in SourceSuffixes)
		{
			if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
			{
				return name[..^suffix.Length];
			}
		}

		return Path.GetFileNameWithoutExtension(name);
	}

	public static IReadOnlyList<AssetDatabaseEntry> FilterSubAssets(AssetsWindowSourceItem source, string search, AssetType? type)
	{
		var query = search.Trim();
		return source.SubAssets.Where(asset =>
			(type.HasValue == false || asset.Type == type.Value) &&
			(query.Length == 0 || asset.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
			.OrderBy(asset => For(asset.Type).Label, StringComparer.OrdinalIgnoreCase)
			.ThenBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}
}
