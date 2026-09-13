using System.Numerics;
using NSubstitute;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class EntityCreationOperationsTests
{
	private static readonly Vector3 Position = new(1.0f, 2.0f, 3.0f);

	[Test]
	public void CreateEntity_PointLight_AddsPointLightAtPosition()
	{
		var scene = new EditorScene();

		var entity = Create(scene, EntityCreationPreset.PointLight, out _, out _);

		Assert.That(scene.World.GetComponent<NameComponent>(entity).Name, Is.EqualTo("Point Light"));
		Assert.That(scene.World.GetComponent<LocalTransform>(entity).LocalPosition, Is.EqualTo(Position));
		var light = scene.World.GetComponent<Light>(entity);
		Assert.That(light.Type, Is.EqualTo(LightType.Point));
		Assert.That(light.Intensity, Is.GreaterThan(0.0f));
		Assert.That(light.Range, Is.GreaterThan(0.0f));
		Assert.That(scene.EntityIcons[entity], Is.EqualTo("light"));
	}

	[Test]
	public void CreateEntity_DirectionalLight_PointsBelowHorizon()
	{
		var scene = new EditorScene();

		var entity = Create(scene, EntityCreationPreset.DirectionalLight, out _, out _);

		var light = scene.World.GetComponent<Light>(entity);
		Assert.That(light.Type, Is.EqualTo(LightType.Directional));
		var forward = Vector3.Transform(Vector3.UnitZ, scene.World.GetComponent<LocalTransform>(entity).LocalRotation);
		Assert.That(DirectionalLightUtility.GetIntensityScale(light, forward), Is.EqualTo(1.0f));
		Assert.That(scene.EntityIcons[entity], Is.EqualTo("light"));
	}

	[TestCase(EntityCreationPreset.Cube, "Cube")]
	[TestCase(EntityCreationPreset.Sphere, "Sphere")]
	public void CreateEntity_Primitive_LinksBuiltInMeshAndDefaultMaterial(EntityCreationPreset preset, string expectedName)
	{
		var scene = new EditorScene();

		var entity = Create(scene, preset, out _, out _);

		var expectedMesh = preset == EntityCreationPreset.Cube ? BuiltInEngineAssets.CubeMesh : BuiltInEngineAssets.SphereMesh;
		var renderer = scene.World.GetComponent<MeshRenderer>(entity);
		Assert.That(scene.World.GetComponent<NameComponent>(entity).Name, Is.EqualTo(expectedName));
		Assert.That(renderer.MeshAsset.NodeId, Is.EqualTo(expectedMesh.NodeId));
		Assert.That(renderer.MaterialAsset.NodeId, Is.EqualTo(BuiltInEngineAssets.DefaultMaterial.NodeId));
	}

	[Test]
	public void CreateEntity_SelectsEntityRecordsUndoAndMarksSceneDirty()
	{
		var scene = new EditorScene();

		var entity = Create(scene, EntityCreationPreset.Cube, out var undoRedoService, out var interactionState);

		Assert.That(EditorGui.SelectedEntity, Is.EqualTo(entity));
		undoRedoService.Received(1).BeginCapture("Create Cube");
		undoRedoService.Received(1).CommitCapture(Arg.Any<EntityCreationUndoRedoEntry>());
		interactionState.Received(1).MarkSceneDirty(scene.World);
	}

	[Test]
	public void BuiltInEngineAssets_MatchCommittedMetadata()
	{
		var assetsRoot = Path.Combine(AppContext.BaseDirectory, "BuiltInContent", "Assets");

		AssertNodeId(assetsRoot, "Materials/Default.mat.json", "main", BuiltInEngineAssets.DefaultMaterial.NodeId);
		AssertNodeId(assetsRoot, "Primitives/Cube.obj", "mesh:root-0-Cube:0:Cube", BuiltInEngineAssets.CubeMesh.NodeId);
		AssertNodeId(assetsRoot, "Primitives/Sphere.obj", "mesh:root-0-Sphere:0:Sphere", BuiltInEngineAssets.SphereMesh.NodeId);
	}

	private static Entity Create(
		EditorScene scene,
		EntityCreationPreset preset,
		out IEditorUndoRedoService undoRedoService,
		out IEditorInteractionState interactionState)
	{
		undoRedoService = Substitute.For<IEditorUndoRedoService>();
		interactionState = Substitute.For<IEditorInteractionState>();
		return EntityCreationOperations.CreateEntity(
			scene,
			preset,
			Position,
			new EditorSceneSnapshotService(Substitute.For<IProjectTypeResolver>()),
			undoRedoService,
			interactionState);
	}

	private static void AssertNodeId(string assetsRoot, string relativeSourcePath, string subAssetKey, Guid expectedNodeId)
	{
		var metadata = new AssetMetadataStore().Load(AssetFileExtensions.GetMetaPath(Path.Combine(assetsRoot, relativeSourcePath)));
		var subAsset = metadata.SubAssets.SingleOrDefault(entry => entry.Key == subAssetKey);
		Assert.That(subAsset, Is.Not.Null, $"'{relativeSourcePath}' has no sub-asset '{subAssetKey}'.");
		Assert.That(subAsset!.NodeId, Is.EqualTo(expectedNodeId), relativeSourcePath);
	}
}
