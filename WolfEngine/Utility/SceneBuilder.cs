using System.Numerics;
using System.IO;
using WolfEngine.ECS;
using WolfEngine.Importing;
using WolfEngine.Rendering;

namespace WolfEngine.Utility;

public interface ISceneBuilder
{
	public void Import3DScene(string path, World world);
}

public class SceneBuilder : ISceneBuilder
{
	private readonly IThreeDFileImporter _fileImporter;
	private readonly ITextureFactory _textureFactory;
	private readonly IMaterialFactory _materialFactory;
	private readonly RenderGraph _rendergraph;

	public SceneBuilder(IThreeDFileImporter fileImporter, ITextureFactory textureFactory,
		IMaterialFactory materialFactory, RenderGraph rendergraph)
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

		var materials = new List<Material>();
		for (var i = 0; i < importedScene.Materials.Count; i++)
		{
			var importedMaterial = importedScene.Materials[i];
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
				occlusionTexture,
				importedMaterial.AlphaMode,
				importedMaterial.AlphaCutoff);
			
			Console.WriteLine($"imported material as {material.AlphaMode}");

			materials.Add(material);
		}

		if (importedScene.RootNodes.Count == 0)
		{
			return;
		}

		var entityCount = 0;
		if (importedScene.RootNodes.Count == 1)
		{
			entityCount += CreateNodeEntity(importedScene.RootNodes[0], world, materials, null);
		}
		else
		{
			var sceneName = string.IsNullOrWhiteSpace(importedScene.Name)
				? Path.GetFileNameWithoutExtension(path)
				: importedScene.Name;
			var wrapper = world.CreateEntity(sceneName);
			world.AddTransform(wrapper, Matrix4x4.Identity);
			entityCount++;

			foreach (var rootNode in importedScene.RootNodes)
			{
				entityCount += CreateNodeEntity(rootNode, world, materials, wrapper);
			}
		}

		if (entityCount == 0) return;

		Console.Out.WriteLine($"Imported {entityCount} entities");
	}

	private int CreateNodeEntity(ImportedNode node, World world, IReadOnlyList<Material> materials, Entity? parent)
	{
		Entity nodeEntity;
		try
		{
			nodeEntity = world.CreateEntity(node.Name);
			if (parent is { } parentEntity)
			{
				world.SetParent(nodeEntity, parentEntity);
			}

			world.AddTransform(nodeEntity, node.LocalTransform);
		}
		catch (Exception e)
		{
			Console.Out.WriteLine($"Error importing node {node.Name}");
			Console.Out.WriteLine(e.Message);
			return 0;
		}

		var entityCount = 1;

		if (node.Meshes.Count == 1)
		{
			TryAttachMeshRenderer(nodeEntity, node.Meshes[0], node.Name, world, materials);
		}
		else if (node.Meshes.Count > 1)
		{
			foreach (var meshNode in node.Meshes)
			{
				Entity meshEntity;
				try
				{
					meshEntity = world.CreateEntity(meshNode.Name);
					world.SetParent(meshEntity, nodeEntity);
					world.AddTransform(meshEntity, Matrix4x4.Identity);
				}
				catch (Exception e)
				{
					Console.Out.WriteLine($"Error importing mesh node {meshNode.Name}");
					Console.Out.WriteLine(e.Message);
					continue;
				}

				entityCount++;
				TryAttachMeshRenderer(meshEntity, meshNode, meshNode.Name, world, materials);
			}
		}

		foreach (var child in node.Children)
		{
			entityCount += CreateNodeEntity(child, world, materials, nodeEntity);
		}

		return entityCount;
	}

	private void TryAttachMeshRenderer(
		Entity entity,
		ImportedNodeMesh importedMesh,
		string ownerName,
		World world,
		IReadOnlyList<Material> materials)
	{
		try
		{
			if (importedMesh.MaterialIndex < 0 || importedMesh.MaterialIndex >= materials.Count)
			{
				throw new InvalidOperationException($"Material index {importedMesh.MaterialIndex} was out of range.");
			}

			_rendergraph.EnsureMeshResources(importedMesh.Mesh);
			world.AddComponent(entity, new MeshRenderer
			{
				Mesh = importedMesh.Mesh,
				Material = materials[importedMesh.MaterialIndex]
			});
		}
		catch (Exception e)
		{
			Console.Out.WriteLine($"Error importing object {ownerName}");
			Console.Out.WriteLine(e.Message);
		}
	}
}
