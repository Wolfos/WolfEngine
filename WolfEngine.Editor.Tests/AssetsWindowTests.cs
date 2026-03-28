using NSubstitute;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;
using WolfEngine.Importing;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.ECS.Tests;

[TestFixture]
public sealed class AssetsWindowTests
{
	[Test]
	public void BrowserModelBuild_IncludesEmptyFolders_AndGroupsSubAssetsUnderSource()
	{
		using var assetsRoot = new TemporaryAssetsRoot();
		Directory.CreateDirectory(System.IO.Path.Combine(assetsRoot.AssetsPath, "Audio"));
		Directory.CreateDirectory(System.IO.Path.Combine(assetsRoot.AssetsPath, "Empty"));

		var sourceId = Guid.NewGuid();
		var primaryAssetId = Guid.NewGuid();
		var subAssetId = Guid.NewGuid();
		var database = new AssetDatabase
		{
			Assets =
			[
				new AssetDatabaseEntry
				{
					Id = primaryAssetId,
					SourceId = sourceId,
					Type = AssetType.Model3D,
					Name = "Birds",
					NodeKey = "main",
					RelativeSourcePath = "Assets/Audio/birds.glb",
					RelativeAssetPath = "Assets/Audio/birds.glb"
				},
				new AssetDatabaseEntry
				{
					Id = subAssetId,
					SourceId = sourceId,
					Type = AssetType.Material,
					Name = "BirdMaterial",
					NodeKey = "bird-material",
					IsGenerated = true,
					RelativeSourcePath = "Assets/Audio/birds.glb",
					RelativeAssetPath = "Assets/Materials/bird.mat.json"
				}
			]
		};

		var browserModel = AssetsWindowBrowserModelBuilder.Build(database.Assets, assetsRoot.AssetsPath);

		Assert.That(browserModel.FoldersByPath.ContainsKey("Assets/Empty"), Is.True);
		Assert.That(browserModel.FoldersByPath.ContainsKey("Assets/Audio"), Is.True);

		var audioFolder = browserModel.FoldersByPath["Assets/Audio"];
		Assert.That(audioFolder.Sources, Has.Count.EqualTo(1));
		Assert.That(audioFolder.Sources[0].DisplayName, Is.EqualTo("birds.glb"));
		Assert.That(audioFolder.Sources[0].PrimaryAsset.Id, Is.EqualTo(primaryAssetId));
		Assert.That(audioFolder.Sources[0].SubAssets.Select(asset => asset.Id), Is.EqualTo(new[] { subAssetId }));
	}

	[Test]
	public void ToggleExpandedSource_SwitchesBetweenSources_AndCollapsesRepeatedClick()
	{
		var firstSourceId = Guid.NewGuid();
		var secondSourceId = Guid.NewGuid();

		var expandedSourceId = AssetsWindowBrowserModelBuilder.ToggleExpandedSource(null, firstSourceId);
		Assert.That(expandedSourceId, Is.EqualTo(firstSourceId));

		expandedSourceId = AssetsWindowBrowserModelBuilder.ToggleExpandedSource(expandedSourceId, secondSourceId);
		Assert.That(expandedSourceId, Is.EqualTo(secondSourceId));

		expandedSourceId = AssetsWindowBrowserModelBuilder.ToggleExpandedSource(expandedSourceId, secondSourceId);
		Assert.That(expandedSourceId, Is.Null);
	}

	[Test]
	public void MaterialCreator_CreatesAssetInsideTargetFolder()
	{
		using var environment = new EditorProjectTestEnvironment();
		var targetFolderPath = "Assets/Materials/UI";

		var result = environment.MaterialCreator.CreateMaterial(targetFolderPath);

		Assert.That(result.Success, Is.True);
		Assert.That(result.AssetId.HasValue, Is.True);
		Assert.That(environment.ProjectService.TryGetAsset(result.AssetId!.Value, out var asset), Is.True);
		Assert.That(asset.RelativeSourcePath, Is.EqualTo("Assets/Materials/UI/New Material.mat.json"));
		Assert.That(File.Exists(environment.ProjectService.GetAbsolutePath(asset.RelativeSourcePath)), Is.True);
	}

	[Test]
	public void DataAssetCreator_CreatesAssetInsideTargetFolder()
	{
		using var environment = new EditorProjectTestEnvironment();
		var targetFolderPath = "Assets/Data/Gameplay";

		var result = environment.DataAssetCreator.CreateDataAsset(typeof(RenderConfig), targetFolderPath);

		Assert.That(result.Success, Is.True);
		Assert.That(result.AssetId.HasValue, Is.True);
		Assert.That(environment.ProjectService.TryGetAsset(result.AssetId!.Value, out var asset), Is.True);
		Assert.That(asset.RelativeSourcePath, Is.EqualTo("Assets/Data/Gameplay/New RenderConfig.data.json"));
		Assert.That(File.Exists(environment.ProjectService.GetAbsolutePath(asset.RelativeSourcePath)), Is.True);
	}

