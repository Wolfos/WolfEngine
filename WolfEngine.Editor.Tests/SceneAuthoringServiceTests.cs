using System.Numerics;
using NSubstitute;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.Tests;

/// <summary>
/// Scene authoring tested through its interface with the real undo history and snapshot service, so every
/// case checks what a user sees: the scene after the operation, after undo and after redo.
/// </summary>
[TestFixture]
public sealed class SceneAuthoringServiceTests
{
	private static readonly Vector3 Position = new(1.0f, 2.0f, 3.0f);

	private EditorScene _scene = null!;
	private EditorInteractionState _interactionState = null!;
	private EditorUndoRedoService _history = null!;
	private IAssetCatalog _catalog = null!;
	private IProjectAssetPipelineService _assetPipeline = null!;
	private SceneAuthoringService _authoring = null!;

	[SetUp]
	public void SetUp()
	{
		EditorGui.ClearEntitySelection();
		_scene = new EditorScene();
		_interactionState = new EditorInteractionState();

		var workspace = Substitute.For<IEditorSceneWorkspace>();
		workspace.CurrentScene.Returns(_ => _scene);
		var snapshotService = new EditorSceneSnapshotService(CreateTypeResolver());
		_history = new EditorUndoRedoService(
			workspace,
			_interactionState,
			snapshotService,
			Substitute.For<IEditorAssetSnapshotService>(),
			Substitute.For<ITerrainAssetPersistenceService>(),
			Substitute.For<IEditorPlaySession>());

		_catalog = Substitute.For<IAssetCatalog>();
		var projectService = Substitute.For<IEditorProjectService>();
		projectService.CurrentAssetCatalog.Returns(_catalog);
		_assetPipeline = Substitute.For<IProjectAssetPipelineService>();
		_authoring = new SceneAuthoringService(
			snapshotService,
			_history,
			_interactionState,
			projectService,
			_assetPipeline);
	}

	[TearDown]
	public void TearDown()
	{
		EditorGui.ClearEntitySelection();
	}

	[Test]
	public void CreateEntity_PointLight_AddsPointLightAtPosition()
	{
		var entity = Create(EntityCreationPreset.PointLight);

		Assert.That(_scene.World.GetComponent<NameComponent>(entity).Name, Is.EqualTo("Point Light"));
		Assert.That(_scene.World.GetComponent<LocalTransform>(entity).LocalPosition, Is.EqualTo(Position));
		var light = _scene.World.GetComponent<Light>(entity);
		Assert.That(light.Type, Is.EqualTo(LightType.Point));
		Assert.That(light.Intensity, Is.GreaterThan(0.0f));
		Assert.That(light.Range, Is.GreaterThan(0.0f));
		Assert.That(_scene.EntityIcons[entity], Is.EqualTo("light"));
	}

	[Test]
	public void CreateEntity_DirectionalLight_PointsBelowHorizon()
	{
		var entity = Create(EntityCreationPreset.DirectionalLight);

		var light = _scene.World.GetComponent<Light>(entity);
		Assert.That(light.Type, Is.EqualTo(LightType.Directional));
		var forward = Vector3.Transform(Vector3.UnitZ, _scene.World.GetComponent<LocalTransform>(entity).LocalRotation);
		Assert.That(DirectionalLightUtility.GetIntensityScale(light, forward), Is.EqualTo(1.0f));
	}

	[TestCase(EntityCreationPreset.Cube, "Cube")]
	[TestCase(EntityCreationPreset.Sphere, "Sphere")]
	public void CreateEntity_Primitive_LinksBuiltInMeshAndDefaultMaterial(EntityCreationPreset preset, string expectedName)
	{
		var entity = Create(preset);

		var expectedMesh = preset == EntityCreationPreset.Cube
			? BuiltInEngineAssets.CubeMesh
			: BuiltInEngineAssets.SphereMesh;
		var renderer = _scene.World.GetComponent<MeshRenderer>(entity);
		Assert.That(_scene.World.GetComponent<NameComponent>(entity).Name, Is.EqualTo(expectedName));
		Assert.That(renderer.MeshAsset.NodeId, Is.EqualTo(expectedMesh.NodeId));
		Assert.That(renderer.MaterialAsset.NodeId, Is.EqualTo(BuiltInEngineAssets.DefaultMaterial.NodeId));
	}

	[Test]
	public void CreateEntity_WithName_UsesTheTrimmedName()
	{
		var entity = _authoring.CreateEntity(_scene, EntityCreationPreset.Empty, Vector3.Zero, "  Spawn Point ").Value;

		Assert.That(_scene.World.GetComponent<NameComponent>(entity).Name, Is.EqualTo("Spawn Point"));
	}

	[Test]
	public void CreateEntity_IsOneUndoStepThatRedoRestoresWithTheSameId()
	{
		var entity = Create(EntityCreationPreset.PointLight);
		var entityId = _scene.EntityIds[entity];

		Assert.That(_interactionState.IsSceneDirty, Is.True);
		Assert.That(_history.Undo(), Is.True);
		Assert.That(_scene.TryGetEntity(entityId, out _), Is.False);

		Assert.That(_history.Redo(), Is.True);
		Assert.That(_scene.TryGetEntity(entityId, out var restored), Is.True);
		Assert.That(_scene.World.GetComponent<NameComponent>(restored).Name, Is.EqualTo("Point Light"));
		Assert.That(_scene.World.GetComponent<Light>(restored).Type, Is.EqualTo(LightType.Point));
	}

