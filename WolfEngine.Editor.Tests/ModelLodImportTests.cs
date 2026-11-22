using System.Numerics;
using Microsoft.Data.Sqlite;
using NSubstitute;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Importing;

namespace WolfEngine.Editor.Tests;

/// <summary>
/// Importing models whose nodes carry authored levels of detail (<c>Tree_LOD0</c>, <c>Tree_LOD1</c>), through the
/// real pipeline with a scripted 3D importer. See docs/mesh-lod.md.
/// </summary>
[TestFixture]
public sealed class ModelLodImportTests
{
	private string _root = "";
	private IThreeDFileImporter _importer = null!;
	private AssetMetadataStore _metadataStore = null!;
	private ProjectAssetPipelineService _pipeline = null!;

	[SetUp]
	public void Setup()
	{
		_root = Path.Combine(Path.GetTempPath(), "model-lod-import-" + Guid.NewGuid());
		_importer = Substitute.For<IThreeDFileImporter>();
		_metadataStore = new AssetMetadataStore();
		_pipeline = new ProjectAssetPipelineService(
			new AssetPipelineIndex(),
			_metadataStore,
			Substitute.For<IImageLoader>(),
			new DataAssetStore(),
			new MaterialAssetStore(),
			_importer,
			null!);
		_pipeline.InitializeProject(_root);
	}

	[TearDown]
	public void Cleanup()
	{
		SqliteConnection.ClearAllPools();
		Directory.Delete(_root, true);
	}

	[Test]
	public void LodNodes_ImportAsOneMeshWithCoarserLevels()
	{
		var scene = CreateScene(
		[
			new("Tree", Matrix4x4.Identity, [], -1),
			new("Tree_LOD0", Matrix4x4.Identity, [Mesh("Trunk", 0, 1.0f)], 0),
			new("Tree_LOD1", Matrix4x4.Identity, [Mesh("Trunk", 0, 2.0f)], 0),
			new("Tree_LOD2", Matrix4x4.Identity, [Mesh("Trunk", 0, 3.0f)], 0)
		]);

		Import(scene);

		var model = ReadModel();
		Assert.That(model.Nodes.Select(node => node.Name), Is.EqualTo(new[] { "Tree", "Tree_LOD0" }));
		Assert.That(model.Nodes[1].ParentIndex, Is.EqualTo(0));

		var levelZeroId = model.Nodes[1].Meshes.Single().MeshNodeId;
		var levelZero = ReadMesh(levelZeroId);
		Assert.That(levelZero.Vertices[1].X, Is.EqualTo(1.0f));
		Assert.That(
			levelZero.LodLevelNodeIds,
			Is.EqualTo(new[] { GetMeshNodeId("Tree-LOD1"), GetMeshNodeId("Tree-LOD2") }));
		Assert.That(ReadMesh(levelZero.LodLevelNodeIds[0]).Vertices[1].X, Is.EqualTo(2.0f));
		Assert.That(ReadMesh(levelZero.LodLevelNodeIds[1]).Vertices[1].X, Is.EqualTo(3.0f));
	}

	[Test]
	public void LodNodes_KeepTheMeshIdsTheyHadBeforeGrouping()
	{
		var scene = CreateScene(
		[
			new("Tree_LOD1", Matrix4x4.Identity, [Mesh("Trunk", 0, 2.0f)], -1),
			new("Tree_LOD0", Matrix4x4.Identity, [Mesh("Trunk", 0, 1.0f)], -1)
		]);

		Import(scene);

		var metadata = LoadMetadata();
		Assert.That(
			metadata.SubAssets.Select(asset => asset.Key),
			Is.SupersetOf(new[] { "mesh:root-0-Tree-LOD1:0:Trunk", "mesh:root-1-Tree-LOD0:0:Trunk" }));
	}

	[Test]
	public void LevelPlacedElsewhere_IsMovedIntoLevelZeroSpace()
	{
		var scene = CreateScene(
		[
			new("Tree_LOD0", Matrix4x4.Identity, [Mesh("Trunk", 0, 1.0f)], -1),
			new("Tree_LOD1", Matrix4x4.CreateTranslation(10.0f, 0.0f, 0.0f), [Mesh("Trunk", 0, 1.0f)], -1)
		]);

		Import(scene);

		var levelZero = ReadMesh(ReadModel().Nodes.Single().Meshes.Single().MeshNodeId);
		var levelOne = ReadMesh(levelZero.LodLevelNodeIds.Single());
		Assert.That(levelOne.Vertices[1].X, Is.EqualTo(11.0f).Within(1e-4f));
	}

