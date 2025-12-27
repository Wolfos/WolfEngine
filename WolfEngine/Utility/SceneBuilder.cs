using WolfEngine.ECS;
using WolfEngine.Importing;
using WolfEngine.Rendering;

namespace WolfEngine.Utility;

public interface ISceneBuilder
{
	public void Import3DScene(string path, World world);
}
public class SceneBuilder: ISceneBuilder
{
	private readonly IThreeDFileImporter _fileImporter;
    private readonly ITextureFactory _textureFactory;
    private readonly IMaterialFactory _materialFactory;
    private readonly RenderGraph _rendergraph;

	public SceneBuilder(IThreeDFileImporter fileImporter, ITextureFactory textureFactory, IMaterialFactory materialFactory, RenderGraph rendergraph)
    {
        _fileImporter = fileImporter;
        _textureFactory = textureFactory;
        _materialFactory = materialFactory;
        _rendergraph = rendergraph;
    }

	public void Import3DScene(string path, World world)
	{
		var importedScene = _fileImporter.Import(path);
		var runtimeTextures = importedScene.Textures.Select(_textureFactory.GetTexture).ToList();
        foreach (var importedMesh in importedScene.Meshes)
        {
            var entity = world.CreateEntity(importedMesh.Name);
            var transform = importedMesh.Transform;

            var importedMaterial = importedScene.Materials[importedMesh.MaterialIndex];
            Texture albedoTexture = null;
            Texture metallicRoughnessTexture = null;
            Texture normalTexture = null;
            Texture emissiveTexture = null;
            Texture occlusionTexture = null;
            if (importedMaterial.BaseColorTextureIndex is { } texIndex &&
                texIndex >= 0 &&
                texIndex < runtimeTextures.Count)
            {
                albedoTexture = runtimeTextures[texIndex];
            }
            if (importedMaterial.MetallicRoughnessTextureIndex is { } mrIndex &&
                mrIndex >= 0 &&
                mrIndex < runtimeTextures.Count)
            {
                metallicRoughnessTexture = runtimeTextures[mrIndex];
            }
            if (importedMaterial.NormalTextureIndex is { } normalIndex &&
                normalIndex >= 0 &&
                normalIndex < runtimeTextures.Count)
            {
                normalTexture = runtimeTextures[normalIndex];
            }
            if (importedMaterial.EmissiveTextureIndex is { } emissiveIndex &&
                emissiveIndex >= 0 &&
                emissiveIndex < runtimeTextures.Count)
            {
                emissiveTexture = runtimeTextures[emissiveIndex];
            }
            if (importedMaterial.OcclusionTextureIndex is { } occlusionIndex &&
                occlusionIndex >= 0 &&
                occlusionIndex < runtimeTextures.Count)
            {
                occlusionTexture = runtimeTextures[occlusionIndex];
            }

            var material = _materialFactory.GetMaterial(
                "gbuffer.slang",
                importedMaterial.BaseColor,
                importedMaterial.MetallicFactor,
                importedMaterial.RoughnessFactor,
                albedoTexture,
                metallicRoughnessTexture,
                normalTexture,
                emissiveTexture,
                occlusionTexture);

            _rendergraph.EnsureMeshResources(importedMesh.Mesh);
            var meshRenderer = new MeshRenderer
            {
                Mesh = importedMesh.Mesh,
                Material = material
            };

            
            world.AddTransform(entity, transform);
            world.AddComponent(entity, meshRenderer);
        }
	}
}