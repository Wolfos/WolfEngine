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
	/// <returns>The texture's node id.</returns>
	public static Guid Write(
		IEditorProjectService projectService,
		IAssetMetadataStore metadataStore,
		RgbaImage image,
		string relativeTexturePath,
		TextureSemantic semantic)
	{
		var absoluteTexturePath = projectService.GetAbsolutePath(relativeTexturePath);
		var textureName = Path.GetFileNameWithoutExtension(relativeTexturePath);
		var nodeId = Guid.NewGuid();
		Directory.CreateDirectory(Path.GetDirectoryName(absoluteTexturePath)!);

		image.SavePng(absoluteTexturePath);
		metadataStore.Save(
			AssetFileExtensions.GetMetaPath(absoluteTexturePath),
			CreateMetadata(textureName, nodeId, semantic));

		return nodeId;
	}

	private static AssetSourceMetaFile CreateMetadata(string name, Guid nodeId, TextureSemantic semantic)
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
			TextureSemantic = semantic
		});

		return metadata;
	}
}