	[Test]
	public void AssimpPivotNodes_OfCoarserLevels_AreLeftOutOfTheModel()
	{
		var scene = CreateScene(
		[
			new("Tree", Matrix4x4.Identity, [], -1),
			new("Tree_LOD0_$AssimpFbx$_PreRotation", Matrix4x4.Identity, [], 0),
			new("Tree_LOD0", Matrix4x4.Identity, [Mesh("Trunk", 0, 1.0f)], 1),
			new("Tree_LOD1_$AssimpFbx$_PreRotation", Matrix4x4.Identity, [], 0),
			new("Tree_LOD1", Matrix4x4.Identity, [Mesh("Trunk", 0, 2.0f)], 3),
			new("Tree_LOD1_$AssimpFbx$_GeometricRotationInverse", Matrix4x4.Identity, [], 4)
		]);

		Import(scene);

		var model = ReadModel();
		Assert.That(
			model.Nodes.Select(node => node.Name),
			Is.EqualTo(new[] { "Tree", "Tree_LOD0_$AssimpFbx$_PreRotation", "Tree_LOD0" }));
		Assert.That(ReadMesh(model.Nodes[2].Meshes.Single().MeshNodeId).LodLevelNodeIds, Has.Length.EqualTo(1));
	}

	[Test]
	public void MeshesSplitByMaterial_EachGetTheirOwnLevels()
	{
		var scene = CreateScene(
		[
			new("Tree_LOD0", Matrix4x4.Identity, [Mesh("Bark", 0, 1.0f), Mesh("Leaves", 1, 1.0f)], -1),
			new("Tree_LOD1", Matrix4x4.Identity, [Mesh("Leaves", 1, 2.0f), Mesh("Bark", 0, 2.0f)], -1)
		]);

		Import(scene);

		var meshes = ReadModel().Nodes.Single().Meshes;
		var bark = ReadMesh(meshes[0].MeshNodeId);
		var leaves = ReadMesh(meshes[1].MeshNodeId);
		Assert.That(bark.LodLevelNodeIds.Single(), Is.EqualTo(GetMeshNodeId("Tree-LOD1", "Bark")));
		Assert.That(leaves.LodLevelNodeIds.Single(), Is.EqualTo(GetMeshNodeId("Tree-LOD1", "Leaves")));
	}

	[Test]
	public void LevelWithAMeshLevelZeroLacks_ImportsUngrouped()
	{
		var scene = CreateScene(
		[
			new("Tree_LOD0", Matrix4x4.Identity, [Mesh("Bark", 0, 1.0f)], -1),
			new("Tree_LOD1", Matrix4x4.Identity, [Mesh("Bark", 0, 2.0f), Mesh("Leaves", 1, 2.0f)], -1)
		]);

		Import(scene);

		var model = ReadModel();
		Assert.That(model.Nodes, Has.Count.EqualTo(2));
		Assert.That(model.Nodes.SelectMany(node => node.Meshes).Select(mesh => ReadMesh(mesh.MeshNodeId).LodLevelNodeIds),
			Has.All.Empty);
	}

	[Test]
	public void LevelOfDetailDependencies_AreHard()
	{
		var scene = CreateScene(
		[
			new("Tree_LOD0", Matrix4x4.Identity, [Mesh("Trunk", 0, 1.0f)], -1),
			new("Tree_LOD1", Matrix4x4.Identity, [Mesh("Trunk", 0, 2.0f)], -1)
		]);

		Import(scene);

		var levelZeroId = ReadModel().Nodes.Single().Meshes.Single().MeshNodeId;
		var dependency = _pipeline.GetDependencies(_root)
			.Single(record => record.FromNodeId == levelZeroId && record.Kind == "mesh-lod");
		Assert.That(dependency.ToNodeId, Is.EqualTo(GetMeshNodeId("Tree-LOD1")));
		Assert.That(dependency.IsHard, Is.True);
	}

	[Test]
	public void ModelWithTwoRenderersWithLevels_GetsALodGroupAroundBoth()
	{
		var scene = CreateScene(
		[
			new("Tree", Matrix4x4.Identity, [], -1),
			new("Trunk_LOD0", Matrix4x4.Identity, [Mesh("Trunk", 0, 1.0f)], 0),
			new("Trunk_LOD1", Matrix4x4.Identity, [Mesh("Trunk", 0, 2.0f)], 0),
			new("Leaves_LOD0", Matrix4x4.CreateTranslation(0.0f, 10.0f, 0.0f), [Mesh("Leaves", 1, 1.0f)], 0),
			new("Leaves_LOD1", Matrix4x4.CreateTranslation(0.0f, 10.0f, 0.0f), [Mesh("Leaves", 1, 2.0f)], 0)
		]);

		Import(scene);

		var group = ReadModel().LodGroup;
		var trunkBounds = scene.Nodes[1].Meshes[0].Mesh.BoundingSphere;
		var leavesBounds = scene.Nodes[3].Meshes[0].Mesh.BoundingSphere;
		var leavesCenter = leavesBounds.Center + new Vector3(0.0f, 10.0f, 0.0f);
		const float tolerance = 1e-4f;
		Assert.That(group, Is.Not.Null);
		Assert.That(group!.ScreenSizes, Is.EqualTo(new[] { global::WolfEngine.Mesh.DefaultFirstLodScreenSize }));
		Assert.That(
			Vector3.Distance(group.BoundsCenter, trunkBounds.Center) + trunkBounds.Radius,
			Is.LessThanOrEqualTo(group.BoundsRadius + tolerance),
			"the trunk at the origin is inside the group's bounds");
		Assert.That(
			Vector3.Distance(group.BoundsCenter, leavesCenter) + leavesBounds.Radius,
			Is.LessThanOrEqualTo(group.BoundsRadius + tolerance),
			"the leaves 10 m up are inside the group's bounds");
	}

