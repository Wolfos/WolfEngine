using Wolfie.IAE.Projects;
using Wolfie.IAE.UnityAssets;

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

	private string CreateUnityProject()
	{
		var unity = Path.Combine(_root, "Unity");
		Directory.CreateDirectory(Path.Combine(unity, "Assets"));
		Directory.CreateDirectory(Path.Combine(unity, "ProjectSettings"));
		return unity;
	}
}