	[Test]
	public void CreateEntity_LeavesSelectionToTheCaller()
	{
		Create(EntityCreationPreset.Empty);

		Assert.That(EditorGui.HasSelectedEntity, Is.False);
	}

	[Test]
	public void CreateEntity_InRuntimeScene_IsAppliedButNotRecorded()
	{
		_scene = new EditorScene { World = new World(WorldTag.Game) };

		var entity = Create(EntityCreationPreset.Empty);

		Assert.That(_scene.World.IsAlive(entity), Is.True);
		Assert.That(_scene.EntityIds.ContainsKey(entity), Is.True);
		Assert.That(_history.CanUndo, Is.False);
		Assert.That(_interactionState.IsSceneDirty, Is.False);
	}

	[Test]
	public void DuplicateEntity_ClonesSubtreeWithFreshIdsAsOneUndoStep()
	{
		var parent = _scene.World.CreateEntity("Parent", new Vector3(1.0f, 2.0f, 3.0f), Quaternion.Identity, Vector3.One);
		var child = _scene.World.CreateEntity("Child", new Vector3(4.0f, 5.0f, 6.0f), Quaternion.Identity, Vector3.One);
		_scene.World.SetParent(child, parent);
		_scene.EntityIcons[parent] = "light";
		_scene.EntityIcons[child] = "object";
		_scene.EntityIds[parent] = Guid.NewGuid();
		_scene.EntityIds[child] = Guid.NewGuid();

		var result = _authoring.DuplicateEntity(_scene, parent);

		Assert.That(result.Succeeded, Is.True);
		var duplicate = result.Value;
		Assert.That(duplicate, Is.Not.EqualTo(parent));
		Assert.That(_scene.World.GetComponent<NameComponent>(duplicate).Name, Is.EqualTo("Parent"));
		Assert.That(_scene.EntityIds[duplicate], Is.Not.EqualTo(_scene.EntityIds[parent]));
		Assert.That(_scene.EntityIcons[duplicate], Is.EqualTo("light"));

		var duplicateChild = _scene.World.GetComponent<Children>(duplicate).First;
		Assert.That(duplicateChild.IsValid, Is.True);
		Assert.That(_scene.World.GetComponent<NameComponent>(duplicateChild).Name, Is.EqualTo("Child"));
		Assert.That(_scene.World.GetComponent<Parent>(duplicateChild).Value, Is.EqualTo(duplicate));
		Assert.That(_scene.EntityIds[duplicateChild], Is.Not.EqualTo(_scene.EntityIds[child]));
		Assert.That(_scene.EntityIcons[duplicateChild], Is.EqualTo("object"));
		Assert.That(_interactionState.IsSceneDirty, Is.True);

		var duplicateId = _scene.EntityIds[duplicate];
		var duplicateChildId = _scene.EntityIds[duplicateChild];
		Assert.That(_history.Undo(), Is.True);
		Assert.That(_scene.TryGetEntity(duplicateId, out _), Is.False);
		Assert.That(_scene.TryGetEntity(duplicateChildId, out _), Is.False);
		Assert.That(_scene.World.IsAlive(parent), Is.True);
		Assert.That(_scene.World.IsAlive(child), Is.True);
	}

	[Test]
	public void DuplicateEntity_RetargetsInternalReferencesAndKeepsExternalOnes()
	{
		var parent = _scene.World.CreateEntity("Parent", Vector3.Zero, Quaternion.Identity, Vector3.One);
		var child = _scene.World.CreateEntity("Child", Vector3.Zero, Quaternion.Identity, Vector3.One);
		_scene.World.SetParent(child, parent);
		var external = _scene.World.CreateEntity("External");
		_scene.EntityIds[external] = Guid.NewGuid();
		_scene.World.AddComponent(parent, new EntityReferenceComponent
		{
			Target = child,
			External = external
		});

		var duplicate = _authoring.DuplicateEntity(_scene, parent).Value;

		var duplicateChild = _scene.World.GetComponent<Children>(duplicate).First;
		var component = _scene.World.GetComponent<EntityReferenceComponent>(duplicate);
		Assert.That(duplicateChild.IsValid, Is.True);
		Assert.That(duplicateChild, Is.Not.EqualTo(child));
		Assert.That(component.Target, Is.EqualTo(duplicateChild));
		Assert.That(component.External, Is.EqualTo(external));
		Assert.That(_scene.World.GetComponent<EntityReferenceComponent>(parent).Target, Is.EqualTo(child));
	}

	[Test]
	public void DuplicateEntity_InsidePrefabInstance_IsRefusedWithAReason()
	{
		var (_, nestedChild) = CreatePrefabInstance();

		var result = _authoring.DuplicateEntity(_scene, nestedChild);

		Assert.That(result.Succeeded, Is.False);
		Assert.That(result.RefusalReason, Does.Contain("prefab"));
		Assert.That(_history.CanUndo, Is.False);
	}

