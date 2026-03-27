using System.Numerics;
using NSubstitute;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor;
using WolfEngine.Editor.Projects;
using WolfEngine.Importing;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.ECS.Tests;

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
			FarPlane = 500.0f,
			AutoResolution = false
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
		Assert.That(loadedCamera.AutoResolution, Is.False);

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

	private sealed class TestEnvironment : IDisposable
	{
		public TestEnvironment()
		{
			ParentDirectory = Path.Combine(Path.GetTempPath(), "WolfEngineSceneTests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(ParentDirectory);

			Registry = new TestAssetInstanceRegistry();
			AssetDatabase.SetInstanceRegistry(Registry);

			var pipelineService = new ProjectAssetPipelineService(
				new AssetPipelineIndex(),
				new AssetMetadataStore(),
				Substitute.For<IImageLoader>(),
				new DataAssetStore(),
				new MaterialAssetStore(),
				Substitute.For<IThreeDFileImporter>());
			PipelineService = pipelineService;
			ProjectService = new EditorProjectService(pipelineService, Registry);
			if (ProjectService.CreateProject(ParentDirectory, "Project", out var errorMessage) == false)
			{
				throw new AssertionException(errorMessage);
			}

			Factory = new EditorSceneFactory(ProjectService, PipelineService);
		}

		public string ParentDirectory { get; }
		public TestAssetInstanceRegistry Registry { get; }
		public IProjectAssetPipelineService PipelineService { get; }
		public IEditorProjectService ProjectService { get; }
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
	}
}
