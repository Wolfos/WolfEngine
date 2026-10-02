using WolfEngine.AssetPipeline;

namespace WolfEngine.Importing;

/// <summary>Offline font bake settings. Font sizes at runtime remain independent of this resolution.</summary>
public sealed record FontImportSettings
{
	public int PixelsPerEm { get; init; } = 48;
	public int DistanceRange { get; init; } = 6;
	public int AtlasWidth { get; init; } = 512;
}

public interface IFontCompiler
{
	FontCompilationResult Compile(string sourcePath, string outputDirectory, FontImportSettings settings);
}

public sealed record FontCompilationResult(string ArtifactPath, string AtlasPath, string PreviewPath,
	FontAssetSummary Summary);
