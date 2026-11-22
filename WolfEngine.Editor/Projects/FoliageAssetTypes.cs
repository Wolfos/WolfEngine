using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

internal static class FoliageAssetTypes
{
	public static bool IsFoliageType(AssetDatabaseEntry asset) => Matches(asset, typeof(FoliageType));
	public static bool IsTerrainFoliage(AssetDatabaseEntry asset) => Matches(asset, typeof(TerrainFoliage));

	private static bool Matches(AssetDatabaseEntry asset, Type type)
	{
		return asset.Type == AssetType.DataAsset &&
		       asset.TryGetSummary<DataAssetSummary>(out var summary) &&
		       (string.Equals(summary.DataAssetTypeId, ProjectTypeResolverUtility.GetStableTypeId(type), StringComparison.Ordinal) ||
		        string.Equals(summary.DataAssetType, type.AssemblyQualifiedName, StringComparison.Ordinal));
	}
}