	[Test]
	public void DeleteAssetSource_RemovesFileMetaAndDatabaseEntries()
	{
		using var environment = new EditorProjectTestEnvironment();
		var result = environment.MaterialCreator.CreateMaterial("Assets/Materials");
		Assert.That(result.Success, Is.True);
		Assert.That(environment.ProjectService.TryGetAsset(result.AssetId!.Value, out var asset), Is.True);

		var absoluteSourcePath = environment.ProjectService.GetAbsolutePath(asset.RelativeSourcePath);
		var absoluteMetaPath = absoluteSourcePath + ".meta";

		environment.ProjectService.DeleteAssetSource(asset.RelativeSourcePath);

		Assert.That(File.Exists(absoluteSourcePath), Is.False);
		Assert.That(File.Exists(absoluteMetaPath), Is.False);
		Assert.That(environment.ProjectService.CurrentAssetDatabase.Assets.Any(candidate => candidate.SourceId == asset.SourceId), Is.False);
	}

	[Test]
	public void DeleteFolder_RemovesNestedAssetsAndDatabaseEntries()
	{
		using var environment = new EditorProjectTestEnvironment();
		var result = environment.DataAssetCreator.CreateDataAsset(typeof(RenderConfig), "Assets/Data/Nested");
		Assert.That(result.Success, Is.True);
		Assert.That(environment.ProjectService.TryGetAsset(result.AssetId!.Value, out var asset), Is.True);

		environment.ProjectService.DeleteFolder("Assets/Data");

		Assert.That(Directory.Exists(environment.ProjectService.GetAbsolutePath("Assets/Data")), Is.False);
		Assert.That(environment.ProjectService.CurrentAssetDatabase.Assets.Any(candidate => candidate.SourceId == asset.SourceId), Is.False);
	}

	[Test]
	public void DeleteOperations_RejectPathsOutsideAssets()
	{
		using var environment = new EditorProjectTestEnvironment();

		Assert.That(
			() => environment.ProjectService.DeleteAssetSource("../outside.txt"),
			Throws.TypeOf<InvalidOperationException>());
		Assert.That(
			() => environment.ProjectService.DeleteFolder("Library"),
			Throws.TypeOf<InvalidOperationException>());
	}

	private sealed class TemporaryAssetsRoot : IDisposable
	{
		public TemporaryAssetsRoot()
		{
			var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WolfEngineAssetsWindowTests", Guid.NewGuid().ToString("N"), "Assets");
			Directory.CreateDirectory(root);
			AssetsPath = root;
		}

		public string AssetsPath { get; }

		public void Dispose()
		{
			var projectRoot = Directory.GetParent(AssetsPath)?.Parent?.FullName;
			if (string.IsNullOrWhiteSpace(projectRoot) == false && Directory.Exists(projectRoot))
			{
				Directory.Delete(projectRoot, recursive: true);
			}
		}
	}

	private sealed class EditorProjectTestEnvironment : IDisposable
	{
		public EditorProjectTestEnvironment()
		{
			ParentDirectory = Path.Combine(Path.GetTempPath(), "WolfEngineAssetsWindowProjectTests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(ParentDirectory);

			Registry = new TestAssetInstanceRegistry();
			AssetDatabase.SetInstanceRegistry(Registry);

			var pipelineService = new ProjectAssetPipelineService(
				new AssetPipelineIndex(),
				new AssetMetadataStore(),
				Substitute.For<global::WolfEngine.Importing.IImageLoader>(),
				new DataAssetStore(),
				new MaterialAssetStore(),
				Substitute.For<IThreeDFileImporter>());
			PipelineService = pipelineService;
			ProjectService = new EditorProjectService(pipelineService, Registry);
			if (ProjectService.CreateProject(ParentDirectory, "Project", out var errorMessage) == false)
			{
				throw new AssertionException(errorMessage);
			}

			MaterialCreator = new MaterialAssetCreator(ProjectService, new MaterialAssetStore(), new AssetMetadataStore(), PipelineService);
			DataAssetCreator = new DataAssetCreator(ProjectService, new DataAssetStore(), new AssetMetadataStore(), PipelineService);
		}

		public string ParentDirectory { get; }
		public TestAssetInstanceRegistry Registry { get; }
		public IProjectAssetPipelineService PipelineService { get; }
		public IEditorProjectService ProjectService { get; }
		public IMaterialAssetCreator MaterialCreator { get; }
		public IDataAssetCreator DataAssetCreator { get; }

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