	[Test]
	public void DeleteEntities_DeletesSubtreeAndUndoRestoresItWithTheSameIds()
	{
		var parent = _scene.World.CreateEntity("Parent", Vector3.Zero, Quaternion.Identity, Vector3.One);
		var child = _scene.World.CreateEntity("Child", Vector3.One, Quaternion.Identity, Vector3.One);
		_scene.World.SetParent(child, parent);
		var parentId = Guid.NewGuid();
		var childId = Guid.NewGuid();
		_scene.EntityIds[parent] = parentId;
		_scene.EntityIds[child] = childId;

		var result = _authoring.DeleteEntities(_scene, [parent]);

		Assert.That(result.Succeeded, Is.True);
		Assert.That(_scene.World.IsAlive(parent), Is.False);
		Assert.That(_scene.World.IsAlive(child), Is.False);
		Assert.That(_scene.EntityIds.Count, Is.Zero);
		Assert.That(_interactionState.IsSceneDirty, Is.True);

		Assert.That(_history.Undo(), Is.True);
		Assert.That(_scene.TryGetEntity(parentId, out var restoredParent), Is.True);
		Assert.That(_scene.TryGetEntity(childId, out var restoredChild), Is.True);
		Assert.That(_scene.World.GetComponent<Parent>(restoredChild).Value, Is.EqualTo(restoredParent));

		Assert.That(_history.Redo(), Is.True);
		Assert.That(_scene.TryGetEntity(parentId, out _), Is.False);
	}

	[Test]
	public void DeleteEntities_SeveralEntities_IsOneUndoStep()
	{
		var first = _scene.World.CreateEntity("First", Vector3.Zero, Quaternion.Identity, Vector3.One);
		var second = _scene.World.CreateEntity("Second", Vector3.Zero, Quaternion.Identity, Vector3.One);

		_authoring.DeleteEntities(_scene, [first, second]);

		Assert.That(_history.Undo(), Is.True);
		Assert.That(_scene.EntityIds.Count, Is.EqualTo(2));
		Assert.That(_history.CanUndo, Is.False);
	}

	[Test]
	public void DeleteEntities_RemovesOnlyTheDeletedEntitiesFromSelection()
	{
		var deleted = _scene.World.CreateEntity("Deleted", Vector3.Zero, Quaternion.Identity, Vector3.One);
		var kept = _scene.World.CreateEntity("Kept", Vector3.Zero, Quaternion.Identity, Vector3.One);
		EditorGui.SelectEntity(deleted, _scene.World, requestFocus: false);
		EditorGui.AddEntitySelection(kept, _scene.World, requestFocus: false);

		_authoring.DeleteEntities(_scene, [deleted]);

		Assert.That(EditorGui.SelectedEntities, Is.EqualTo(new[] { kept }));
		Assert.That(EditorGui.SelectedEntity, Is.EqualTo(kept));
	}

	[Test]
	public void DeleteEntities_InsidePrefabInstance_IsRefusedAndDeletesNothing()
	{
		var (root, nestedChild) = CreatePrefabInstance();

		var result = _authoring.DeleteEntities(_scene, [nestedChild]);

		Assert.That(result.Succeeded, Is.False);
		Assert.That(result.RefusalReason, Does.Contain("prefab"));
		Assert.That(_scene.World.IsAlive(nestedChild), Is.True);
		Assert.That(_scene.World.IsAlive(root), Is.True);
		Assert.That(_history.CanUndo, Is.False);
	}

	[Test]
	public void DeleteEntities_PrefabRootWithItsOwnChildren_DeletesTheWholeInstance()
	{
		var (root, nestedChild) = CreatePrefabInstance();

		var result = _authoring.DeleteEntities(_scene, [root, nestedChild]);

		Assert.That(result.Succeeded, Is.True);
		Assert.That(_scene.World.IsAlive(root), Is.False);
		Assert.That(_scene.World.IsAlive(nestedChild), Is.False);
	}

	[Test]
	public void DeleteEntities_InRuntimeScene_IsAppliedButNotRecorded()
	{
		_scene = new EditorScene { World = new World(WorldTag.Game) };
		var entity = _scene.World.CreateEntity("Entity");

		var result = _authoring.DeleteEntities(_scene, [entity]);

		Assert.That(result.Succeeded, Is.True);
		Assert.That(_scene.World.IsAlive(entity), Is.False);
		Assert.That(_history.CanUndo, Is.False);
		Assert.That(_interactionState.IsSceneDirty, Is.False);
	}

	[Test]
	public void Instantiate_Prefab_ReturnsRootAndSubtreeAsOneUndoStep()
	{
		var prefabId = RegisterAsset(AssetType.Prefab, "Barrel");
		var rootEntity = default(Entity);
		var childEntity = default(Entity);
		_assetPipeline
			.When(pipeline => pipeline.InstantiatePrefab(_catalog, prefabId, Arg.Any<EditorScene>(), Arg.Any<Vector3?>()))
			.Do(call =>
			{
				var scene = call.ArgAt<EditorScene>(2);
				rootEntity = scene.World.CreateEntity("Barrel", Position, Quaternion.Identity, Vector3.One);
				childEntity = scene.World.CreateEntity("Lid", Vector3.Zero, Quaternion.Identity, Vector3.One);
				scene.World.SetParent(childEntity, rootEntity);
			});

		var result = _authoring.Instantiate(_scene, prefabId, Position);

		Assert.That(result.Succeeded, Is.True);
		Assert.That(result.Value.Root, Is.EqualTo(rootEntity));
		Assert.That(result.Value.Entities, Is.EqualTo(new[] { rootEntity, childEntity }));
		Assert.That(_scene.EntityIds.ContainsKey(rootEntity), Is.True);
		Assert.That(_scene.EntityIds.ContainsKey(childEntity), Is.True);
		Assert.That(_interactionState.IsSceneDirty, Is.True);

		Assert.That(_history.Undo(), Is.True);
		Assert.That(_scene.World.IsAlive(rootEntity), Is.False);
		Assert.That(_scene.World.IsAlive(childEntity), Is.False);
	}