	[Test]
	public void ModelWithOneRendererWithLevels_GetsNoLodGroup()
	{
		var scene = CreateScene(
		[
			new("Rock_LOD0", Matrix4x4.Identity, [Mesh("Rock", 0, 1.0f)], -1),
			new("Rock_LOD1", Matrix4x4.Identity, [Mesh("Rock", 0, 2.0f)], -1)
		]);

		Import(scene);

		Assert.That(ReadModel().LodGroup, Is.Null);
	}

	[Test]
	public void AuthoredThresholds_AreWrittenToLevelZeroMeshes()
	{
		var scene = CreateScene(
		[
			new("Rock_LOD0", Matrix4x4.Identity, [Mesh("Rock", 0, 1.0f)], -1),
			new("Rock_LOD1", Matrix4x4.Identity, [Mesh("Rock", 0, 2.0f)], -1),
			new("Rock_LOD2", Matrix4x4.Identity, [Mesh("Rock", 0, 3.0f)], -1)
		]);
		var metadata = new AssetSourceMetaFile { SourceId = Guid.NewGuid(), ImporterId = AssetImporterIds.ThreeDScene };
		metadata.SetImportSettings(new ModelImportSettings { LodScreenSizes = [0.4f] });
		_metadataStore.Save(Path.Combine(_root, "Assets/Tree.fbx.meta"), metadata);

		Import(scene);

		var levelZero = ReadMesh(ReadModel().Nodes.Single().Meshes.Single().MeshNodeId);
		Assert.That(levelZero.LodScreenSizes, Is.EqualTo(new[] { 0.4f, 0.2f }), "a missing threshold continues halving");
	}

	private void Import(ImportedScene scene)
	{
		_importer.Import(Arg.Any<string>(), Arg.Any<ModelImportSettings>()).Returns(scene);
		File.WriteAllText(Path.Combine(_root, "Assets/Tree.fbx"), "model");
		_pipeline.ReimportSource(_root, "Assets/Tree.fbx");
	}

	private AssetSourceMetaFile LoadMetadata()
	{
		return _metadataStore.Load(Path.Combine(_root, "Assets/Tree.fbx.meta"));
	}

	private ImportedModelAssetFile ReadModel()
	{
		var path = Path.Combine(_root, "Library", "Imported", LoadMetadata().SourceId.ToString("D"), "model3d.asset.json");
		return AssetPipelineSerialization.Deserialize<ImportedModelAssetFile>(File.ReadAllText(path));
	}

	private Guid GetMeshNodeId(string nodeName, string meshName = "Trunk")
	{
		return LoadMetadata().SubAssets
			.Single(asset => asset.Key.StartsWith("mesh:", StringComparison.Ordinal) &&
			                 asset.Key.Contains(nodeName, StringComparison.Ordinal) &&
			                 asset.Key.EndsWith(":" + meshName, StringComparison.Ordinal))
			.NodeId;
	}

	private ImportedMeshAssetFile ReadMesh(Guid meshNodeId)
	{
		var entry = LoadMetadata().SubAssets.Single(asset => asset.NodeId == meshNodeId);
		var fileName = entry.Key.Replace(':', '_') + ".mesh.bin";
		var path = Path.Combine(_root, "Library", "Imported", LoadMetadata().SourceId.ToString("D"), "meshes", fileName);
		return ImportedMeshSerializer.Read(path);
	}

	private static ImportedScene CreateScene(List<ImportedNode> nodes)
	{
		return new ImportedScene(
			"Tree",
			[CreateMaterial(), CreateMaterial()],
			[],
			nodes,
			[],
			[]);
	}

	private static ImportedMaterial CreateMaterial()
	{
		return new ImportedMaterial(
			new Rendering.ColorRGBA(1, 1, 1, 1),
			0.0f,
			1.0f,
			1.0f,
			Vector3.Zero,
			1.0f,
			null,
			null,
			null,
			null,
			null,
			global::WolfEngine.AlphaMode.Opaque,
			0.5f,
			false);
	}

	/// <summary>A distinct triangle per call, whose second vertex sits at <paramref name="marker"/> on X.</summary>
	private static ImportedNodeMesh Mesh(string name, int materialIndex, float marker)
	{
		var mesh = new global::WolfEngine.Mesh(
			[new Vector4(0, 0, 0, 1), new Vector4(marker, 0, 0, 1), new Vector4(0, 1, 0, 1)],
			[0u, 1u, 2u]);

		return new ImportedNodeMesh(name, mesh, materialIndex);
	}
}
