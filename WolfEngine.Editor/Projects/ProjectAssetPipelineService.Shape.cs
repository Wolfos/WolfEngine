using WolfEngine.AssetPipeline;
using WolfEngine.Modeling;

namespace WolfEngine.Editor.Projects;

public sealed partial class ProjectAssetPipelineService
{
	private const string ShapeModelNodeKey = "main";
	private const string ShapeMeshNodeKey = "mesh";

	/// <summary>
	/// Imports a <c>.shape</c> as a model with one mesh, so it can be placed in a scene like any imported model.
	/// <para>
	/// The importer never compiles. It cooks the mesh saved in the source file, which the editor compiled, because a
	/// compile depends on the previous compile and so cannot be repeated from the tree alone. A shape that has never
	/// been compiled imports as a model with no mesh.
	/// </para>
	/// </summary>
	private ImportGraph ImportShapeSource(
		string projectRootPath,
		string absoluteSourcePath,
		string relativeSourcePath,
		string relativeMetaPath,
		AssetSourceMetaFile metadata)
	{
		var shape = ShapeFile.Read(absoluteSourcePath);
		var shapeName = ShapeFile.GetShapeName(relativeSourcePath);
		var nodeName = Path.GetFileName(relativeSourcePath);
		var importedFolder = Path.Combine(
			AssetPipelinePaths.LibraryFolderName,
			AssetPipelinePaths.ImportedFolderName,
			metadata.SourceId.ToString("D"));

		var nodes = new List<AssetNodeRecord>();
		var dependencies = new List<AssetDependencyRecord>();
		var modelNodeId = GetOrCreateNodeId(metadata, ShapeModelNodeKey, AssetType.Model3D, nodeName);
		var modelNode = new ImportedModelAssetNode
		{
			Name = shapeName
		};

		if (shape.Mesh is { IsEmpty: false } mesh)
		{
			var meshNodeId = GetOrCreateNodeId(metadata, ShapeMeshNodeKey, AssetType.Mesh, shapeName);
			var relativeMeshPath = NormalizeRelativePath(Path.Combine(importedFolder, "shape.mesh.bin"));
			ImportedMeshSerializer.Write(GetAbsolutePath(projectRootPath, relativeMeshPath), ShapeMeshCooker.ToImportedMesh(mesh));

			nodes.Add(new AssetNodeRecord
			{
				NodeId = meshNodeId,
				SourceId = metadata.SourceId,
				Type = AssetType.Mesh,
				NodeKey = ShapeMeshNodeKey,
				Name = shapeName,
				IsGenerated = true,
				RelativeSourcePath = relativeSourcePath,
				RelativeAssetPath = relativeMeshPath,
				RelativeMetaPath = relativeMetaPath,
				SummaryJson = AssetPipelineSerialization.Serialize(new MeshAssetSummary
				{
					RelativeImportedMeshPath = relativeMeshPath,
					VertexCount = mesh.VertexCount,
					IndexCount = mesh.Indices.Length,
					IsSkinned = false
				})
			});

			var materialId = shape.Settings.MaterialId;
			modelNode.Meshes.Add(new ImportedModelAssetMeshInstance
			{
				Name = shapeName,
				MeshNodeId = meshNodeId,
				MaterialNodeId = materialId
			});

			dependencies.Add(new AssetDependencyRecord
			{
				FromNodeId = modelNodeId,
				ToNodeId = meshNodeId,
				Kind = "model-mesh",
				IsHard = true
			});

			if (materialId != Guid.Empty)
			{
				dependencies.Add(new AssetDependencyRecord
				{
					FromNodeId = meshNodeId,
					ToNodeId = materialId,
					Kind = "mesh-material",
					IsHard = true
				});
				dependencies.Add(new AssetDependencyRecord
				{
					FromNodeId = modelNodeId,
					ToNodeId = materialId,
					Kind = "model-material",
					IsHard = true
				});
			}
		}

		var relativeModelPath = NormalizeRelativePath(Path.Combine(importedFolder, "model3d.asset.json"));
		WriteJsonFile(GetAbsolutePath(projectRootPath, relativeModelPath), new ImportedModelAssetFile
		{
			Name = shapeName,
			Nodes = [modelNode]
		});

		// The model is the shape's primary asset: what the browser shows, what double-click opens in Model, and what
		// a scene references when the shape is placed.
		nodes.Add(new AssetNodeRecord
		{
			NodeId = modelNodeId,
			SourceId = metadata.SourceId,
			Type = AssetType.Model3D,
			NodeKey = ShapeModelNodeKey,
			Name = nodeName,
			IsGenerated = false,
			RelativeSourcePath = relativeSourcePath,
			RelativeAssetPath = relativeModelPath,
			RelativeMetaPath = relativeMetaPath,
			SummaryJson = AssetPipelineSerialization.Serialize(new Model3DAssetSummary
			{
				RelativeImportedModelPath = relativeModelPath,
				RootNodeCount = 1
			})
		});

		return new ImportGraph
		{
			Nodes = nodes,
			Artifacts = [],
			Dependencies = dependencies
		};
	}
}