	[Test]
	public void Instantiate_Model_GoesThroughTheProjectCatalog()
	{
		var modelId = RegisterAsset(AssetType.Model3D, "Car");
		_assetPipeline
			.When(pipeline => pipeline.InstantiateImportedModel(_catalog, modelId, Arg.Any<World>(), Arg.Any<Vector3?>()))
			.Do(call => call.ArgAt<World>(2).CreateEntity("Car", Position, Quaternion.Identity, Vector3.One));

		var result = _authoring.Instantiate(_scene, modelId, Position);

		Assert.That(result.Succeeded, Is.True);
		Assert.That(_scene.World.GetComponent<NameComponent>(result.Value.Root).Name, Is.EqualTo("Car"));
		Assert.That(_history.CanUndo, Is.True);
	}

	[Test]
	public void Instantiate_UnknownAsset_IsRefused()
	{
		var result = _authoring.Instantiate(_scene, Guid.NewGuid(), null);

		Assert.That(result.Succeeded, Is.False);
		Assert.That(_history.CanUndo, Is.False);
	}

	[Test]
	public void Instantiate_AssetThatIsNotAModelOrPrefab_IsRefused()
	{
		var materialId = RegisterAsset(AssetType.Material, "Red");

		var result = _authoring.Instantiate(_scene, materialId, null);

		Assert.That(result.Succeeded, Is.False);
		Assert.That(result.RefusalReason, Does.Contain("Red"));
	}

	[Test]
	public void ReparentEntities_ParentsAndKeepsWorldTransform_UndoPutsItBack()
	{
		var transformSystem = new TransformSystem();
		var parent = _scene.World.CreateEntity("Parent", new Vector3(10.0f, 0.0f, 0.0f), Quaternion.Identity, Vector3.One);
		var child = _scene.World.CreateEntity("Child", new Vector3(2.0f, 3.0f, 4.0f), Quaternion.Identity, Vector3.One);
		transformSystem.PreRender(0.0f, _scene.World);
		var beforeWorldTransform = _scene.World.GetComponent<WorldTransform>(child).LocalToWorld;

		var result = _authoring.ReparentEntities(_scene, [child], parent);
		transformSystem.PreRender(0.0f, _scene.World);

		Assert.That(result.Succeeded, Is.True);
		Assert.That(result.Value, Is.EqualTo(new[] { child }));
		Assert.That(_scene.World.GetComponent<Parent>(child).Value, Is.EqualTo(parent));
		AssertMatrix(_scene.World.GetComponent<WorldTransform>(child).LocalToWorld, beforeWorldTransform);
		Assert.That(_interactionState.IsSceneDirty, Is.True);

		Assert.That(_history.Undo(), Is.True);
		Assert.That(_scene.World.HasComponent<Parent>(child), Is.False);
		Assert.That(_scene.World.GetComponent<LocalTransform>(child).LocalPosition, Is.EqualTo(new Vector3(2.0f, 3.0f, 4.0f)));
	}

	[Test]
	public void ReparentEntities_ToRoot_UnparentsAndKeepsWorldTransform()
	{
		var transformSystem = new TransformSystem();
		var parent = _scene.World.CreateEntity("Parent", new Vector3(10.0f, 0.0f, 0.0f), Quaternion.Identity, Vector3.One);
		var child = _scene.World.CreateEntity("Child", new Vector3(2.0f, 3.0f, 4.0f), Quaternion.Identity, Vector3.One);
		_scene.World.SetParent(child, parent);
		transformSystem.PreRender(0.0f, _scene.World);
		var beforeWorldTransform = _scene.World.GetComponent<WorldTransform>(child).LocalToWorld;

		var result = _authoring.ReparentEntities(_scene, [child], null);
		transformSystem.PreRender(0.0f, _scene.World);

		Assert.That(result.Succeeded, Is.True);
		Assert.That(_scene.World.HasComponent<Parent>(child), Is.False);
		AssertMatrix(_scene.World.GetComponent<WorldTransform>(child).LocalToWorld, beforeWorldTransform);
	}

	[Test]
	public void ReparentEntities_AlreadyInPlace_IsRefusedAndRecordsNothing()
	{
		var parent = _scene.World.CreateEntity("Parent", Vector3.Zero, Quaternion.Identity, Vector3.One);
		var child = _scene.World.CreateEntity("Child", Vector3.One, Quaternion.Identity, Vector3.One);
		_scene.World.SetParent(child, parent);

		var result = _authoring.ReparentEntities(_scene, [child], parent);

		Assert.That(result.Succeeded, Is.False);
		Assert.That(_history.CanUndo, Is.False);
		Assert.That(_interactionState.IsSceneDirty, Is.False);
	}

	[Test]
	public void ReparentEntities_UnderOwnDescendant_IsRefusedAndKeepsHierarchy()
	{
		var parent = _scene.World.CreateEntity("Parent", Vector3.Zero, Quaternion.Identity, Vector3.One);
		var child = _scene.World.CreateEntity("Child", Vector3.One, Quaternion.Identity, Vector3.One);
		var grandchild = _scene.World.CreateEntity("Grandchild", Vector3.One * 2.0f, Quaternion.Identity, Vector3.One);
		_scene.World.SetParent(child, parent);
		_scene.World.SetParent(grandchild, child);

		var result = _authoring.ReparentEntities(_scene, [parent], grandchild);

		Assert.That(result.Succeeded, Is.False);
		Assert.That(_scene.World.HasComponent<Parent>(parent), Is.False);
		Assert.That(_scene.World.GetComponent<Parent>(child).Value, Is.EqualTo(parent));
		Assert.That(_scene.World.GetComponent<Parent>(grandchild).Value, Is.EqualTo(child));
		Assert.That(_history.CanUndo, Is.False);
	}

