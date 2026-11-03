using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.Importing;

namespace WolfEngine.Editor.Projects;

/// <summary>
/// The part of an asset source's file name that selects its importer. A rename keeps it and changes only the rest,
/// since a file that stops matching its importer loses its asset type.
/// </summary>
public static class AssetSourceSuffix
{
	// Every multi-part extension an importer recognises.
	private static readonly string[] CompoundSuffixes =
	[
		MaterialAsset.FileExtension,
		DataAssetFile.FileExtension,
		EditorSceneAssetFile.FileExtension,
		PrefabAssetFile.FileExtension,
		Cell.FileExtension,
		AuthoredAnimationClipFile.Extension,
		AnimationGraph.Extension,
		ComputeShaderImport.SourceExtension
	];

	public static string Get(string fileName)
	{
		for (var i = 0; i < CompoundSuffixes.Length; i++)
		{
			if (fileName.EndsWith(CompoundSuffixes[i], StringComparison.OrdinalIgnoreCase))
			{
				return fileName[^CompoundSuffixes[i].Length..];
			}
		}

		return Path.GetExtension(fileName);
	}
}
