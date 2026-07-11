using Wolfie.IAE.Projects;
using Wolfie.IAE.UnityAssets;
using Wolfie.IAE.ManagedAssets;

namespace Wolfie.IAE.Tests;

public sealed class WolfieProjectTests
{
	private string _root = null!;

	[SetUp]
	public void SetUp()
	{
		_root = Path.Combine(Path.GetTempPath(), "WolfieTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_root);
	}

	[TearDown]
	public void TearDown() => Directory.Delete(_root, true);

	[Test]
	public void UnityValidationRequiresAssetsAndProjectSettings()
	{
		Directory.CreateDirectory(Path.Combine(_root, "Assets"));
		Assert.That(WolfieProjectService.ValidateUnityProject(_root, out var error), Is.False);
		Assert.That(error, Does.Contain("ProjectSettings"));
		Directory.CreateDirectory(Path.Combine(_root, "ProjectSettings"));
		Assert.That(WolfieProjectService.ValidateUnityProject(_root, out _), Is.True);
	}

	[Test]
	public void CreateAndOpenPreserveStableProjectData()
	{
		var unity = CreateUnityProject();
		var service = new WolfieProjectService();
		var created = service.Create(unity, _root, "Art Project", out var file);
		var opened = service.Open(file);
		var destination = Path.Combine(_root, "Art Project");

		Assert.Multiple(() =>
		{
			Assert.That(opened.ProjectId, Is.EqualTo(created.ProjectId));
			Assert.That(opened.FormatVersion, Is.EqualTo(WolfieProject.CurrentFormatVersion));
			Assert.That(opened.Name, Is.EqualTo("Art Project"));
			Assert.That(opened.UnityProjectPath, Is.EqualTo(WolfiePath.NormalizeAbsolute(unity)));
			Assert.That(Directory.Exists(Path.Combine(destination, "Assets")), Is.True);
			Assert.That(Directory.Exists(Path.Combine(destination, "Cache")), Is.True);
		});
	}

	[Test]
	public void CreateRejectsDestinationInsideUnityProject()
	{
		var unity = CreateUnityProject();
		var service = new WolfieProjectService();
		Assert.That(() => service.Create(unity, unity, "Wolfie", out _),
			Throws.InvalidOperationException.With.Message.Contains("separate"));
	}

	[Test]
	public void PathsNormalizeAndRespectDirectoryBoundaries()
	{
		var parent = Path.Combine(_root, "Unity");
		Assert.That(WolfiePath.IsWithin(Path.Combine(parent, ".", "Assets"), parent), Is.True);
		Assert.That(WolfiePath.IsWithin(parent + "-Other", parent), Is.False);
	}

	[Test]
	public void ScannerPreservesHierarchyAndFiltersMetaFiles()
	{
		var unity = CreateUnityProject();
		var folder = Directory.CreateDirectory(Path.Combine(unity, "Assets", "Models")).FullName;
		File.WriteAllText(Path.Combine(folder, "Tree.fbx"), "not loaded by scanner");
		File.WriteAllText(Path.Combine(folder, "Tree.fbx.meta"), "guid: ignored");
		var result = new UnityAssetScanner().Scan(unity);
		var models = result.Root.Children.Single(entry => entry.Name == "Models");

		Assert.Multiple(() =>
		{
			Assert.That(models.RelativePath, Is.EqualTo("Assets/Models"));
			Assert.That(models.Children.Select(entry => entry.Name), Is.EqualTo(new[] { "Tree.fbx" }));
			Assert.That(models.Children[0].Extension, Is.EqualTo(".fbx"));
		});
	}

	[Test]
	public void PreferencesPersistTheLastProjectPath()
	{
		var preferencesFile = Path.Combine(_root, "Preferences", "WolfiePreferences.json");
		var projectFile = Path.Combine(_root, "Project", "Art.wolfieproject");
		var preferences = new WolfiePreferences(preferencesFile);
		preferences.SetLastProjectPath(projectFile);

		Assert.That(new WolfiePreferences(preferencesFile).LastProjectPath,
			Is.EqualTo(WolfiePath.NormalizeAbsolute(projectFile)));
	}

	[Test]
	public void ManageTextureCopiesSourceAndPersistsStableOwnership()
	{
		var unity = CreateUnityProject();
		var texture = Path.Combine(unity, "Assets", "Art", "Stone.png");
		Directory.CreateDirectory(Path.GetDirectoryName(texture)!);
		File.WriteAllText(texture, "pixels");
		File.WriteAllText(texture + ".meta", "fileFormatVersion: 2\nguid: 0123456789abcdef0123456789abcdef\n");
		var projects = new WolfieProjectService();
		var project = projects.Create(unity, _root, "WolfieArt", out var projectFile);
		var service = new ManagedAssetService();

		var first = service.ManageTexture(project, projectFile, "Assets/Art/Stone.png");
		var second = service.ManageTexture(project, projectFile, "Assets/Art/Stone.png");
		var wolfieTexture = Path.Combine(_root, "WolfieArt", "Assets", "Art", "Stone.png");
		var reloaded = service.LoadAll(projectFile)["Assets/Art/Stone.png"];
		var browserEntry = new UnityAssetScanner().Scan(project, projectFile, service).Root.Children
			.Single(entry => entry.Name == "Art").Children.Single(entry => entry.Name == "Stone.png");

		Assert.Multiple(() =>
		{
			Assert.That(File.ReadAllText(wolfieTexture), Is.EqualTo("pixels"));
			Assert.That(File.Exists(wolfieTexture + ".meta"), Is.True);
			Assert.That(second.SourceId, Is.EqualTo(first.SourceId));
			Assert.That(reloaded.SourceId, Is.EqualTo(first.SourceId));
			Assert.That(reloaded.SourcePath, Is.EqualTo("Assets/Art/Stone.png"));
			Assert.That(reloaded.Outputs.Single().Path, Is.EqualTo("Assets/Art/Stone.png"));
			Assert.That(reloaded.Outputs.Single().UnityGuid, Is.EqualTo("0123456789abcdef0123456789abcdef"));
			Assert.That(browserEntry.IsManaged, Is.True);
			Assert.That(browserEntry.ManagedAssetId, Is.EqualTo(first.SourceId));
			Assert.That(browserEntry.UnityGuid, Is.EqualTo("0123456789abcdef0123456789abcdef"));
		});
	}

	[Test]
	public void PublishingPreservesUnityMetaAndRejectsUnregisteredOutputs()
	{
		var unity = CreateUnityProject();
		var texture = Path.Combine(unity, "Assets", "Stone.png");
		File.WriteAllText(texture, "old");
		File.WriteAllText(texture + ".meta", "guid: fedcba9876543210fedcba9876543210\n");
		var projects = new WolfieProjectService();
		var project = projects.Create(unity, _root, "WolfieArt", out var projectFile);
		var service = new ManagedAssetService();
		var asset = service.ManageTexture(project, projectFile, "Assets/Stone.png");

		using (var content = new MemoryStream("new"u8.ToArray()))
			service.PublishOutput(project, asset, "Assets/Stone.png", content);

		Assert.Multiple(() =>
		{
			Assert.That(File.ReadAllText(texture), Is.EqualTo("new"));
			Assert.That(File.ReadAllText(texture + ".meta"), Does.Contain("fedcba9876543210fedcba9876543210"));
			Assert.That(Directory.EnumerateFiles(Path.GetDirectoryName(texture)!, "*.tmp"), Is.Empty);
		});
		using var forbidden = new MemoryStream("bad"u8.ToArray());
		Assert.That(() => service.PublishOutput(project, asset, "Assets/UserOwned.png", forbidden),
			Throws.InvalidOperationException.With.Message.Contains("unregistered"));
		Assert.That(File.Exists(Path.Combine(unity, "Assets", "UserOwned.png")), Is.False);
	}

	[Test]
	public void UnmanageDeletesOnlyWolfieSourceAndEmptyFolders()
	{
		var unity = CreateUnityProject();
		var texture = Path.Combine(unity, "Assets", "Nested", "Stone.png");
		Directory.CreateDirectory(Path.GetDirectoryName(texture)!);
		File.WriteAllText(texture, "unity remains");
		var projects = new WolfieProjectService();
		var project = projects.Create(unity, _root, "WolfieArt", out var projectFile);
		var service = new ManagedAssetService();
		service.ManageTexture(project, projectFile, "Assets/Nested/Stone.png");

		service.Unmanage(projectFile, "Assets/Nested/Stone.png");

		Assert.Multiple(() =>
		{
			Assert.That(File.Exists(texture), Is.True);
			Assert.That(Directory.Exists(Path.Combine(_root, "WolfieArt", "Assets", "Nested")), Is.False);
			Assert.That(service.LoadAll(projectFile), Is.Empty);
		});
	}

	private string CreateUnityProject()
	{
		var unity = Path.Combine(_root, "Unity");
		Directory.CreateDirectory(Path.Combine(unity, "Assets"));
		Directory.CreateDirectory(Path.Combine(unity, "ProjectSettings"));
		return unity;
	}
}