	[Test]
	public void TransformEdit_Commit_IsOneUndoStep()
	{
		var entity = _scene.World.CreateEntity("Moved", Matrix4x4.CreateTranslation(Position));
		new TransformSystem().PreRender(0.0f, _scene.World);

		var edit = _authoring.BeginTransformEdit(_scene, [entity]);
		edit.SetWorldPosition(entity, new Vector3(5.0f, 2.0f, 3.0f));
		edit.SetWorldPosition(entity, new Vector3(6.0f, 2.0f, 3.0f));
		var changed = edit.Commit();

		Assert.That(changed, Is.True);
		Assert.That(_scene.World.GetComponent<LocalTransform>(entity).LocalPosition, Is.EqualTo(new Vector3(6.0f, 2.0f, 3.0f)));
		Assert.That(_interactionState.IsSceneDirty, Is.True);

		Assert.That(_history.Undo(), Is.True);
		Assert.That(_scene.World.GetComponent<LocalTransform>(entity).LocalPosition, Is.EqualTo(Position));
		Assert.That(_history.CanUndo, Is.False);
	}

	[Test]
	public void TransformEdit_CommitThatChangedNothing_RecordsNothing()
	{
		var entity = _scene.World.CreateEntity("Still", Matrix4x4.CreateTranslation(Position));

		var edit = _authoring.BeginTransformEdit(_scene, [entity]);
		var changed = edit.Commit();

		Assert.That(changed, Is.False);
		Assert.That(_history.CanUndo, Is.False);
		Assert.That(_interactionState.IsSceneDirty, Is.False);
	}

	[Test]
	public void TransformEdit_Cancel_RestoresTransformsAndRecordsNothing()
	{
		var entity = _scene.World.CreateEntity("Moved", Matrix4x4.CreateTranslation(Position));

		var edit = _authoring.BeginTransformEdit(_scene, [entity]);
		edit.SetLocalScale(entity, new Vector3(2.0f));
		edit.SetLocalRotation(entity, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.0f));
		edit.Cancel();

