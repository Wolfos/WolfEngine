using NSubstitute;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;
using WolfEngine.Importing;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class AssetsWindowTests
{
	[Test]
	public void BrowserModelBuild_IncludesEmptyFolders_AndGroupsSubAssetsUnderSource()
	{
		using var assetsRoot = new TemporaryAssetsRoot();
		Directory.CreateDirectory(Path.Combine(assetsRoot.AssetsPath, "Audio"));
		Directory.CreateDirectory(Path.Combine(assetsRoot.AssetsPath, "Empty"));

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
	public void CreateProject_CreatesGameplayScaffoldingAndManifest()
	{
		var parentDirectory = Path.Combine(Path.GetTempPath(), "WolfEngineCreateProjectTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(parentDirectory);

		var projectService = new EditorProjectService(new TrackingProjectAssetPipelineService(), new TestAssetInstanceRegistry());

		try
		{
			Assert.That(projectService.CreateProject(parentDirectory, "My Game", out var errorMessage), Is.True, errorMessage);

			var projectRoot = Path.Combine(parentDirectory, "My Game");
			var manifest = EditorProjectManifestFile.Load(projectRoot);
			var gameplayFolderPath = Path.Combine(projectRoot, ProjectGameplayScaffolder.GameplayFolderName);
			var gameplayProjectPath = Path.Combine(projectRoot, "Gameplay", "My Game.Gameplay.csproj");
			var gameplaySourcePath = Path.Combine(gameplayFolderPath, ProjectGameplayScaffolder.GameplaySourceFileName);
			var solutionPath = Path.Combine(projectRoot, ProjectGameplayScaffolder.GetSolutionFileName("My Game"));
			var projectFileContents = File.ReadAllText(gameplayProjectPath);
			var solutionFileContents = File.ReadAllText(solutionPath);

			Assert.That(Directory.Exists(gameplayFolderPath), Is.True);
			Assert.That(File.Exists(gameplayProjectPath), Is.True);
			Assert.That(File.Exists(gameplaySourcePath), Is.True);
			Assert.That(File.Exists(solutionPath), Is.True);
			Assert.That(manifest.GameplayProjectRelativePath, Is.EqualTo("Gameplay/My Game.Gameplay.csproj"));
			Assert.That(projectService.GameplayProjectRelativePath, Is.EqualTo("Gameplay/My Game.Gameplay.csproj"));
			Assert.That(projectService.GameplayProjectPath, Is.EqualTo(gameplayProjectPath));
			Assert.That(projectFileContents, Does.Contain("../../WolfEngine/WolfEngine/WolfEngine.csproj"));
			Assert.That(projectFileContents, Does.Contain("../../WolfEngine/WolfEngine.ECS/WolfEngine.ECS.csproj"));
			Assert.That(projectFileContents, Does.Contain("../../WolfEngine/WolfEngine.Physics/WolfEngine.Physics.csproj"));
			Assert.That(solutionFileContents, Does.Contain(@"Gameplay\My Game.Gameplay.csproj"));
			Assert.That(solutionFileContents, Does.Contain(@"..\WolfEngine\WolfEngine\WolfEngine.csproj"));
			Assert.That(solutionFileContents, Does.Contain(@"..\WolfEngine\WolfEngine.ECS\WolfEngine.ECS.csproj"));
			Assert.That(solutionFileContents, Does.Contain(@"..\WolfEngine\WolfEngine.Physics\WolfEngine.Physics.csproj"));
			Assert.That(solutionFileContents, Does.Contain(@"..\WolfEngine\WolfEngine.Editor\WolfEngine.Editor.csproj"));
		}
		finally
		{
			projectService.CloseProject();
			if (Directory.Exists(parentDirectory))
			{
				Directory.Delete(parentDirectory, recursive: true);
			}
		}
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

	[Test]
	public void DeleteAssetSource_UsesTargetedIndexRefreshInsteadOfFullProjectRefresh()
	{
		var projectRoot = Path.Combine(Path.GetTempPath(), "WolfEngineDeleteAssetSourceTests", Guid.NewGuid().ToString("N"));
		CreateManifestBackedProjectStructure(projectRoot, "DeleteAssetSourceTests");

		var sourcePath = Path.Combine(projectRoot, "Assets", "file.mat.json");
		File.WriteAllText(sourcePath, "{}");
		File.WriteAllText(sourcePath + ".meta", "{}");

		var pipeline = new TrackingProjectAssetPipelineService();
		var projectService = new EditorProjectService(pipeline, new TestAssetInstanceRegistry());

		try
		{
			Assert.That(projectService.OpenProject(projectRoot, out var errorMessage), Is.True, errorMessage);

			pipeline.ResetCounters();
			projectService.DeleteAssetSource("Assets/file.mat.json");

			Assert.That(pipeline.RefreshProjectCalls, Is.EqualTo(0));
			Assert.That(pipeline.RefreshProjectIncrementalCalls, Is.EqualTo(0));
			Assert.That(pipeline.RemoveDeletedSourceCalls, Is.EqualTo(1));
			Assert.That(pipeline.LoadDatabaseCalls, Is.EqualTo(1));
			Assert.That(pipeline.LastRemovedSourcePath, Is.EqualTo("Assets/file.mat.json"));
		}
		finally
		{
			projectService.CloseProject();
			if (Directory.Exists(projectRoot))
			{
				Directory.Delete(projectRoot, recursive: true);
			}
		}
	}

	[Test]
	public void DeleteFolder_UsesTargetedIndexRefreshInsteadOfFullProjectRefresh()
	{
		var projectRoot = Path.Combine(Path.GetTempPath(), "WolfEngineDeleteFolderTests", Guid.NewGuid().ToString("N"));
		CreateManifestBackedProjectStructure(projectRoot, "DeleteFolderTests");
		Directory.CreateDirectory(Path.Combine(projectRoot, "Assets", "Data"));

		var sourcePath = Path.Combine(projectRoot, "Assets", "Data", "file.data.json");
		File.WriteAllText(sourcePath, "{}");
		File.WriteAllText(sourcePath + ".meta", "{}");

		var pipeline = new TrackingProjectAssetPipelineService();
		var projectService = new EditorProjectService(pipeline, new TestAssetInstanceRegistry());

		try
		{
			Assert.That(projectService.OpenProject(projectRoot, out var errorMessage), Is.True, errorMessage);

			pipeline.ResetCounters();
			projectService.DeleteFolder("Assets/Data");

			Assert.That(pipeline.RefreshProjectCalls, Is.EqualTo(0));
			Assert.That(pipeline.RefreshProjectIncrementalCalls, Is.EqualTo(0));
			Assert.That(pipeline.RemoveDeletedSourcesUnderFolderCalls, Is.EqualTo(1));
			Assert.That(pipeline.LoadDatabaseCalls, Is.EqualTo(1));
			Assert.That(pipeline.LastRemovedFolderPath, Is.EqualTo("Assets/Data"));
		}
		finally
		{
			projectService.CloseProject();
			if (Directory.Exists(projectRoot))
			{
				Directory.Delete(projectRoot, recursive: true);
			}
		}
	}

	[Test]
	public void OpenProject_UsesIncrementalRefresh()
	{
		var projectRoot = Path.Combine(Path.GetTempPath(), "WolfEngineOpenProjectRefreshTests", Guid.NewGuid().ToString("N"));
		CreateManifestBackedProjectStructure(projectRoot, "OpenProjectRefreshTests");

		var pipeline = new TrackingProjectAssetPipelineService();
		var projectService = new EditorProjectService(pipeline, new TestAssetInstanceRegistry());

		try
		{
			Assert.That(projectService.OpenProject(projectRoot, out var errorMessage), Is.True, errorMessage);

			Assert.That(pipeline.RefreshProjectIncrementalCalls, Is.EqualTo(1));
			Assert.That(pipeline.RefreshProjectCalls, Is.EqualTo(0));
		}
		finally
		{
			projectService.CloseProject();
			if (Directory.Exists(projectRoot))
			{
				Directory.Delete(projectRoot, recursive: true);
			}
		}
	}

	[Test]
	public void OpenProject_RebuildsAssetDatabaseWhenLibraryIsMissing()
	{
		var projectRoot = Path.Combine(Path.GetTempPath(), "WolfEngineOpenProjectRebuildTests", Guid.NewGuid().ToString("N"));
		CreateManifestBackedProjectStructure(projectRoot, "OpenProjectRebuildTests");
		Directory.Delete(Path.Combine(projectRoot, "Library"), recursive: true);

		var pipeline = new TrackingProjectAssetPipelineService();
		var notifications = new EditorNotificationService();
		var projectService = new EditorProjectService(pipeline, new TestAssetInstanceRegistry(), notifications);

		try
		{
			Assert.That(projectService.OpenProject(projectRoot, out var errorMessage), Is.True, errorMessage);

			Assert.That(pipeline.RebuildProjectCalls, Is.EqualTo(1));
			Assert.That(pipeline.RefreshProjectIncrementalCalls, Is.EqualTo(0));
			Assert.That(Directory.Exists(Path.Combine(projectRoot, "Library")), Is.True);
			Assert.That(notifications.TryDequeue(out var notification), Is.True);
			Assert.That(notification.Kind, Is.EqualTo(EditorNotificationKind.Info));
			Assert.That(notification.Message, Does.Contain("Library folder was missing"));
		}
		finally
		{
			projectService.CloseProject();
			if (Directory.Exists(projectRoot))
			{
				Directory.Delete(projectRoot, recursive: true);
			}
		}
	}

	[Test]
	public void OpenProject_LoadsGameplayProjectPathFromManifest()
	{
		var projectRoot = Path.Combine(Path.GetTempPath(), "WolfEngineOpenProjectGameplayTests", Guid.NewGuid().ToString("N"));
		CreateManifestBackedProjectStructure(projectRoot, "GameplayDiscoveryTests");

		var pipeline = new TrackingProjectAssetPipelineService();
		var projectService = new EditorProjectService(pipeline, new TestAssetInstanceRegistry());

		try
		{
			Assert.That(projectService.OpenProject(projectRoot, out var errorMessage), Is.True, errorMessage);

			Assert.That(projectService.GameplayProjectRelativePath, Is.EqualTo("Gameplay/GameplayDiscoveryTests.Gameplay.csproj"));
			Assert.That(projectService.GameplayProjectPath, Is.EqualTo(Path.Combine(projectRoot, "Gameplay", "GameplayDiscoveryTests.Gameplay.csproj")));
		}
		finally
		{
			projectService.CloseProject();
			if (Directory.Exists(projectRoot))
			{
				Directory.Delete(projectRoot, recursive: true);
			}
		}
	}

	[Test]
	public void OpenProject_RejectsProjectWithoutManifest()
	{
		var projectRoot = Path.Combine(Path.GetTempPath(), "WolfEngineLegacyProjectTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Path.Combine(projectRoot, "Assets"));

		var projectService = new EditorProjectService(new TrackingProjectAssetPipelineService(), new TestAssetInstanceRegistry());

		try
		{
			Assert.That(projectService.OpenProject(projectRoot, out var errorMessage), Is.False);
			Assert.That(errorMessage, Does.Contain(EditorProjectManifestFile.FileName));
		}
		finally
		{
			projectService.CloseProject();
			if (Directory.Exists(projectRoot))
			{
				Directory.Delete(projectRoot, recursive: true);
			}
		}
	}

	[Test]
	public void OpenProject_RebuildsLibraryAndPreservesAssetsWhenLibraryIsMissing()
	{
		var notifications = new EditorNotificationService();
		using var environment = new EditorProjectTestEnvironment(notifications);
		var result = environment.MaterialCreator.CreateMaterial("Assets/Materials");
		Assert.That(result.Success, Is.True);
		Assert.That(environment.ProjectService.TryGetAsset(result.AssetId!.Value, out var originalAsset), Is.True);

		environment.ProjectService.CloseProject();
		Directory.Delete(Path.Combine(environment.ProjectRootPath, "Library"), recursive: true);

		Assert.That(environment.ProjectService.OpenProject(environment.ProjectRootPath, out var errorMessage), Is.True, errorMessage);

		Assert.That(environment.ProjectService.TryGetAsset(result.AssetId.Value, out var rebuiltAsset), Is.True);
		Assert.That(rebuiltAsset.RelativeSourcePath, Is.EqualTo(originalAsset.RelativeSourcePath));
		Assert.That(Directory.Exists(Path.Combine(environment.ProjectRootPath, "Library")), Is.True);
		Assert.That(File.Exists(Path.Combine(environment.ProjectRootPath, "Library", AssetPipelinePaths.SqliteFileName)), Is.True);
		Assert.That(Directory.Exists(Path.Combine(environment.ProjectRootPath, "Library", AssetPipelinePaths.ImportedFolderName)), Is.True);
		Assert.That(Directory.Exists(Path.Combine(environment.ProjectRootPath, "Library", AssetPipelinePaths.ArtifactsFolderName)), Is.True);
		Assert.That(notifications.TryDequeue(out var notification), Is.True);
		Assert.That(notification.Kind, Is.EqualTo(EditorNotificationKind.Info));
	}

	[Test]
	public void ReloadAssetDatabase_UsesIncrementalRefresh()
	{
		var projectRoot = Path.Combine(Path.GetTempPath(), "WolfEngineReloadRefreshTests", Guid.NewGuid().ToString("N"));
		CreateManifestBackedProjectStructure(projectRoot, "ReloadRefreshTests");

		var pipeline = new TrackingProjectAssetPipelineService();
		var projectService = new EditorProjectService(pipeline, new TestAssetInstanceRegistry());

		try
		{
			Assert.That(projectService.OpenProject(projectRoot, out var errorMessage), Is.True, errorMessage);

			pipeline.ResetCounters();
			projectService.ReloadAssetDatabase();

			Assert.That(pipeline.RefreshProjectIncrementalCalls, Is.EqualTo(1));
			Assert.That(pipeline.RefreshProjectCalls, Is.EqualTo(0));
		}
		finally
		{
			projectService.CloseProject();
			if (Directory.Exists(projectRoot))
			{
				Directory.Delete(projectRoot, recursive: true);
			}
		}
	}

	[Test]
	public void ReloadAssetDatabase_InvalidatesChangedAssetsAndDependentsOnly()
	{
		var projectRoot = Path.Combine(Path.GetTempPath(), "WolfEngineReloadInvalidationTests", Guid.NewGuid().ToString("N"));
		CreateManifestBackedProjectStructure(projectRoot, "ReloadInvalidationTests");

		var changedAssetId = Guid.NewGuid();
		var unchangedAssetId = Guid.NewGuid();
		var dependentAssetId = Guid.NewGuid();
		var pipeline = new TrackingProjectAssetPipelineService
		{
			NextRefreshProjectIncrementalResult = CreateAssetDatabase(
				CreateAssetEntry(changedAssetId, "hash-a"),
				CreateAssetEntry(unchangedAssetId, "hash-static"),
				CreateAssetEntry(dependentAssetId, "hash-dependent")),
		};
		pipeline.DependentInvalidations[changedAssetId] = [dependentAssetId];

		var registry = new TestAssetInstanceRegistry();
		registry.Register(changedAssetId, new object());
		registry.Register(unchangedAssetId, new object());
		registry.Register(dependentAssetId, new object());

		var projectService = new EditorProjectService(pipeline, registry);

		try
		{
			Assert.That(projectService.OpenProject(projectRoot, out var errorMessage), Is.True, errorMessage);

			pipeline.NextRefreshProjectIncrementalResult = CreateAssetDatabase(
				CreateAssetEntry(changedAssetId, "hash-b"),
				CreateAssetEntry(unchangedAssetId, "hash-static"),
				CreateAssetEntry(dependentAssetId, "hash-dependent"));

			registry.Register(changedAssetId, new object());
			registry.Register(unchangedAssetId, new object());
			registry.Register(dependentAssetId, new object());

			var refreshResult = projectService.ReloadAssetDatabase();

			Assert.That(refreshResult.InvalidatedAssetIds, Is.EquivalentTo(new[] { changedAssetId, dependentAssetId }));
			Assert.That(registry.GetInstance(changedAssetId, typeof(object)), Is.Null);
			Assert.That(registry.GetInstance(dependentAssetId, typeof(object)), Is.Null);
			Assert.That(registry.GetInstance(unchangedAssetId, typeof(object)), Is.Not.Null);
		}
		finally
		{
			projectService.CloseProject();
			if (Directory.Exists(projectRoot))
			{
				Directory.Delete(projectRoot, recursive: true);
			}
		}
	}

	[Test]
	public void ReopenProject_WithUnchangedAssets_DoesNotReimportSources()
	{
		using var environment = new EditorProjectTestEnvironment();
		var result = environment.MaterialCreator.CreateMaterial("Assets/Materials");
		Assert.That(result.Success, Is.True);
		Assert.That(environment.ProjectService.TryGetAsset(result.AssetId!.Value, out var asset), Is.True);

		var absoluteMetaPath = environment.ProjectService.GetAbsolutePath(asset.RelativeSourcePath) + ".meta";
		var originalMetaWriteTime = File.GetLastWriteTimeUtc(absoluteMetaPath);

		WaitForTimestampTick();
		environment.ProjectService.CloseProject();

		Assert.That(environment.ProjectService.OpenProject(environment.ProjectRootPath, out var errorMessage), Is.True, errorMessage);
		Assert.That(File.GetLastWriteTimeUtc(absoluteMetaPath), Is.EqualTo(originalMetaWriteTime));
	}

	[Test]
	public void ReopenProject_WithExternallyModifiedSource_ReimportsOnlyChangedSource()
	{
		using var environment = new EditorProjectTestEnvironment();
		var firstResult = environment.MaterialCreator.CreateMaterial("Assets/Materials");
		var secondResult = environment.MaterialCreator.CreateMaterial("Assets/Materials");
		Assert.That(firstResult.Success && secondResult.Success, Is.True);
		Assert.That(environment.ProjectService.TryGetAsset(firstResult.AssetId!.Value, out var firstAsset), Is.True);
		Assert.That(environment.ProjectService.TryGetAsset(secondResult.AssetId!.Value, out var secondAsset), Is.True);

		var changedSourcePath = environment.ProjectService.GetAbsolutePath(firstAsset.RelativeSourcePath);
		var unchangedMetaPath = environment.ProjectService.GetAbsolutePath(secondAsset.RelativeSourcePath) + ".meta";
		var unchangedMetaWriteTime = File.GetLastWriteTimeUtc(unchangedMetaPath);
		var changedMaterial = environment.MaterialStore.LoadAsset(changedSourcePath);
		changedMaterial.MaterialType = MaterialAssetType.AlphaBlend;

		WaitForTimestampTick();
		environment.MaterialStore.SaveAsset(changedSourcePath, changedMaterial);
		var changedMetaPath = changedSourcePath + ".meta";
		var previousChangedMetaWriteTime = File.GetLastWriteTimeUtc(changedMetaPath);

		WaitForTimestampTick();
		environment.ProjectService.CloseProject();
		Assert.That(environment.ProjectService.OpenProject(environment.ProjectRootPath, out var errorMessage), Is.True, errorMessage);

		Assert.That(environment.ProjectService.TryGetAsset(firstResult.AssetId.Value, out var refreshedChangedAsset), Is.True);
		Assert.That(refreshedChangedAsset.GetRequiredSummary<MaterialAssetSummary>().MaterialType, Is.EqualTo(MaterialAssetType.AlphaBlend));
		Assert.That(File.GetLastWriteTimeUtc(changedMetaPath), Is.GreaterThan(previousChangedMetaWriteTime));
		Assert.That(File.GetLastWriteTimeUtc(unchangedMetaPath), Is.EqualTo(unchangedMetaWriteTime));
	}

	[Test]
	public void ReloadAssetDatabase_ProcessesAddedChangedAndDeletedSourcesIncrementally()
	{
		using var environment = new EditorProjectTestEnvironment();
		var changedResult = environment.MaterialCreator.CreateMaterial("Assets/Materials");
		var deletedResult = environment.DataAssetCreator.CreateDataAsset(typeof(RenderConfig), "Assets/Data");
		Assert.That(changedResult.Success && deletedResult.Success, Is.True);
		Assert.That(environment.ProjectService.TryGetAsset(changedResult.AssetId!.Value, out var changedAsset), Is.True);
		Assert.That(environment.ProjectService.TryGetAsset(deletedResult.AssetId!.Value, out var deletedAsset), Is.True);

		var changedSourcePath = environment.ProjectService.GetAbsolutePath(changedAsset.RelativeSourcePath);
		var deletedSourcePath = environment.ProjectService.GetAbsolutePath(deletedAsset.RelativeSourcePath);
		var deletedMetaPath = deletedSourcePath + ".meta";
		var newSourcePath = environment.ProjectService.GetAbsolutePath("Assets/Materials/Externally Added.mat.json");
		var changedMaterial = environment.MaterialStore.LoadAsset(changedSourcePath);
		changedMaterial.MaterialType = MaterialAssetType.AlphaTest;
		var newMaterial = environment.MaterialStore.CreateDefault(MaterialAssetType.AlphaBlend);

		WaitForTimestampTick();
		environment.MaterialStore.SaveAsset(changedSourcePath, changedMaterial);
		environment.MaterialStore.SaveAsset(newSourcePath, newMaterial);
		File.Delete(deletedSourcePath);
		File.Delete(deletedMetaPath);

		environment.ProjectService.ReloadAssetDatabase();

		Assert.That(environment.ProjectService.TryGetAsset(changedResult.AssetId.Value, out var refreshedChangedAsset), Is.True);
		Assert.That(refreshedChangedAsset.GetRequiredSummary<MaterialAssetSummary>().MaterialType, Is.EqualTo(MaterialAssetType.AlphaTest));
		Assert.That(environment.ProjectService.CurrentAssetDatabase.Assets.Any(asset => asset.RelativeSourcePath == deletedAsset.RelativeSourcePath), Is.False);
		Assert.That(environment.ProjectService.CurrentAssetDatabase.Assets.Any(asset => asset.RelativeSourcePath == "Assets/Materials/Externally Added.mat.json"), Is.True);
		Assert.That(File.Exists(newSourcePath + ".meta"), Is.True);
	}

	[Test]
	public void ReloadAssetDatabase_RecreatesMissingMetaFile()
	{
		using var environment = new EditorProjectTestEnvironment();
		var result = environment.MaterialCreator.CreateMaterial("Assets/Materials");
		Assert.That(result.Success, Is.True);
		Assert.That(environment.ProjectService.TryGetAsset(result.AssetId!.Value, out var asset), Is.True);

		var absoluteSourcePath = environment.ProjectService.GetAbsolutePath(asset.RelativeSourcePath);
		var absoluteMetaPath = absoluteSourcePath + ".meta";
		File.Delete(absoluteMetaPath);

		environment.ProjectService.ReloadAssetDatabase();

		Assert.That(File.Exists(absoluteMetaPath), Is.True);
		Assert.That(environment.ProjectService.CurrentAssetDatabase.Assets.Any(candidate => candidate.RelativeSourcePath == asset.RelativeSourcePath), Is.True);
	}

	private sealed class TemporaryAssetsRoot : IDisposable
	{
		public TemporaryAssetsRoot()
		{
			var root = Path.Combine(Path.GetTempPath(), "WolfEngineAssetsWindowTests", Guid.NewGuid().ToString("N"), "Assets");
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
		public EditorProjectTestEnvironment(IEditorNotificationService? notificationService = null)
		{
			ParentDirectory = Path.Combine(Path.GetTempPath(), "WolfEngineAssetsWindowProjectTests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(ParentDirectory);

			Registry = new TestAssetInstanceRegistry();
			AssetDatabase.SetInstanceRegistry(Registry);

			var pipelineService = new ProjectAssetPipelineService(
				new AssetPipelineIndex(),
				new AssetMetadataStore(),
				Substitute.For<Importing.IImageLoader>(),
				new DataAssetStore(),
				new MaterialAssetStore(),
				Substitute.For<IThreeDFileImporter>());
			PipelineService = pipelineService;
			ProjectService = new EditorProjectService(pipelineService, Registry, notificationService);
			if (ProjectService.CreateProject(ParentDirectory, "Project", out var errorMessage) == false)
			{
				throw new AssertionException(errorMessage);
			}

			MaterialCreator = new MaterialAssetCreator(ProjectService, new MaterialAssetStore(), new AssetMetadataStore(), PipelineService);
			DataAssetCreator = new DataAssetCreator(ProjectService, new DataAssetStore(), new AssetMetadataStore(), PipelineService);
		}

		public string ParentDirectory { get; }
		public string ProjectRootPath => Path.Combine(ParentDirectory, "Project");
		public TestAssetInstanceRegistry Registry { get; }
		public IProjectAssetPipelineService PipelineService { get; }
		public IEditorProjectService ProjectService { get; }
		public IMaterialAssetCreator MaterialCreator { get; }
		public IDataAssetCreator DataAssetCreator { get; }
		public MaterialAssetStore MaterialStore { get; } = new();

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

	private sealed class TrackingProjectAssetPipelineService : IProjectAssetPipelineService
	{
		public int RefreshProjectCalls { get; private set; }
		public int RebuildProjectCalls { get; private set; }
		public int RefreshProjectIncrementalCalls { get; private set; }
		public int RemoveDeletedSourceCalls { get; private set; }
		public int RemoveDeletedSourcesUnderFolderCalls { get; private set; }
		public int LoadDatabaseCalls { get; private set; }
		public string? LastRemovedSourcePath { get; private set; }
		public string? LastRemovedFolderPath { get; private set; }

		public void InitializeProject(string projectRootPath)
		{
			Directory.CreateDirectory(Path.Combine(projectRootPath, "Assets"));
			Directory.CreateDirectory(Path.Combine(projectRootPath, "Library"));
		}

		public AssetDatabase RefreshProject(string projectRootPath)
		{
			RefreshProjectCalls++;
			return new AssetDatabase();
		}

		public AssetDatabase RebuildProject(string projectRootPath)
		{
			RebuildProjectCalls++;
			InitializeProject(projectRootPath);
			return new AssetDatabase();
		}

		public AssetDatabase RefreshProjectIncremental(string projectRootPath)
		{
			RefreshProjectIncrementalCalls++;
			return NextRefreshProjectIncrementalResult ?? new AssetDatabase();
		}

		public IReadOnlyCollection<Guid> ExpandInvalidationClosure(string projectRootPath, IEnumerable<Guid> changedNodeIds)
		{
			var results = changedNodeIds
				.Where(nodeId => nodeId != Guid.Empty)
				.ToHashSet();
			foreach (var nodeId in results.ToArray())
			{
				if (DependentInvalidations.TryGetValue(nodeId, out var dependentNodeIds) == false)
				{
					continue;
				}

				foreach (var dependentNodeId in dependentNodeIds)
				{
					results.Add(dependentNodeId);
				}
			}

			return results.ToArray();
		}

		public void RemoveDeletedSource(string projectRootPath, string relativeSourcePath)
		{
			RemoveDeletedSourceCalls++;
			LastRemovedSourcePath = relativeSourcePath;
		}

		public void RemoveDeletedSourcesUnderFolder(string projectRootPath, string relativeFolderPath)
		{
			RemoveDeletedSourcesUnderFolderCalls++;
			LastRemovedFolderPath = relativeFolderPath;
		}

		public void ReimportSource(string projectRootPath, string relativeSourcePath)
		{
		}

		public AssetDatabase LoadDatabase(string projectRootPath)
		{
			LoadDatabaseCalls++;
			return new AssetDatabase();
		}

		public bool TryGetAsset(string projectRootPath, Guid nodeId, out AssetDatabaseEntry asset)
		{
			asset = null!;
			return false;
		}

		public bool TryGetPrimaryNodeIdForRelativeSourcePath(string projectRootPath, string relativeSourcePath, out Guid nodeId)
		{
			nodeId = Guid.Empty;
			return false;
		}

		public AssetImportResult ImportExternalSource(string projectRootPath, string absoluteSourcePath)
		{
			return new AssetImportResult();
		}

		public void InstantiateImportedModel(string projectRootPath, Guid modelNodeId, World world)
		{
		}

		public void InstantiatePrefab(string projectRootPath, Guid prefabNodeId, EditorScene scene)
		{
		}

		public void ResetCounters()
		{
			RefreshProjectCalls = 0;
			RebuildProjectCalls = 0;
			RefreshProjectIncrementalCalls = 0;
			RemoveDeletedSourceCalls = 0;
			RemoveDeletedSourcesUnderFolderCalls = 0;
			LoadDatabaseCalls = 0;
			LastRemovedSourcePath = null;
			LastRemovedFolderPath = null;
		}

		public AssetDatabase? NextRefreshProjectIncrementalResult { get; set; }
		public Dictionary<Guid, List<Guid>> DependentInvalidations { get; } = new();
	}

	private static void WaitForTimestampTick()
	{
		Thread.Sleep(1100);
	}

	private static AssetDatabase CreateAssetDatabase(params AssetDatabaseEntry[] assets)
	{
		return new AssetDatabase
		{
			Assets = assets.ToList()
		};
	}

	private static AssetDatabaseEntry CreateAssetEntry(Guid assetId, string contentHash)
	{
		return new AssetDatabaseEntry
		{
			Id = assetId,
			SourceId = assetId,
			Type = AssetType.Mesh,
			Name = assetId.ToString("N"),
			NodeKey = "main",
			RelativeSourcePath = $"Assets/{assetId:N}.glb",
			RelativeAssetPath = $"Assets/{assetId:N}.glb",
			RelativeMetaPath = $"Assets/{assetId:N}.glb.meta",
			Artifacts =
			[
				new AssetArtifactRecord
				{
					NodeId = assetId,
					ArtifactKey = "mesh",
					Kind = "RuntimeMesh",
					RelativePath = $"Library/Artifacts/{assetId:N}.mesh.bin",
					ContentHash = contentHash
				}
			]
		};
	}

	private static void CreateManifestBackedProjectStructure(string projectRoot, string projectName)
	{
		Directory.CreateDirectory(projectRoot);
		Directory.CreateDirectory(Path.Combine(projectRoot, "Assets"));
		Directory.CreateDirectory(Path.Combine(projectRoot, "Library"));
		ProjectGameplayScaffolder.Scaffold(projectRoot, projectName);
		EditorProjectManifestFile.Save(projectRoot, new EditorProjectManifest
		{
			GameplayProjectRelativePath = ProjectGameplayScaffolder.GetGameplayProjectRelativePath(projectName)
		});
	}
}
