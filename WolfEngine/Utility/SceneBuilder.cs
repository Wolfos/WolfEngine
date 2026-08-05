using System;
using System.Collections.Generic;
using System.Numerics;
using System.IO;
using WolfEngine.AssetPipeline;
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
		// This path bypasses the asset pipeline, so there is no meta file to read settings from.
		var importedScene = _fileImporter.Import(path, new ModelImportSettings());
		var runtimeTextures = importedScene.Textures.Select(_textureFactory.GetTexture).ToList();

		var materials = new List<Material>();
		for (var i = 0; i < importedScene.Materials.Count; i++)
		{
			var importedMaterial = importedScene.Materials[i];
			Texture? albedoTexture = null;
			Texture? ormTexture = null;
			Texture? normalTexture = null;
			Texture? emissiveTexture = null;
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
				ormTexture = runtimeTextures[mrIndex];
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

			var material = _materialFactory.GetMaterial(
				"gbuffer.slang",
				importedMaterial.BaseColor,
				importedMaterial.MetallicFactor,
				importedMaterial.RoughnessFactor,
				importedMaterial.NormalScale,
				importedMaterial.EmissiveFactor,
				importedMaterial.EmissiveIntensity,
				albedoTexture,
				ormTexture,
				normalTexture,
				emissiveTexture,
				importedMaterial.AlphaMode,
				importedMaterial.AlphaCutoff);
			
			materials.Add(material);
		}

		if (importedScene.Nodes.Count == 0)
		{
			return;
		}

		var rootCount = importedScene.Nodes.Count(node => node.ParentIndex < 0);
		var entityCount = 0;
		Entity? wrapper = null;
		if (rootCount > 1)
		{
			var sceneName = string.IsNullOrWhiteSpace(importedScene.Name)
				? Path.GetFileNameWithoutExtension(path)
				: importedScene.Name;
			wrapper = world.CreateEntity(sceneName);
			world.AddTransform(wrapper.Value, Matrix4x4.Identity);
			entityCount++;
		}

		var nodeEntities = new Entity?[importedScene.Nodes.Count];
		for (var i = 0; i < importedScene.Nodes.Count; i++)
		{
			var node = importedScene.Nodes[i];
			Entity? parent = wrapper;
			if (node.ParentIndex >= 0)
			{
				if (node.ParentIndex >= i)
				{
					throw new InvalidDataException(
						$"Imported node {i} has invalid parent index {node.ParentIndex}; parents must precede children.");
				}

				parent = nodeEntities[node.ParentIndex];
				if (parent is null)
				{
					continue;
				}
			}

			// A lone root stands in for the whole asset, so it is named after the file rather than
			// whatever the DCC tool called it. Several roots share a wrapper that carries that name.
			var displayName = node.ParentIndex < 0 && rootCount == 1 && string.IsNullOrWhiteSpace(importedScene.Name) == false
				? importedScene.Name
				: node.Name;
			nodeEntities[i] = CreateNodeEntity(node, displayName, world, materials, parent, out var createdEntityCount);
			entityCount += createdEntityCount;
		}

		if (entityCount == 0) return;

		Console.Out.WriteLine($"Imported {entityCount} entities");
	}

	private Entity? CreateNodeEntity(
		ImportedNode node,
		string displayName,
		World world,
		IReadOnlyList<Material> materials,
		Entity? parent,
		out int createdEntityCount)
	{
		createdEntityCount = 0;
		Entity nodeEntity;
		try
		{
			nodeEntity = world.CreateEntity(displayName);
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
			return null;
		}

		createdEntityCount = 1;
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

				createdEntityCount++;
				TryAttachMeshRenderer(meshEntity, meshNode, meshNode.Name, world, materials);
			}
		}

		return nodeEntity;
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
			var material = materials[importedMesh.MaterialIndex];
			world.AddComponent(entity, new MeshRenderer
			{
				Mesh = importedMesh.Mesh,
				Material = material
			});
		}
		catch (Exception e)
		{
			Console.Out.WriteLine($"Error importing object {ownerName}");
			Console.Out.WriteLine(e.Message);
		}
	}
}