		var localTransform = _scene.World.GetComponent<LocalTransform>(entity);
		Assert.That(localTransform.LocalScale, Is.EqualTo(Vector3.One));
		Assert.That(localTransform.LocalRotation, Is.EqualTo(Quaternion.Identity));
		Assert.That(localTransform.LocalPosition, Is.EqualTo(Position));
		Assert.That(_history.CanUndo, Is.False);
	}

	[Test]
	public void TransformEdit_DestroyedEntity_IsSkipped()
	{
		var entity = _scene.World.CreateEntity("Moved", Matrix4x4.CreateTranslation(Position));

		var edit = _authoring.BeginTransformEdit(_scene, [entity]);
		_scene.World.DestroyEntity(entity);

		Assert.DoesNotThrow(() => edit.SetWorldPosition(entity, Vector3.Zero));
		Assert.That(edit.Commit(), Is.False);
		Assert.That(_history.CanUndo, Is.False);
	}

	[Test]
	public void TransformEdit_UsedAfterItEnded_Throws()
	{
		var entity = _scene.World.CreateEntity("Moved", Matrix4x4.CreateTranslation(Position));

		var edit = _authoring.BeginTransformEdit(_scene, [entity]);
		edit.Commit();

		Assert.Throws<InvalidOperationException>(() => edit.SetLocalScale(entity, Vector3.One));
		Assert.Throws<InvalidOperationException>(() => edit.Commit());
	}

	[Test]
	public void TransformEdit_InRuntimeScene_IsAppliedButNotRecorded()
	{
		_scene = new EditorScene { World = new World(WorldTag.Game) };
		var entity = _scene.World.CreateEntity("Moved", Matrix4x4.CreateTranslation(Position));

		var edit = _authoring.BeginTransformEdit(_scene, [entity]);
		edit.SetLocalScale(entity, new Vector3(3.0f));

		Assert.That(edit.Commit(), Is.True);
		Assert.That(_scene.World.GetComponent<LocalTransform>(entity).LocalScale, Is.EqualTo(new Vector3(3.0f)));
		Assert.That(_history.CanUndo, Is.False);
		Assert.That(_interactionState.IsSceneDirty, Is.False);
	}

	[Test]
	public void ComponentEdit_RepeatedApply_IsOneUndoStep()
	{
		var entity = CreateLight(intensity: 1.0f);

		var edit = _authoring.BeginComponentEdit(_scene, [entity], typeof(Light), "Edit Light");
		edit.Apply(target => _scene.World.GetComponent<Light>(target).Intensity = 2.0f);
		edit.Apply(target => _scene.World.GetComponent<Light>(target).Intensity = 3.0f);
		var changed = edit.Commit();

		Assert.That(changed, Is.True);
		Assert.That(_scene.World.GetComponent<Light>(entity).Intensity, Is.EqualTo(3.0f));
		Assert.That(_interactionState.IsSceneDirty, Is.True);

		Assert.That(_history.Undo(), Is.True);
		Assert.That(_scene.World.GetComponent<Light>(entity).Intensity, Is.EqualTo(1.0f));
		Assert.That(_history.CanUndo, Is.False);

		Assert.That(_history.Redo(), Is.True);
		Assert.That(_scene.World.GetComponent<Light>(entity).Intensity, Is.EqualTo(3.0f));
	}

	[Test]
	public void ComponentEdit_SeveralEntities_RecordsOnlyThoseThatChanged()
	{
		var changedEntity = CreateLight(intensity: 1.0f);
		var unchangedEntity = CreateLight(intensity: 5.0f);

		var edit = _authoring.BeginComponentEdit(_scene, [changedEntity, unchangedEntity], typeof(Light), "Edit Light");
		edit.Apply(target => _scene.World.GetComponent<Light>(target).Intensity = 5.0f);
		edit.Commit();

		_scene.World.GetComponent<Light>(unchangedEntity).Intensity = 7.0f;
		Assert.That(_history.Undo(), Is.True);

		Assert.That(_scene.World.GetComponent<Light>(changedEntity).Intensity, Is.EqualTo(1.0f));
		Assert.That(_scene.World.GetComponent<Light>(unchangedEntity).Intensity, Is.EqualTo(7.0f));
	}

	[Test]
	public void ComponentEdit_OnlyIncludesEntitiesThatHaveTheComponent()
	{
		var light = CreateLight(intensity: 1.0f);
		var plain = _scene.World.CreateEntity("Plain");

		var edit = _authoring.BeginComponentEdit(_scene, [light, plain], typeof(Light), "Edit Light");

		Assert.That(edit.Entities, Is.EqualTo(new[] { light }));
		edit.Cancel();
	}

	[Test]
	public void ComponentEdit_CommitThatChangedNothing_RecordsNothing()
	{
		var entity = CreateLight(intensity: 1.0f);

		var edit = _authoring.BeginComponentEdit(_scene, [entity], typeof(Light), "Edit Light");
		edit.Apply(target => _scene.World.GetComponent<Light>(target).Intensity = 1.0f);

		Assert.That(edit.Commit(), Is.False);
		Assert.That(_history.CanUndo, Is.False);
		Assert.That(_interactionState.IsSceneDirty, Is.False);
	}

	[Test]
	public void ComponentEdit_Cancel_RestoresTheComponent()
	{
		var entity = CreateLight(intensity: 1.0f);

		var edit = _authoring.BeginComponentEdit(_scene, [entity], typeof(Light), "Edit Light");
		edit.Apply(target => _scene.World.GetComponent<Light>(target).Intensity = 9.0f);
		edit.Cancel();

		Assert.That(_scene.World.GetComponent<Light>(entity).Intensity, Is.EqualTo(1.0f));
		Assert.That(_history.CanUndo, Is.False);
	}

	[Test]
	public void ComponentEdit_ComponentRemovedDuringEdit_IsSkipped()
	{
		var entity = CreateLight(intensity: 1.0f);

		var edit = _authoring.BeginComponentEdit(_scene, [entity], typeof(Light), "Edit Light");
		_scene.World.RemoveComponent<Light>(entity);

		Assert.DoesNotThrow(() => edit.Apply(target => _scene.World.GetComponent<Light>(target).Intensity = 2.0f));
		Assert.That(edit.Commit(), Is.False);
		Assert.That(_history.CanUndo, Is.False);
	}

	[Test]
	public void ComponentEdit_InRuntimeScene_IsAppliedButNotRecorded()
	{
		_scene = new EditorScene { World = new World(WorldTag.Game) };
		var entity = CreateLight(intensity: 1.0f);

		var edit = _authoring.BeginComponentEdit(_scene, [entity], typeof(Light), "Edit Light");
		edit.Apply(target => _scene.World.GetComponent<Light>(target).Intensity = 4.0f);

		Assert.That(edit.Commit(), Is.True);
		Assert.That(_scene.World.GetComponent<Light>(entity).Intensity, Is.EqualTo(4.0f));
		Assert.That(_history.CanUndo, Is.False);
		Assert.That(_interactionState.IsSceneDirty, Is.False);
	}

	[Test]
	public void BeginComponentEdit_ForLocalTransform_IsATransformEdit()
	{
		var entity = _scene.World.CreateEntity("Moved", Matrix4x4.CreateTranslation(Position));

		var edit = _authoring.BeginComponentEdit(_scene, [entity], typeof(LocalTransform), "Edit Transform");
		edit.Apply(target => _scene.World.SetLocalPosition(target, Vector3.Zero));
		edit.Commit();

		Assert.That(edit, Is.InstanceOf<ISceneTransformEdit>());
		Assert.That(_history.Undo(), Is.True);
		Assert.That(_scene.World.GetComponent<LocalTransform>(entity).LocalPosition, Is.EqualTo(Position));
	}

	[Test]
	public void AddComponent_AddsDefaultsAndIconAsOneUndoStep()
	{
		var entity = _scene.World.CreateEntity("Entity", Vector3.Zero, Quaternion.Identity, Vector3.One);

		var result = _authoring.AddComponent(_scene, entity, typeof(IconComponent));

		Assert.That(result.Succeeded, Is.True);
		Assert.That(_scene.World.HasComponent<IconComponent>(entity), Is.True);
		Assert.That(_scene.EntityIcons[entity], Is.EqualTo("flag"));
		Assert.That(_interactionState.IsSceneDirty, Is.True);

		Assert.That(_history.Undo(), Is.True);
		Assert.That(_scene.World.HasComponent<IconComponent>(entity), Is.False);
		Assert.That(_scene.EntityIcons.ContainsKey(entity), Is.False);

		Assert.That(_history.Redo(), Is.True);
		Assert.That(_scene.World.HasComponent<IconComponent>(entity), Is.True);
		Assert.That(_scene.EntityIcons[entity], Is.EqualTo("flag"));
	}

	[Test]
	public void AddComponent_KeepsAnIconTheEntityAlreadyHas()
	{
		var entity = _scene.World.CreateEntity("Entity");
		_scene.EntityIcons[entity] = "light";

		_authoring.AddComponent(_scene, entity, typeof(IconComponent));

		Assert.That(_scene.EntityIcons[entity], Is.EqualTo("light"));
	}

	[Test]
	public void AddComponent_AlreadyPresent_IsRefused()
	{
		var entity = CreateLight(intensity: 1.0f);

		var result = _authoring.AddComponent(_scene, entity, typeof(Light));

		Assert.That(result.Succeeded, Is.False);
		Assert.That(_history.CanUndo, Is.False);
	}

	[Test]
	public void RemoveComponent_UndoRestoresItsValues()
	{
		var entity = CreateLight(intensity: 6.0f);

		var result = _authoring.RemoveComponent(_scene, entity, typeof(Light));

		Assert.That(result.Succeeded, Is.True);
		Assert.That(_scene.World.HasComponent<Light>(entity), Is.False);

		Assert.That(_history.Undo(), Is.True);
		Assert.That(_scene.World.GetComponent<Light>(entity).Intensity, Is.EqualTo(6.0f));
	}

	[Test]
	public void RemoveComponent_InRuntimeScene_IsAppliedButNotRecorded()
	{
		_scene = new EditorScene { World = new World(WorldTag.Game) };
		var entity = CreateLight(intensity: 1.0f);

		_authoring.RemoveComponent(_scene, entity, typeof(Light));

		Assert.That(_scene.World.HasComponent<Light>(entity), Is.False);
		Assert.That(_history.CanUndo, Is.False);
	}

	[Test]
	public void SetEnabled_IsOneUndoStep()
	{
		var entity = _scene.World.CreateEntity("Entity");

		var result = _authoring.SetEnabled(_scene, entity, false);

		Assert.That(result.Succeeded, Is.True);
		Assert.That(_scene.World.IsEnabled(entity), Is.False);
		Assert.That(_interactionState.IsSceneDirty, Is.True);

		Assert.That(_history.Undo(), Is.True);
		Assert.That(_scene.World.IsEnabled(entity), Is.True);

		Assert.That(_history.Redo(), Is.True);
		Assert.That(_scene.World.IsEnabled(entity), Is.False);
	}

	[Test]
	public void SetEnabled_ToTheCurrentState_IsRefused()
	{
		var entity = _scene.World.CreateEntity("Entity");

		var result = _authoring.SetEnabled(_scene, entity, true);

		Assert.That(result.Succeeded, Is.False);
		Assert.That(_history.CanUndo, Is.False);
	}

	[Test]
	public void SetIcon_UndoRestoresTheDefaultIcon()
	{
		var entity = _scene.World.CreateEntity("Entity");

		_authoring.SetIcon(_scene, entity, "light");

		Assert.That(_scene.EntityIcons[entity], Is.EqualTo("light"));
		Assert.That(_history.Undo(), Is.True);
		Assert.That(_scene.EntityIcons.ContainsKey(entity), Is.False);
		Assert.That(_history.Redo(), Is.True);
		Assert.That(_scene.EntityIcons[entity], Is.EqualTo("light"));
	}

	[Test]
	public void SetIcon_InRuntimeScene_IsAppliedButNotRecorded()
	{
		_scene = new EditorScene { World = new World(WorldTag.Game) };
		var entity = _scene.World.CreateEntity("Entity");

		_authoring.SetIcon(_scene, entity, "light");

		Assert.That(_scene.EntityIcons[entity], Is.EqualTo("light"));
		Assert.That(_history.CanUndo, Is.False);
	}

	[Test]
	public void GizmoTarget_ReportsWorldPositionAndLocalScale()
	{
		var entity = _scene.World.CreateEntity("Moved", Matrix4x4.CreateTranslation(Position));
		new TransformSystem().PreRender(0.0f, _scene.World);
		var target = new SceneEntityGizmoTarget(_scene, [entity], _authoring);

		var state = target.GetState(0);

		Assert.That(state.WorldPosition, Is.EqualTo(Position));
		Assert.That(state.WorldRotation, Is.EqualTo(Quaternion.Identity));
		Assert.That(state.LocalScale, Is.EqualTo(Vector3.One));
	}

	[TestCase(true)]
	[TestCase(false)]
	public void GizmoTarget_ReleasedOrAbandonedDrag_IsOneUndoStep(bool released)
	{
		var entity = _scene.World.CreateEntity("Moved", Matrix4x4.CreateTranslation(Position));
		new TransformSystem().PreRender(0.0f, _scene.World);
		var target = new SceneEntityGizmoTarget(_scene, [entity], _authoring);

		target.BeginEdit();
		target.SetWorldPosition(0, new Vector3(5.0f, 2.0f, 3.0f));
		target.OnTransformsChanged();
		target.EndEdit(commit: released);

		Assert.That(_scene.World.GetComponent<LocalTransform>(entity).LocalPosition, Is.EqualTo(new Vector3(5.0f, 2.0f, 3.0f)));
		Assert.That(_history.Undo(), Is.True);
		Assert.That(_scene.World.GetComponent<LocalTransform>(entity).LocalPosition, Is.EqualTo(Position));
	}

	[Test]
	public void TryGetEntity_FindsLiveEntitiesOnly()
	{
		var entity = _scene.World.CreateEntity("Entity");
		var entityId = Guid.NewGuid();
		_scene.EntityIds[entity] = entityId;

		Assert.That(_scene.TryGetEntity(entityId, out var found), Is.True);
		Assert.That(found, Is.EqualTo(entity));

		_scene.World.DestroyEntity(entity);

		Assert.That(_scene.TryGetEntity(entityId, out _), Is.False);
		Assert.That(_scene.TryGetEntity(Guid.Empty, out _), Is.False);
	}

	private Entity Create(EntityCreationPreset preset)
	{
		var result = _authoring.CreateEntity(_scene, preset, Position);
		Assert.That(result.Succeeded, Is.True);

		return result.Value;
	}

	private Entity CreateLight(float intensity)
	{
		var entity = _scene.World.CreateEntity("Light", Vector3.Zero, Quaternion.Identity, Vector3.One);
		_scene.World.AddComponent(entity, new Light
		{
			Type = LightType.Point,
			Color = ColorRGBA.White,
			Intensity = intensity,
			Range = 10.0f
		});

		return entity;
	}

	private (Entity Root, Entity NestedChild) CreatePrefabInstance()
	{
		var prefabId = Guid.NewGuid();
		var root = _scene.World.CreateEntity("Prefab Root", Vector3.Zero, Quaternion.Identity, Vector3.One);
		var nestedChild = _scene.World.CreateEntity("Prefab Child", Vector3.Zero, Quaternion.Identity, Vector3.One);
		_scene.World.SetParent(nestedChild, root);
		_scene.EntityPrefabSourcePaths[root] = [new SavedPrefabLink { PrefabAssetId = prefabId, PrefabEntityId = Guid.NewGuid() }];
		_scene.EntityPrefabSourcePaths[nestedChild] = [new SavedPrefabLink { PrefabAssetId = prefabId, PrefabEntityId = Guid.NewGuid() }];

		return (root, nestedChild);
	}

	private Guid RegisterAsset(AssetType type, string name)
	{
		var assetId = Guid.NewGuid();
		var mountedAsset = new MountedAsset(
			Substitute.For<IAssetMount>(),
			new AssetDatabaseEntry
			{
				Id = assetId,
				Type = type,
				Name = name
			});
		_catalog.TryGetAsset(assetId, out Arg.Any<MountedAsset>())
			.Returns(call =>
			{
				call[1] = mountedAsset;
				return true;
			});

		return assetId;
	}

	private static IProjectTypeResolver CreateTypeResolver()
	{
		var resolver = Substitute.For<IProjectTypeResolver>();
		resolver.GetTypeName(Arg.Any<Type>()).Returns(call => call.Arg<Type>().AssemblyQualifiedName);
		resolver.GetStableTypeId(Arg.Any<Type>()).Returns(call => call.Arg<Type>().FullName);
		resolver.TryResolveType(Arg.Any<string>(), out Arg.Any<Type>())
			.Returns(call =>
			{
				var type = Type.GetType(call.ArgAt<string>(0));
				call[1] = type;
				return type is not null;
			});
		resolver.TryResolveStableTypeId(Arg.Any<string>(), out Arg.Any<Type>())
			.Returns(call =>
			{
				var type = FindTypeByFullName(call.ArgAt<string>(0));
				call[1] = type;
				return type is not null;
			});

		return resolver;
	}

	private static Type? FindTypeByFullName(string fullName)
	{
		foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			var type = assembly.GetType(fullName);
			if (type is not null)
			{
				return type;
			}
		}

		return null;
	}

	[DefaultIcon("flag")]
	public struct IconComponent : IEntityComponent
	{
		public float Value;
	}

	private struct EntityReferenceComponent : IEntityComponent
	{
		public Entity Target;
		public Entity External;
	}

	private static void AssertMatrix(Matrix4x4 actual, Matrix4x4 expected, float tolerance = 0.0001f)
	{
		Assert.That(actual.M11, Is.EqualTo(expected.M11).Within(tolerance));
		Assert.That(actual.M12, Is.EqualTo(expected.M12).Within(tolerance));
		Assert.That(actual.M13, Is.EqualTo(expected.M13).Within(tolerance));
		Assert.That(actual.M21, Is.EqualTo(expected.M21).Within(tolerance));
		Assert.That(actual.M22, Is.EqualTo(expected.M22).Within(tolerance));
		Assert.That(actual.M23, Is.EqualTo(expected.M23).Within(tolerance));
		Assert.That(actual.M31, Is.EqualTo(expected.M31).Within(tolerance));
		Assert.That(actual.M32, Is.EqualTo(expected.M32).Within(tolerance));
		Assert.That(actual.M33, Is.EqualTo(expected.M33).Within(tolerance));
		Assert.That(actual.M41, Is.EqualTo(expected.M41).Within(tolerance));
		Assert.That(actual.M42, Is.EqualTo(expected.M42).Within(tolerance));
		Assert.That(actual.M43, Is.EqualTo(expected.M43).Within(tolerance));
	}
}
