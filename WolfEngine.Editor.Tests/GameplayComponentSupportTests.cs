using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using NSubstitute;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;
using WolfEngine.Importing;
using WolfEngine.Rendering.Passes;
using ImportImageLoader = WolfEngine.Importing.IImageLoader;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class GameplayComponentSupportTests
{
	[Test]
	public void Catalog_MissingGameplayBuild_DoesNotThrowAndIgnoresUnbuiltProjectAssembly()
	{
		using var environment = new GameplayTestEnvironment();

		var componentTypes = environment.TypeCatalog.GetComponentTypes();

		Assert.That(componentTypes.Select(descriptor => descriptor.Type), Does.Contain(typeof(NameComponent)));
		Assert.That(
			componentTypes.Any(descriptor => string.Equals(descriptor.Type.Assembly.GetName().Name, environment.GameplayAssemblyName, StringComparison.Ordinal)),
			Is.False);
	}

	[Test]
	public void GameplayComponent_CanBeDiscoveredAddedEditedSavedAndLoaded()
	{
		using var environment = new GameplayTestEnvironment();
		environment.BuildAndLoadGameplayAssembly(
			"""
			using WolfEngine.ECS;

			namespace GameplayComponentSupport;

			public struct GameplayInspectableComponent : IEntityComponent
			{
				public int Count;
				public string Label;
			}
			""");

		var descriptor = environment.TypeCatalog.GetComponentTypes()
			.Single(candidate => string.Equals(candidate.Type.Name, "GameplayInspectableComponent", StringComparison.Ordinal));
		var scene = environment.Factory.New();
		scene.Name = "Gameplay Scene";
		var entity = scene.World.CreateEntity("Gameplay Entity");

		RuntimeComponentAccessor.AddDefault(scene.World, entity, descriptor.Type);
		var componentValue = RuntimeComponentAccessor.ReadBoxed(scene.World, entity, descriptor.Type);
		var fieldEditor = new TestPropertyDrawerRegistry(new Dictionary<string, object?>
		{
			["Count"] = 17,
			["Label"] = "from-editor"
		});
		Assert.That(RuntimeComponentFieldEditor.ApplyPublicFields(descriptor.Type, fieldEditor, ref componentValue), Is.True);
		RuntimeComponentAccessor.WriteBoxed(scene.World, entity, descriptor.Type, componentValue);

		AssertFieldValue<int>(componentValue, "Count", 17);
		AssertFieldValue<string>(componentValue, "Label", "from-editor");

		environment.Factory.Save(scene);
		var savedComponent = scene.GlobalCell.Entities.Single().Components.Single();
		Assert.That(savedComponent.Type, Is.EqualTo(descriptor.TypeName));
		Assert.That(savedComponent.TypeId, Is.EqualTo(descriptor.StableTypeId));

		var loadedScene = environment.Factory.Load(scene.AssetId);
		var loadedEntity = FindEntityByName(loadedScene.World, "Gameplay Entity");
		var loadedComponent = RuntimeComponentAccessor.ReadBoxed(loadedScene.World, loadedEntity, descriptor.Type);

		AssertFieldValue<int>(loadedComponent, "Count", 17);
		AssertFieldValue<string>(loadedComponent, "Label", "from-editor");
	}

	[Test]
	public void GameplayComponent_AddDefault_AppliesComponentDefaultValues()
	{
		using var environment = new GameplayTestEnvironment();
		environment.BuildAndLoadGameplayAssembly(
			"""
			using WolfEngine.ECS;

			namespace GameplayComponentSupport;

			public struct GameplayDefaultedComponent : IEntityComponent
			{
				public int Count;
				public string Label;

				public void ApplyDefaultValues()
				{
					Count = 42;
					Label = "default-label";
				}
			}
			""");

		var componentType = environment.TypeCatalog.GetComponentTypes()
			.Single(candidate => string.Equals(candidate.Type.Name, "GameplayDefaultedComponent", StringComparison.Ordinal))
			.Type;
		var scene = environment.Factory.New();
		var entity = scene.World.CreateEntity("Defaulted Entity");

		RuntimeComponentAccessor.AddDefault(scene.World, entity, componentType);
		var componentValue = RuntimeComponentAccessor.ReadBoxed(scene.World, entity, componentType);

		AssertFieldValue<int>(componentValue, "Count", 42);
		AssertFieldValue<string>(componentValue, "Label", "default-label");
	}

	[Test]
	public void DataAssetStore_LoadsBuiltInDataAssetTypesThroughResolver()
	{
		using var environment = new GameplayTestEnvironment();
		var assetPath = Path.Combine(environment.ProjectRootPath, "Assets", "Data", $"RenderConfig{DataAssetFile.FileExtension}");
		Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);

		environment.DataAssetStore.SaveAsset(assetPath, typeof(RenderConfig), new RenderConfig());
		var loadResult = environment.DataAssetStore.LoadAsset(assetPath);

		Assert.That(loadResult.DataAssetType, Is.EqualTo(typeof(RenderConfig)));
		Assert.That(loadResult.Asset, Is.TypeOf<RenderConfig>());
	}

	[Test]
	public void GameplayReload_PreservesMatchingFieldsAndDefaultsNewFields()
	{
		using var environment = new GameplayTestEnvironment();
		environment.BuildAndLoadGameplayAssembly(
			"""
			using WolfEngine.ECS;

			namespace GameplayComponentSupport;

			public struct GameplayReloadComponent : IEntityComponent
			{
				public int Count;
			}
			""");

		var componentType = environment.TypeCatalog.GetComponentTypes()
			.Single(candidate => string.Equals(candidate.Type.Name, "GameplayReloadComponent", StringComparison.Ordinal))
			.Type;
		var scene = environment.Factory.New();
		var entity = scene.World.CreateEntity("Reload Entity");
		RuntimeComponentAccessor.AddDefault(scene.World, entity, componentType);
		var componentValue = RuntimeComponentAccessor.ReadBoxed(scene.World, entity, componentType);
		componentType.GetField("Count")!.SetValue(componentValue, 17);
		RuntimeComponentAccessor.WriteBoxed(scene.World, entity, componentType, componentValue);

		var snapshot = environment.SceneReloadService.Capture(scene);

		var buildResult = environment.BuildGameplayAssembly(
			"""
			using WolfEngine.ECS;

			namespace GameplayComponentSupport;

			public struct GameplayReloadComponent : IEntityComponent
			{
				public int Count;
				public string Label;
			}
			""");
		environment.TypeCatalogImpl.ClearCaches();
		RuntimeComponentAccessor.ClearCachedDelegates();
		RuntimeComponentFieldEditor.ClearCachedFields();
		RuntimeAssetDescriptor.ClearCache();
		ProjectTypeResolverUtility.ClearCaches();
		environment.AssetInstanceRegistry.ClearCachedInstances();
		environment.Host.ApplyPreparedBuild(buildResult);

		var restoredScene = environment.SceneReloadService.Restore(snapshot);
		var restoredEntity = FindEntityByName(restoredScene.World, "Reload Entity");
		var reloadedType = environment.TypeCatalog.GetComponentTypes()
			.Single(candidate => string.Equals(candidate.Type.Name, "GameplayReloadComponent", StringComparison.Ordinal))
			.Type;
		var restoredComponent = RuntimeComponentAccessor.ReadBoxed(restoredScene.World, restoredEntity, reloadedType);

		AssertFieldValue<int>(restoredComponent, "Count", 17);
		Assert.That(reloadedType.GetField("Label")!.GetValue(restoredComponent), Is.Null);
	}

	[Test]
	public void GameplayReload_UnloadsPreviousLoadContextAfterDepinning()
	{
		var unloadedContextReference = CreateUnloadedGameplayContextReference();
		ForceCollect(unloadedContextReference);
		Assert.That(unloadedContextReference.IsAlive, Is.False);
	}

	[Test]
	public void SceneLoad_LegacyComponentTypeWithoutTypeId_StillLoads()
	{
		using var environment = new GameplayTestEnvironment();
		environment.BuildAndLoadGameplayAssembly(
			"""
			using WolfEngine.ECS;

			namespace GameplayComponentSupport;

			public struct LegacyGameplayComponent : IEntityComponent
			{
				public int Count;
			}
			""");

		var descriptor = environment.TypeCatalog.GetComponentTypes()
			.Single(candidate => string.Equals(candidate.Type.Name, "LegacyGameplayComponent", StringComparison.Ordinal));
		var scene = environment.Factory.New();
		var entity = scene.World.CreateEntity("Legacy Entity");
		RuntimeComponentAccessor.AddDefault(scene.World, entity, descriptor.Type);
		var component = RuntimeComponentAccessor.ReadBoxed(scene.World, entity, descriptor.Type);
		descriptor.Type.GetField("Count")!.SetValue(component, 7);
		RuntimeComponentAccessor.WriteBoxed(scene.World, entity, descriptor.Type, component);
		environment.Factory.Save(scene);

		var globalCellPath = Path.Combine(environment.ProjectRootPath, scene.GlobalCell.RelativePath.Replace('/', Path.DirectorySeparatorChar));
		var cell = JsonSerializer.Deserialize<Cell>(File.ReadAllText(globalCellPath), AssetJson.SerializerOptions)!;
		cell.Entities.Single().Components.Single().TypeId = string.Empty;
		File.WriteAllText(globalCellPath, JsonSerializer.Serialize(cell, AssetJson.SerializerOptions));

		var loadedScene = environment.Factory.Load(scene.AssetId);
		var loadedEntity = FindEntityByName(loadedScene.World, "Legacy Entity");
		var loadedComponent = RuntimeComponentAccessor.ReadBoxed(loadedScene.World, loadedEntity, descriptor.Type);
		AssertFieldValue<int>(loadedComponent, "Count", 7);
	}

	[Test]
	public void DataAssetStore_LegacyTypeWithoutTypeId_StillLoads()
	{
		using var environment = new GameplayTestEnvironment();
		environment.BuildAndLoadGameplayAssembly(
			"""
			using WolfEngine.AssetPipeline;

			namespace GameplayComponentSupport;

			public class GameplayDataAsset : IDataAsset
			{
				public int Count { get; set; }
			}
			""");

		var descriptor = environment.TypeCatalog.GetDataAssetTypes()
			.Single(candidate => string.Equals(candidate.Type.Name, "GameplayDataAsset", StringComparison.Ordinal));
		var assetPath = Path.Combine(environment.ProjectRootPath, "Assets", "Data", $"GameplayAsset{DataAssetFile.FileExtension}");
		Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
		var asset = (IDataAsset)Activator.CreateInstance(descriptor.Type)!;
		descriptor.Type.GetProperty("Count")!.SetValue(asset, 5);
		environment.DataAssetStore.SaveAsset(assetPath, descriptor.Type, asset);

		var file = JsonSerializer.Deserialize<DataAssetFile>(File.ReadAllText(assetPath), AssetJson.SerializerOptions)!;
		file.DataAssetTypeId = string.Empty;
		File.WriteAllText(assetPath, JsonSerializer.Serialize(file, AssetJson.SerializerOptions));

		var loadResult = environment.DataAssetStore.LoadAsset(assetPath);
		Assert.That(loadResult.DataAssetType, Is.EqualTo(descriptor.Type));
		Assert.That(descriptor.Type.GetProperty("Count")!.GetValue(loadResult.Asset), Is.EqualTo(5));
	}

	private static Entity FindEntityByName(World world, string name)
	{
		var entities = new List<Entity>();
		world.GetAllEntities(entities);
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

	private static void AssertFieldValue<T>(object componentValue, string fieldName, T expectedValue)
	{
		var field = componentValue.GetType().GetField(fieldName)
		           ?? throw new AssertionException($"Field '{fieldName}' was not found on '{componentValue.GetType().FullName}'.");
		Assert.That(field.GetValue(componentValue), Is.EqualTo(expectedValue));
	}

	private static void ForceCollect(WeakReference weakReference)
	{
		for (var attempt = 0; attempt < 10 && weakReference.IsAlive; attempt++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(50);
		}
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static WeakReference CreateUnloadedGameplayContextReference()
	{
		using var environment = new GameplayTestEnvironment();
		environment.BuildAndLoadGameplayAssembly(
			"""
			using WolfEngine.ECS;

			namespace GameplayComponentSupport;

			public struct GameplayUnloadComponent : IEntityComponent
			{
				public int Count;
			}
			""");

		var componentType = environment.TypeCatalog.GetComponentTypes()
			.Single(candidate => string.Equals(candidate.Type.Name, "GameplayUnloadComponent", StringComparison.Ordinal))
			.Type;
		_ = environment.TypeCatalogImpl.GetStableTypeId(componentType);
		var world = new World(WorldTag.Game);
		var entity = world.CreateEntity("Unload Entity");
		RuntimeComponentAccessor.AddDefault(world, entity, componentType);
		var componentValue = RuntimeComponentAccessor.ReadBoxed(world, entity, componentType);
		var fieldEditor = new TestPropertyDrawerRegistry(new Dictionary<string, object?>());
		_ = RuntimeComponentFieldEditor.ApplyPublicFields(componentType, fieldEditor, ref componentValue);

		var buildResult = environment.BuildGameplayAssembly(
			"""
			using WolfEngine.ECS;

			namespace GameplayComponentSupport;

			public struct GameplayUnloadComponent : IEntityComponent
			{
				public int Count;
				public int Extra;
			}
			""");
		environment.TypeCatalogImpl.ClearCaches();
		RuntimeComponentAccessor.ClearCachedDelegates();
		RuntimeComponentFieldEditor.ClearCachedFields();
		RuntimeAssetDescriptor.ClearCache();
		ProjectTypeResolverUtility.ClearCaches();
		environment.AssetInstanceRegistry.ClearCachedInstances();
		var loadResult = environment.Host.ApplyPreparedBuild(buildResult);

		Assert.That(loadResult.UnloadedContextReference, Is.Not.Null);
		return loadResult.UnloadedContextReference!;
	}

	private sealed class GameplayTestEnvironment : IDisposable
	{
		private readonly string _parentDirectory;
		private readonly TestAssetInstanceRegistry _registry;
		private readonly EditorProjectService _projectService;
		private readonly GameplayAssemblyHost _gameplayAssemblyHost;

		public GameplayTestEnvironment()
		{
			_parentDirectory = Path.Combine(Path.GetTempPath(), "WolfEngineGameplayComponentTests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_parentDirectory);

			ProjectName = $"GameplaySupport{Guid.NewGuid():N}";
			GameplayAssemblyName = $"{ProjectName}.Gameplay";

			_registry = new TestAssetInstanceRegistry();
			AssetDatabase.SetInstanceRegistry(_registry);

			_gameplayAssemblyHost = new GameplayAssemblyHost(() => _projectService);
			TypeCatalogImpl = new ProjectTypeCatalog(() => _projectService, _gameplayAssemblyHost);
			DataAssetStore = new DataAssetStore(TypeCatalogImpl);
			var pipelineService = new ProjectAssetPipelineService(
				new AssetPipelineIndex(),
				new AssetMetadataStore(),
				Substitute.For<ImportImageLoader>(),
				DataAssetStore,
				new MaterialAssetStore(),
				Substitute.For<IThreeDFileImporter>());
			_projectService = new EditorProjectService(pipelineService, _registry);
			if (_projectService.CreateProject(_parentDirectory, ProjectName, out var errorMessage) == false)
			{
				throw new AssertionException(errorMessage);
			}

			ProjectRootPath = Path.Combine(_parentDirectory, ProjectName);
			TypeCatalog = TypeCatalogImpl;
			Factory = new EditorSceneFactory(_projectService, pipelineService, TypeCatalogImpl);
			SceneReloadService = new EditorSceneReloadService(TypeCatalogImpl);
		}

		public string ProjectName { get; }
		public string ProjectRootPath { get; }
		public string GameplayAssemblyName { get; }
		public ProjectTypeCatalog TypeCatalogImpl { get; }
		public IProjectTypeCatalog TypeCatalog { get; }
		public DataAssetStore DataAssetStore { get; }
		public IEditorSceneFactory Factory { get; }
		public IEditorSceneReloadService SceneReloadService { get; }
		public GameplayAssemblyHost Host => _gameplayAssemblyHost;
		public TestAssetInstanceRegistry AssetInstanceRegistry => _registry;

		public GameplayBuildResult BuildGameplayAssembly(string source)
		{
			WriteGameplaySource(source);
			var buildResult = WaitForReloadBuild();
			Assert.That(buildResult.Succeeded, Is.True, buildResult.Output);
			return buildResult;
		}

		public void BuildAndLoadGameplayAssembly(string source)
		{
			WriteGameplaySource(source);
			var buildResult = WaitForReloadBuild();
			Assert.That(buildResult.Succeeded, Is.True, buildResult.Output);
			_gameplayAssemblyHost.ApplyPreparedBuild(buildResult);
			Assert.That(TypeCatalogImpl.TryGetDescriptor("missing", out _), Is.False);
		}

		public GameplayBuildResult WaitForReloadBuild()
		{
			Assert.That(_gameplayAssemblyHost.RequestBuildAndReload(), Is.True);
			var timeoutAt = DateTime.UtcNow.AddSeconds(30);
			while (DateTime.UtcNow < timeoutAt)
			{
				if (_gameplayAssemblyHost.TryConsumeBuildResult(out var buildResult))
				{
					return buildResult;
				}

				Thread.Sleep(50);
			}

			throw new AssertionException("Timed out waiting for gameplay build result.");
		}

		public void Dispose()
		{
			_projectService.CloseProject();
			AssetDatabase.ClearInstanceRegistry();
			if (Directory.Exists(_parentDirectory))
			{
				Directory.Delete(_parentDirectory, recursive: true);
			}
		}

		private void RewriteGameplayProjectReferences()
		{
			var gameplayProjectPath = _projectService.GameplayProjectPath
			                         ?? throw new AssertionException("Gameplay project path was not set.");
			var repoRoot = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));
			var engineProjectPath = Path.Combine(repoRoot, "WolfEngine", "WolfEngine.csproj");
			var ecsProjectPath = Path.Combine(repoRoot, "WolfEngine.ECS", "WolfEngine.ECS.csproj");
			var physicsProjectPath = Path.Combine(repoRoot, "WolfEngine.Physics", "WolfEngine.Physics.csproj");
			var projectContents = File.ReadAllText(gameplayProjectPath);
			projectContents = projectContents.Replace("../../WolfEngine/WolfEngine/WolfEngine.csproj", engineProjectPath, StringComparison.Ordinal);
			projectContents = projectContents.Replace("../../WolfEngine/WolfEngine.ECS/WolfEngine.ECS.csproj", ecsProjectPath, StringComparison.Ordinal);
			projectContents = projectContents.Replace("../../WolfEngine/WolfEngine.Physics/WolfEngine.Physics.csproj", physicsProjectPath, StringComparison.Ordinal);
			File.WriteAllText(gameplayProjectPath, projectContents);
		}

		private void WriteGameplaySource(string source)
		{
			var gameplaySourcePath = Path.Combine(ProjectRootPath, ProjectGameplayScaffolder.GameplayFolderName, ProjectGameplayScaffolder.GameplaySourceFileName);
			File.WriteAllText(gameplaySourcePath, source);
			RewriteGameplayProjectReferences();
		}
	}

	private sealed class TestPropertyDrawerRegistry : IPropertyDrawerRegistry
	{
		private readonly IReadOnlyDictionary<string, object?> _valuesByLabel;

		public TestPropertyDrawerRegistry(IReadOnlyDictionary<string, object?> valuesByLabel)
		{
			_valuesByLabel = valuesByLabel;
		}

		public PropertyDrawerResult Draw(PropertyDrawerContext context)
		{
			if (_valuesByLabel.TryGetValue(context.Label, out var value) == false)
			{
				return new PropertyDrawerResult(false, false, context.Value);
			}

			return new PropertyDrawerResult(true, true, value);
		}
	}

	private sealed class TestAssetInstanceRegistry : IAssetInstanceRegistry
	{
		private readonly Dictionary<Guid, object> _instances = new();

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
