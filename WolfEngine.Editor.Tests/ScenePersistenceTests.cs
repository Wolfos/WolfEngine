using System.Numerics;
using System.Text.Json;
using NSubstitute;
using EditorUI = WolfEngine.Editor.UI;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Importing;
using WolfEngine.Mathematics;
using WolfEngine.Physics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class ScenePersistenceTests
{
	[Test]
	public void SaveAndLoad_EmptyScene_RoundTripsSceneAssetAndGlobalCell()
	{
		using var environment = new TestEnvironment();
		var scene = environment.Factory.New();
		scene.Name = "Empty Scene";

		environment.Factory.Save(scene);

		Assert.That(scene.AssetId, Is.Not.EqualTo(Guid.Empty));
		Assert.That(environment.ProjectService.TryGetAsset(scene.AssetId, out var savedAsset), Is.True);
		Assert.That(savedAsset.Type, Is.EqualTo(AssetType.Scene));

		var loadedScene = environment.Factory.Load(scene.AssetId);

		Assert.That(loadedScene.Name, Is.EqualTo("Empty Scene"));
		Assert.That(loadedScene.GlobalCell.RelativePath, Is.EqualTo("Assets/Scenes/Empty Scene/global.cell.json"));
		Assert.That(loadedScene.SpatialCells, Is.Empty);
		Assert.That(GetAllEntities(loadedScene.World), Is.Empty);
	}

	[Test]
	public void SaveAndLoad_Hierarchy_PreservesMetadataAndSkipsEditorOnlyComponents()
	{
		using var environment = new TestEnvironment();
		var scene = environment.Factory.New();
		scene.Name = "Hierarchy Scene";

		var parent = scene.World.CreateEntity("Parent");
		scene.World.AddTransform(parent, Matrix4x4.Identity);
		var child = scene.World.CreateEntity("Child");
		scene.World.AddTransform(child, Matrix4x4.CreateTranslation(new Vector3(1.0f, 2.0f, 3.0f)));
		scene.World.SetParent(child, parent);
		scene.World.SetEnabled(child, false);
		scene.EntityIcons[child] = "light";
		scene.World.AddComponent(child, new CameraMover { MoveSpeed = 7.0f, LookSensitivity = 0.01f });

		environment.Factory.Save(scene);
		var loadedScene = environment.Factory.Load(scene.AssetId);
		var loadedParent = FindEntityByName(loadedScene.World, "Parent");
		var loadedChild = FindEntityByName(loadedScene.World, "Child");

		Assert.That(loadedScene.World.HasComponent<Parent>(loadedChild), Is.True);
		Assert.That(loadedScene.World.GetComponent<Parent>(loadedChild).Value, Is.EqualTo(loadedParent));
		Assert.That(loadedScene.World.IsEnabled(loadedChild), Is.False);
		Assert.That(loadedScene.EntityIcons[loadedChild], Is.EqualTo("light"));
		Assert.That(loadedScene.World.HasComponent<CameraMover>(loadedChild), Is.False);
		Assert.That(loadedScene.World.HasComponent<WorldTransform>(loadedParent), Is.True);
		Assert.That(loadedScene.World.HasComponent<WorldTransform>(loadedChild), Is.True);
	}

	[Test]
	public void SaveAndLoad_MeshRenderer_HydratesMeshAndMaterialAssets()
	{
		using var environment = new TestEnvironment();
		var meshId = Guid.NewGuid();
		var materialId = Guid.NewGuid();
		var mesh = CreateTriangleMesh();
		var material = new Material("test-shader.slang");
		environment.Registry.Register(meshId, mesh);
		environment.Registry.Register(materialId, material);

		var scene = environment.Factory.New();
		scene.Name = "Mesh Scene";
		var entity = scene.World.CreateEntity("Mesh Entity");
		scene.World.AddTransform(entity, Matrix4x4.Identity);
		scene.World.AddComponent(entity, new MeshRenderer
		{
			MeshAsset = new AssetRef<Mesh> { NodeId = meshId },
			MaterialAsset = new AssetRef<Material> { NodeId = materialId },
			Mesh = mesh,
			Material = material
		});

		environment.Factory.Save(scene);
		var loadedScene = environment.Factory.Load(scene.AssetId);
		var loadedEntity = FindEntityByName(loadedScene.World, "Mesh Entity");
		var loadedRenderer = loadedScene.World.GetComponent<MeshRenderer>(loadedEntity);

		Assert.That(loadedRenderer.MeshAsset.NodeId, Is.EqualTo(meshId));
		Assert.That(loadedRenderer.MaterialAsset.NodeId, Is.EqualTo(materialId));
		Assert.That(loadedRenderer.Mesh, Is.SameAs(mesh));
		Assert.That(loadedRenderer.Material, Is.SameAs(material));
	}

	[Test]
	public void RuntimeComponentAccessor_AddDefault_MeshColliderCopiesMeshRendererMesh()
	{
		using var environment = new TestEnvironment();
		var meshId = Guid.NewGuid();
		var mesh = CreateTriangleMesh();
		environment.Registry.Register(meshId, mesh);

		var scene = environment.Factory.New();
		var entity = scene.World.CreateEntity("Mesh Entity");
		scene.World.AddComponent(entity, new MeshRenderer
		{
			MeshAsset = new AssetRef<Mesh> { NodeId = meshId },
			Mesh = mesh
		});

		EditorUI.RuntimeComponentAccessor.AddDefault(scene.World, entity, typeof(MeshCollider));

		var collider = scene.World.GetComponent<MeshCollider>(entity);
		Assert.That(collider.MeshAsset.NodeId, Is.EqualTo(meshId));
		Assert.That(collider.Mesh, Is.SameAs(mesh));
	}

	[Test]
	public void RuntimeComponentAccessor_AddDefault_MeshColliderWithoutMeshRendererLeavesMeshUnset()
	{
		using var environment = new TestEnvironment();
		var scene = environment.Factory.New();
		var entity = scene.World.CreateEntity("Plain Entity");

		EditorUI.RuntimeComponentAccessor.AddDefault(scene.World, entity, typeof(MeshCollider));

		var collider = scene.World.GetComponent<MeshCollider>(entity);
		Assert.That(collider.MeshAsset.NodeId, Is.EqualTo(Guid.Empty));
		Assert.That(collider.Mesh, Is.Null);
	}

	[Test]
	public void SaveAndLoad_MeshCollider_HydratesMeshAsset()
	{
		using var environment = new TestEnvironment();
		var meshId = Guid.NewGuid();
		var mesh = CreateTriangleMesh();
		environment.Registry.Register(meshId, mesh);

		var scene = environment.Factory.New();
		scene.Name = "Mesh Collider Scene";
		var entity = scene.World.CreateEntity("Mesh Collider Entity");
		scene.World.AddComponent(entity, new MeshCollider
		{
			MeshAsset = new AssetRef<Mesh> { NodeId = meshId },
			Mesh = mesh
		});

		environment.Factory.Save(scene);
		var loadedScene = environment.Factory.Load(scene.AssetId);
		var loadedEntity = FindEntityByName(loadedScene.World, "Mesh Collider Entity");
		var loadedCollider = loadedScene.World.GetComponent<MeshCollider>(loadedEntity);

		Assert.That(loadedCollider.MeshAsset.NodeId, Is.EqualTo(meshId));
		Assert.That(loadedCollider.Mesh, Is.SameAs(mesh));
	}

	[Test]
	public void SaveAndLoad_TerrainComponent_RoundTripsAssetRefsAndSettings()
	{
		using var environment = new TestEnvironment();
		var scene = environment.Factory.New();
		scene.Name = "Terrain Scene";
		var entity = scene.World.CreateEntity("Terrain");
		scene.World.AddTransform(entity, Matrix4x4.CreateTranslation(10.0f, 2.0f, -4.0f));

		var terrainAssetId = Guid.NewGuid();
		var layerSetId = Guid.NewGuid();
		scene.World.AddComponent(entity, new TerrainComponent
		{
			TerrainAsset = new AssetRef<TerrainAsset> { NodeId = terrainAssetId },
			LayerSetAsset = new AssetRef<TerrainLayerSet> { NodeId = layerSetId },
			WorldSizeMeters = new Vector2(1024.0f, 768.0f),
			HeightScaleMeters = 96.0f,
			ChunkSizeMeters = 96.0f,
			LodCount = 4,
			Lod0ResolutionInQuads = 32,
			RayTracingResolutionInQuads = 24,
			LodDistancesMeters = [100.0f, 240.0f, 520.0f]
		});

		environment.Factory.Save(scene);
		var loadedScene = environment.Factory.Load(scene.AssetId);
		var loadedEntity = FindEntityByName(loadedScene.World, "Terrain");
		var loadedTerrain = loadedScene.World.GetComponent<TerrainComponent>(loadedEntity);

		Assert.That(loadedTerrain.TerrainAsset.NodeId, Is.EqualTo(terrainAssetId));
		Assert.That(loadedTerrain.LayerSetAsset.NodeId, Is.EqualTo(layerSetId));
		Assert.That(loadedTerrain.WorldSizeMeters, Is.EqualTo(new Vector2(1024.0f, 768.0f)));
		Assert.That(loadedTerrain.HeightScaleMeters, Is.EqualTo(96.0f));
		Assert.That(loadedTerrain.ChunkSizeMeters, Is.EqualTo(96.0f));
		Assert.That(loadedTerrain.LodCount, Is.EqualTo(4));
		Assert.That(loadedTerrain.Lod0ResolutionInQuads, Is.EqualTo(32));
		Assert.That(loadedTerrain.RayTracingResolutionInQuads, Is.EqualTo(24));
		Assert.That(loadedTerrain.LodDistancesMeters, Is.EqualTo(new[] { 100.0f, 240.0f, 520.0f }));
	}

	[Test]
	public void SaveAndLoad_CustomComponentFromTestAssembly_RoundTripsWithoutFactoryRegistration()
	{
		using var environment = new TestEnvironment();
		var scene = environment.Factory.New();
		scene.Name = "Custom Component Scene";
		var entity = scene.World.CreateEntity("Custom Entity");
		scene.World.AddComponent(entity, new TestSceneComponent
		{
			Count = 42,
			Label = "generic"
		});

		environment.Factory.Save(scene);
		var loadedScene = environment.Factory.Load(scene.AssetId);
		var loadedEntity = FindEntityByName(loadedScene.World, "Custom Entity");
		var loadedComponent = loadedScene.World.GetComponent<TestSceneComponent>(loadedEntity);

		Assert.That(loadedComponent.Count, Is.EqualTo(42));
		Assert.That(loadedComponent.Label, Is.EqualTo("generic"));
	}

	[Test]
	public void SaveAndLoad_ComponentEntityReference_RoundTripsAcrossCells()
	{
		using var environment = new TestEnvironment();
		var scene = environment.Factory.New();
		scene.Name = "Entity Reference Scene";
		var cellCoordinates = new Int2(2, -1);
		scene.SpatialCells[cellCoordinates] = new Cell();

		var target = scene.World.CreateEntity("Target");
		scene.World.AddTransform(target, Matrix4x4.Identity);
		scene.EntityCellKeys[target] = SceneCellKey.Spatial(cellCoordinates);

		var source = scene.World.CreateEntity("Source");
		scene.World.AddComponent(source, new EntityReferenceComponent
		{
			Target = target
		});

		environment.Factory.Save(scene);
		var loadedScene = environment.Factory.Load(scene.AssetId);
		var loadedSource = FindEntityByName(loadedScene.World, "Source");
		var loadedTarget = FindEntityByName(loadedScene.World, "Target");
		var loadedComponent = loadedScene.World.GetComponent<EntityReferenceComponent>(loadedSource);

		Assert.That(loadedComponent.Target, Is.EqualTo(loadedTarget));
	}

	[Test]
	public void SaveAndLoad_ComponentEntityReference_AutoClearsWhenTargetMissing()
	{
		using var environment = new TestEnvironment();
		var scene = environment.Factory.New();
		scene.Name = "Broken Entity Reference Scene";
		var target = scene.World.CreateEntity("Target");
		var source = scene.World.CreateEntity("Source");
		scene.World.AddComponent(source, new EntityReferenceComponent
		{
			Target = target
		});

		environment.Factory.Save(scene);

		var globalCellPath = Path.Combine(environment.ProjectService.ProjectRootPath!, scene.GlobalCell.RelativePath.Replace('/', Path.DirectorySeparatorChar));
		var cell = JsonSerializer.Deserialize<Cell>(File.ReadAllText(globalCellPath), AssetJson.SerializerOptions)!;
		cell.Entities.RemoveAll(candidate => string.Equals(candidate.Name, "Target", StringComparison.Ordinal));
		File.WriteAllText(globalCellPath, JsonSerializer.Serialize(cell, AssetJson.SerializerOptions));

		var loadedScene = environment.Factory.Load(scene.AssetId);
		var loadedSource = FindEntityByName(loadedScene.World, "Source");
		var loadedComponent = loadedScene.World.GetComponent<EntityReferenceComponent>(loadedSource);

		Assert.That(loadedComponent.Target, Is.EqualTo(default(Entity)));
	}

	[Test]
	public void SaveAndLoad_CameraLightAndWorldSettings_RoundTripCorrectly()
	{
		using var environment = new TestEnvironment();
		var renderConfigId = Guid.NewGuid();
		var renderConfig = new RenderConfig();
		environment.Registry.Register(renderConfigId, renderConfig);

		var scene = environment.Factory.New();
		scene.Name = "Component Scene";

		var cameraEntity = scene.World.CreateEntity("Camera");
		scene.World.AddTransform(cameraEntity, Matrix4x4.Identity);
		var camera = new Camera
		{
			ScreenResolution = new Int2(1280, 720),
			NearPlane = 0.5f,
			FarPlane = 500.0f
		};
		camera.SetPerspective(75.0f);
		scene.World.AddComponent(cameraEntity, camera);

		var lightEntity = scene.World.CreateEntity("Light");
		scene.World.AddComponent(lightEntity, new Light
		{
			Type = LightType.Point,
			Intensity = 3.5f,
			Range = 12.0f,
			Color = ColorRGBA.FromVector4(new Vector4(0.4f, 0.5f, 0.6f, 1.0f)),
			HorizonFade = false
		});

		var settingsEntity = scene.World.CreateEntity("Settings");
		scene.World.AddComponent(settingsEntity, new WorldSettings
		{
			RenderConfigAsset = new AssetRef<RenderConfig> { NodeId = renderConfigId }
		});

		environment.Factory.Save(scene);
		var loadedScene = environment.Factory.Load(scene.AssetId);

		var loadedCamera = loadedScene.World.GetComponent<Camera>(FindEntityByName(loadedScene.World, "Camera"));
		var loadedLight = loadedScene.World.GetComponent<Light>(FindEntityByName(loadedScene.World, "Light"));
		var loadedSettings = loadedScene.World.GetComponent<WorldSettings>(FindEntityByName(loadedScene.World, "Settings"));

		Assert.That(loadedCamera.ScreenResolution, Is.EqualTo(new Int2(1280, 720)));
		Assert.That(loadedCamera.Fov, Is.EqualTo(75.0f).Within(0.001f));
		Assert.That(loadedCamera.NearPlane, Is.EqualTo(0.5f));
		Assert.That(loadedCamera.FarPlane, Is.EqualTo(500.0f));

		Assert.That(loadedLight.Type, Is.EqualTo(LightType.Point));
		Assert.That(loadedLight.Intensity, Is.EqualTo(3.5f));
		Assert.That(loadedLight.Range, Is.EqualTo(12.0f));
		Assert.That(loadedLight.Color, Is.EqualTo(ColorRGBA.FromVector4(new Vector4(0.4f, 0.5f, 0.6f, 1.0f))));

		Assert.That(loadedSettings.RenderConfigAsset.NodeId, Is.EqualTo(renderConfigId));
		Assert.That(loadedSettings.RenderConfigAsset.Asset, Is.SameAs(renderConfig));
	}

	[Test]
	public void Load_SceneWithSpatialCells_LoadsAllCellsIntoWorldImmediately()
	{
		using var environment = new TestEnvironment();
		var scene = environment.Factory.New();
		scene.Name = "Spatial Scene";
		var spatialCoordinates = new Int2(4, -2);
		scene.SpatialCells[spatialCoordinates] = new Cell();

		var globalParent = scene.World.CreateEntity("Global Parent");
		scene.World.AddTransform(globalParent, Matrix4x4.Identity);

		var spatialChild = scene.World.CreateEntity("Spatial Child");
		scene.World.AddTransform(spatialChild, Matrix4x4.CreateTranslation(new Vector3(5.0f, 0.0f, 0.0f)));
		scene.World.SetParent(spatialChild, globalParent);
		scene.EntityCellKeys[spatialChild] = SceneCellKey.Spatial(spatialCoordinates);

		environment.Factory.Save(scene);
		var loadedScene = environment.Factory.Load(scene.AssetId);
		var loadedSpatialChild = FindEntityByName(loadedScene.World, "Spatial Child");

		Assert.That(loadedScene.SpatialCells.ContainsKey(spatialCoordinates), Is.True);
		Assert.That(loadedScene.EntityCellKeys[loadedSpatialChild], Is.EqualTo(SceneCellKey.Spatial(spatialCoordinates)));
		Assert.That(GetAllEntities(loadedScene.World), Has.Count.EqualTo(2));
		Assert.That(loadedScene.World.HasComponent<Parent>(loadedSpatialChild), Is.True);
	}

	[Test]
	public void Save_PersistsTransformOnEntityAndSkipsTransientTransformComponents()
	{
		using var environment = new TestEnvironment();
		var scene = environment.Factory.New();
		scene.Name = "Transform Persistence Scene";

		var entity = scene.World.CreateEntity("Transform Entity");
		var transform = Matrix4x4.CreateScale(new Vector3(2.0f, 3.0f, 4.0f))
		                * Matrix4x4.CreateFromYawPitchRoll(0.2f, 0.3f, 0.4f)
		                * Matrix4x4.CreateTranslation(new Vector3(5.0f, 6.0f, 7.0f));
		scene.World.AddTransform(entity, transform);

		environment.Factory.Save(scene);

		var savedEntity = scene.GlobalCell.Entities.Single();
		Assert.That(savedEntity.LocalTransform.HasValue, Is.True);
		AssertMatrix(savedEntity.LocalTransform!.Value, transform);
		Assert.That(savedEntity.Components.Select(component => Type.GetType(component.Type, throwOnError: false)), Does.Not.Contain(typeof(LocalTransform)));
		Assert.That(savedEntity.Components.Select(component => Type.GetType(component.Type, throwOnError: false)), Does.Not.Contain(typeof(WorldTransform)));
		Assert.That(savedEntity.Components.Select(component => Type.GetType(component.Type, throwOnError: false)), Does.Not.Contain(typeof(DirtyTransformRoot)));
	}

	[Test]
	public void SaveAndLoad_UnnamedEntityWithTransform_PreservesMissingNameComponent()
	{
		using var environment = new TestEnvironment();
		var scene = environment.Factory.New();
		scene.Name = "Unnamed Transform Scene";

		var entity = scene.World.CreateEntity();
		scene.World.AddTransform(entity, Matrix4x4.CreateTranslation(new Vector3(8.0f, 9.0f, 10.0f)));

		environment.Factory.Save(scene);
		var loadedScene = environment.Factory.Load(scene.AssetId);
		var loadedEntity = GetAllEntities(loadedScene.World).Single();
		var localTransform = loadedScene.World.GetComponent<LocalTransform>(loadedEntity);

		Assert.That(loadedScene.World.HasComponent<NameComponent>(loadedEntity), Is.False);
		Assert.That(loadedScene.World.HasComponent<WorldTransform>(loadedEntity), Is.True);
		Assert.That(localTransform.LocalPosition, Is.EqualTo(new Vector3(8.0f, 9.0f, 10.0f)));
	}

	[Test]
	public void SaveEntityAsPrefab_InstantiateAndSaveScene_PersistsOverridesAndRefreshesSourceDefaults()
	{
		using var environment = new TestEnvironment();
		var prefabAuthoringScene = environment.Factory.New();
		prefabAuthoringScene.Name = "Prefab Authoring";

		var sourceRoot = prefabAuthoringScene.World.CreateEntity("Source Root");
		prefabAuthoringScene.World.AddTransform(sourceRoot, Matrix4x4.CreateTranslation(new Vector3(1.0f, 2.0f, 3.0f)));
		var sourceChild = prefabAuthoringScene.World.CreateEntity("Source Child");
		prefabAuthoringScene.World.AddTransform(sourceChild, Matrix4x4.CreateTranslation(new Vector3(4.0f, 5.0f, 6.0f)));
		prefabAuthoringScene.World.SetParent(sourceChild, sourceRoot);
		prefabAuthoringScene.World.AddComponent(sourceChild, new Light
		{
			Type = LightType.Point,
			Intensity = 2.0f,
			Range = 8.0f,
			Color = ColorRGBA.White,
			HorizonFade = true
		});

		var prefabCreationResult = environment.PrefabCreator.SaveEntityAsPrefab(prefabAuthoringScene, sourceRoot, "Assets/Prefabs");

		Assert.That(prefabCreationResult.Success, Is.True, prefabCreationResult.ErrorMessage);
		Assert.That(prefabCreationResult.AssetId.HasValue, Is.True);

		var scene = environment.Factory.New();
		scene.Name = "Prefab Instance Scene";
		environment.PipelineService.InstantiatePrefab(environment.ProjectService.ProjectRootPath!, prefabCreationResult.AssetId!.Value, scene);

		var instanceRoot = FindEntityByName(scene.World, "Source Root");
		var instanceChild = FindEntityByName(scene.World, "Source Child");
		Assert.That(scene.EntityPrefabSourcePaths.ContainsKey(instanceRoot), Is.True);
		Assert.That(scene.EntityPrefabSourcePaths.ContainsKey(instanceChild), Is.True);

		scene.World.GetComponent<NameComponent>(instanceRoot).Name = "Scene Override Root";
		scene.World.SetLocalPosition(instanceChild, new Vector3(9.0f, 8.0f, 7.0f));

		environment.Factory.Save(scene);

		Assert.That(environment.ProjectService.TryGetAsset(prefabCreationResult.AssetId.Value, out var prefabAsset), Is.True);
		var prefabAbsolutePath = environment.ProjectService.GetAbsolutePath(prefabAsset.RelativeAssetPath);
		var prefabFile = PrefabAssetFile.Load(prefabAbsolutePath);
		var prefabRoot = prefabFile.Entities.Single(entity => entity.EntityId == prefabFile.RootEntityId);
		prefabRoot.Name = "Source Root Updated";
		prefabRoot.LocalTransform = Matrix4x4.CreateTranslation(new Vector3(21.0f, 22.0f, 23.0f));
		var prefabChild = prefabFile.Entities.Single(entity => entity.ParentEntityId == prefabFile.RootEntityId);
		prefabChild.LocalTransform = Matrix4x4.CreateTranslation(new Vector3(11.0f, 12.0f, 13.0f));
		File.WriteAllText(prefabAbsolutePath, System.Text.Json.JsonSerializer.Serialize(prefabFile, AssetJson.SerializerOptions));
		environment.ProjectService.RefreshAssetSource(prefabAsset.RelativeAssetPath);

		var loadedScene = environment.Factory.Load(scene.AssetId);
		var loadedRoot = FindEntityByName(loadedScene.World, "Scene Override Root");
		var loadedChild = FindEntityByName(loadedScene.World, "Source Child");

		Assert.That(loadedScene.EntityPrefabSourcePaths.ContainsKey(loadedRoot), Is.True);
		Assert.That(loadedScene.World.GetComponent<NameComponent>(loadedRoot).Name, Is.EqualTo("Scene Override Root"));
		Assert.That(loadedScene.World.GetComponent<LocalTransform>(loadedRoot).LocalPosition, Is.EqualTo(new Vector3(21.0f, 22.0f, 23.0f)));
		Assert.That(loadedScene.World.GetComponent<LocalTransform>(loadedChild).LocalPosition, Is.EqualTo(new Vector3(9.0f, 8.0f, 7.0f)));
	}

	[Test]
	public void SaveEntityAsPrefab_InstantiateScene_PreservesEntityReferenceWithinPrefab()
	{
		using var environment = new TestEnvironment();
		var prefabAuthoringScene = environment.Factory.New();
		prefabAuthoringScene.Name = "Prefab Entity Ref";

		var root = prefabAuthoringScene.World.CreateEntity("Root");
		var child = prefabAuthoringScene.World.CreateEntity("Child");
		prefabAuthoringScene.World.SetParent(child, root);
		prefabAuthoringScene.World.AddComponent(root, new EntityReferenceComponent
		{
			Target = child
		});

		var prefabCreationResult = environment.PrefabCreator.SaveEntityAsPrefab(prefabAuthoringScene, root, "Assets/Prefabs");
		Assert.That(prefabCreationResult.Success, Is.True, prefabCreationResult.ErrorMessage);

		var scene = environment.Factory.New();
		environment.PipelineService.InstantiatePrefab(environment.ProjectService.ProjectRootPath!, prefabCreationResult.AssetId!.Value, scene);

		var instanceRoot = FindEntityByName(scene.World, "Root");
		var instanceChild = FindEntityByName(scene.World, "Child");
		var component = scene.World.GetComponent<EntityReferenceComponent>(instanceRoot);

		Assert.That(component.Target, Is.EqualTo(instanceChild));
	}

	private static List<Entity> GetAllEntities(World world)
	{
		var entities = new List<Entity>();
		world.GetAllEntities(entities);
		return entities;
	}

	private static Entity FindEntityByName(World world, string name)
	{
		var entities = GetAllEntities(world);
		for (var i = 0; i < entities.Count; i++)
		{
			var entity = entities[i];
			if (world.HasComponent<NameComponent>(entity) == false)
			{
				continue;
			}

			if (string.Equals(world.GetComponent<NameComponent>(entity).Name, name, StringComparison.Ordinal))
			{
				return entity;
			}
		}

		throw new AssertionException($"Entity '{name}' was not found.");
	}

	private static Mesh CreateTriangleMesh()
	{
		return new Mesh(
			[
				new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
				new Vector4(1.0f, 0.0f, 0.0f, 1.0f),
				new Vector4(0.0f, 1.0f, 0.0f, 1.0f)
			],
			[0u, 1u, 2u]);
	}

	private static void AssertMatrix(Matrix4x4 actual, Matrix4x4 expected, float tolerance = 0.0001f)
	{
		Assert.That(actual.M11, Is.EqualTo(expected.M11).Within(tolerance));
		Assert.That(actual.M12, Is.EqualTo(expected.M12).Within(tolerance));
		Assert.That(actual.M13, Is.EqualTo(expected.M13).Within(tolerance));
		Assert.That(actual.M14, Is.EqualTo(expected.M14).Within(tolerance));
		Assert.That(actual.M21, Is.EqualTo(expected.M21).Within(tolerance));
		Assert.That(actual.M22, Is.EqualTo(expected.M22).Within(tolerance));
		Assert.That(actual.M23, Is.EqualTo(expected.M23).Within(tolerance));
		Assert.That(actual.M24, Is.EqualTo(expected.M24).Within(tolerance));
		Assert.That(actual.M31, Is.EqualTo(expected.M31).Within(tolerance));
		Assert.That(actual.M32, Is.EqualTo(expected.M32).Within(tolerance));
		Assert.That(actual.M33, Is.EqualTo(expected.M33).Within(tolerance));
		Assert.That(actual.M34, Is.EqualTo(expected.M34).Within(tolerance));
		Assert.That(actual.M41, Is.EqualTo(expected.M41).Within(tolerance));
		Assert.That(actual.M42, Is.EqualTo(expected.M42).Within(tolerance));
		Assert.That(actual.M43, Is.EqualTo(expected.M43).Within(tolerance));
		Assert.That(actual.M44, Is.EqualTo(expected.M44).Within(tolerance));
	}

	private struct TestSceneComponent : IEntityComponent
	{
		public int Count;
		public string Label;
	}

	private struct EntityReferenceComponent : IEntityComponent
	{
		public Entity Target;
	}

	private sealed class TestEnvironment : IDisposable
	{
		public TestEnvironment()
		{
			ParentDirectory = Path.Combine(Path.GetTempPath(), "WolfEngineSceneTests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(ParentDirectory);

			Registry = new TestAssetInstanceRegistry();
			AssetDatabase.SetInstanceRegistry(Registry);

			var metadataStore = new AssetMetadataStore();
			var pipelineService = new ProjectAssetPipelineService(
				new AssetPipelineIndex(),
				metadataStore,
				Substitute.For<IImageLoader>(),
				new DataAssetStore(),
				new MaterialAssetStore(),
				Substitute.For<IThreeDFileImporter>());
			PipelineService = pipelineService;
			ProjectService = new EditorProjectService(pipelineService, Registry);
			TypeResolver = new ProjectTypeCatalog(() => ProjectService);
			PrefabCreator = new PrefabAssetCreator(
				ProjectService,
				metadataStore,
				PipelineService,
				new EditorSceneSnapshotService(TypeResolver),
				TypeResolver);
			if (ProjectService.CreateProject(ParentDirectory, "Project", out var errorMessage) == false)
			{
				throw new AssertionException(errorMessage);
			}

			Factory = new EditorSceneFactory(ProjectService, PipelineService, TypeResolver);
		}

		public string ParentDirectory { get; }
		public TestAssetInstanceRegistry Registry { get; }
		public IProjectAssetPipelineService PipelineService { get; }
		public IEditorProjectService ProjectService { get; }
		public IProjectTypeResolver TypeResolver { get; }
		public IPrefabAssetCreator PrefabCreator { get; }
		public IEditorSceneFactory Factory { get; }

		public void Dispose()
		{
			ProjectService.CloseProject();
			AssetDatabase.ClearInstanceRegistry();
			if (Directory.Exists(ParentDirectory))
			{
				Directory.Delete(ParentDirectory, recursive: true);
			}
		}
	}

	private sealed class TestAssetInstanceRegistry : IAssetInstanceRegistry
	{
		private readonly Dictionary<Guid, object> _instances = new();

		public void Register(Guid assetId, object instance)
		{
			_instances[assetId] = instance;
		}

		public object? GetInstance(Guid assetId, Type expectedType)
		{
			if (_instances.TryGetValue(assetId, out var instance) == false)
			{
				return null;
			}

			return expectedType.IsInstanceOfType(instance) ? instance : null;
		}

		public void RefreshProject(string projectRootPath, AssetDatabase database)
		{
		}

		public void InvalidateAssets(IEnumerable<Guid> assetIds)
		{
			foreach (var assetId in assetIds)
			{
				_instances.Remove(assetId);
			}
		}

		public void Clear()
		{
			_instances.Clear();
		}

		public void ClearCachedInstances()
		{
			_instances.Clear();
		}
	}
}
