using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Tooling.Imaging;
using WolfEngine.Importing;

namespace WolfEngine.Editor.Projects;

/// <summary>Writes an image the editor generated, such as a repacked map, as a PNG texture source with its metadata.</summary>
internal static class GeneratedTextureWriter
{
	/// <summary>
	/// Saves <paramref name="image"/> at <paramref name="relativeTexturePath"/> with a new node id and the semantic it is
	/// imported with. The caller refreshes the source in the asset pipeline.
	/// </summary>
	/// <param name="alphaCoverageCutoff">
	/// When above zero, the texture's mips preserve alpha coverage at this cutoff; see
	/// <see cref="TextureImportSettings.PreserveAlphaCoverage"/>.
	/// </param>
	/// <returns>The texture's node id.</returns>
	public static Guid Write(
		IEditorProjectService projectService,
		IAssetMetadataStore metadataStore,
		RgbaImage image,
		string relativeTexturePath,
		TextureSemantic semantic,
		float alphaCoverageCutoff = 0.0f)
	{
		var absoluteTexturePath = projectService.GetAbsolutePath(relativeTexturePath);
		var textureName = Path.GetFileNameWithoutExtension(relativeTexturePath);
		var nodeId = Guid.NewGuid();
		Directory.CreateDirectory(Path.GetDirectoryName(absoluteTexturePath)!);

		image.SavePng(absoluteTexturePath);
		metadataStore.Save(
			AssetFileExtensions.GetMetaPath(absoluteTexturePath),
			CreateMetadata(textureName, nodeId, semantic, alphaCoverageCutoff));

		return nodeId;
	}

	private static AssetSourceMetaFile CreateMetadata(string name, Guid nodeId, TextureSemantic semantic, float alphaCoverageCutoff)
	{
		var metadata = new AssetSourceMetaFile
		{
			SourceId = Guid.NewGuid(),
			ImporterId = AssetImporterIds.Texture,
			ImporterVersion = 1,
			SubAssets =
			[
				new AssetSubAssetManifestEntry
				{
					Key = "main",
					NodeId = nodeId,
					Type = AssetType.Texture2D,
					Name = name
				}
			]
		};
		metadata.SetImportSettings(new TextureImportSettings
		{
			TextureSemantic = semantic,
			PreserveAlphaCoverage = alphaCoverageCutoff > 0.0f,
			AlphaCoverageCutoff = alphaCoverageCutoff > 0.0f ? alphaCoverageCutoff : 0.5f
		});

		return metadata;
	}
}
