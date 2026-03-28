using System.Diagnostics;
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
		environment.BuildGameplayAssembly(
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

		var loadedScene = environment.Factory.Load(scene.AssetId);
		var loadedEntity = FindEntityByName(loadedScene.World, "Gameplay Entity");
		var loadedComponent = RuntimeComponentAccessor.ReadBoxed(loadedScene.World, loadedEntity, descriptor.Type);

		AssertFieldValue<int>(loadedComponent, "Count", 17);
		AssertFieldValue<string>(loadedComponent, "Label", "from-editor");
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

	private sealed class GameplayTestEnvironment : IDisposable
	{
		private readonly string _parentDirectory;
		private readonly TestAssetInstanceRegistry _registry;
		private readonly EditorProjectService _projectService;

		public GameplayTestEnvironment()
		{
			_parentDirectory = Path.Combine(Path.GetTempPath(), "WolfEngineGameplayComponentTests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_parentDirectory);

			ProjectName = $"GameplaySupport{Guid.NewGuid():N}";
			GameplayAssemblyName = $"{ProjectName}.Gameplay";

			_registry = new TestAssetInstanceRegistry();
			AssetDatabase.SetInstanceRegistry(_registry);

			TypeCatalogImpl = new ProjectTypeCatalog(() => _projectService);
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
		}

		public string ProjectName { get; }
		public string ProjectRootPath { get; }
		public string GameplayAssemblyName { get; }
		public ProjectTypeCatalog TypeCatalogImpl { get; }
		public IProjectTypeCatalog TypeCatalog { get; }
		public DataAssetStore DataAssetStore { get; }
		public IEditorSceneFactory Factory { get; }

		public void BuildGameplayAssembly(string source)
		{
			var gameplaySourcePath = Path.Combine(ProjectRootPath, ProjectGameplayScaffolder.GameplayFolderName, ProjectGameplayScaffolder.GameplaySourceFileName);
			File.WriteAllText(gameplaySourcePath, source);
			RewriteGameplayProjectReferences();

			var gameplayProjectPath = _projectService.GameplayProjectPath
			                         ?? throw new AssertionException("Gameplay project path was not set.");
			var startInfo = new ProcessStartInfo("dotnet", $"build \"{gameplayProjectPath}\" /m:1")
			{
				WorkingDirectory = ProjectRootPath,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false
			};

			using var process = Process.Start(startInfo) ?? throw new AssertionException("Failed to start gameplay build process.");
			var standardOutput = process.StandardOutput.ReadToEnd();
			var standardError = process.StandardError.ReadToEnd();
			process.WaitForExit();
			Assert.That(process.ExitCode, Is.EqualTo(0), $"{standardOutput}{Environment.NewLine}{standardError}");
			Assert.That(TypeCatalogImpl.TryGetDescriptor("missing", out _), Is.False);
			Assert.That(global::WolfEngine.Editor.Projects.ProjectTypeCatalog.TryFindGameplayAssemblyPath(gameplayProjectPath), Is.Not.Null);
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
			var projectContents = File.ReadAllText(gameplayProjectPath);
			projectContents = projectContents.Replace("../../WolfEngine/WolfEngine/WolfEngine.csproj", engineProjectPath, StringComparison.Ordinal);
			projectContents = projectContents.Replace("../../WolfEngine/WolfEngine.ECS/WolfEngine.ECS.csproj", ecsProjectPath, StringComparison.Ordinal);
			File.WriteAllText(gameplayProjectPath, projectContents);
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
	}
}
