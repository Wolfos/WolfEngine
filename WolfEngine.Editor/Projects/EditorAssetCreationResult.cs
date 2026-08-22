namespace WolfEngine.Editor.Projects;

public readonly record struct EditorAssetCreationResult(bool Success, string? ErrorMessage, Guid? AssetId)
{
	public static EditorAssetCreationResult Succeeded(Guid assetId) => new(true, null, assetId);
	public static EditorAssetCreationResult Failed(string errorMessage) => new(false, errorMessage, null);
}
