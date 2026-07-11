using Wolfie.IAE.Projects;
using Wolfie.IAE.UnityAssets;
using Wolfie.IAE.ManagedAssets;
using Wolfie.IAE.ExternalTools;

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
			Assert.That(Directory.Exists(Path.Combine(destination, "Templates")), Is.True);
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
	public void WorkspaceScannerExposesTemplatesAsWolfieOwnedTopLevelContent()
	{
		var unity = CreateUnityProject();
		var projects = new WolfieProjectService();
		var project = projects.Create(unity, _root, "WolfieArt", out var projectFile);
		var templateFolder = Path.Combine(_root, "WolfieArt", "Templates", "Models");
		Directory.CreateDirectory(templateFolder);
		File.WriteAllText(Path.Combine(templateFolder, "Default.blend"), "template");

		var result = new UnityAssetScanner().Scan(project, projectFile, new ManagedAssetService());
		var templates = result.Root.Children.Single(entry => entry.RelativePath == "Templates");
		var template = templates.Children.Single().Children.Single();

		Assert.Multiple(() =>
		{
			Assert.That(result.Root.Children.Select(entry => entry.RelativePath), Is.EqualTo(new[] { "Assets", "Templates" }));
			Assert.That(templates.IsManaged, Is.True);
			Assert.That(template.RelativePath, Is.EqualTo("Templates/Models/Default.blend"));
			Assert.That(template.IsManaged, Is.True);
		});
	}

	[Test]
	public void CreateFromTemplateCreatesManagedSourceMetadataAndBrowserEntry()
	{
		var unity = CreateUnityProject();
		var projects = new WolfieProjectService();
		var project = projects.Create(unity, _root, "WolfieArt", out var projectFile);
		var template = Path.Combine(_root, "WolfieArt", "Templates", "Default.blend");
		File.WriteAllText(template, "blend template");
		var service = new ManagedAssetService();

		var created = service.CreateFromTemplate(projectFile, "Assets/Characters",
			"Templates/Default.blend", "Hero");
		var source = Path.Combine(_root, "WolfieArt", "Assets", "Characters", "Hero.blend");
		var loaded = service.LoadAll(projectFile)["Assets/Characters/Hero.blend"];
		var browserFile = new UnityAssetScanner().Scan(project, projectFile, service).Root.Children
			.Single(entry => entry.RelativePath == "Assets").Children.Single(entry => entry.Name == "Characters")
			.Children.Single(entry => entry.Name == "Hero.blend");

		Assert.Multiple(() =>
		{
			Assert.That(File.ReadAllText(source), Is.EqualTo("blend template"));
			Assert.That(File.Exists(source + ".meta"), Is.True);
			Assert.That(loaded.SourceId, Is.EqualTo(created.SourceId));
			Assert.That(loaded.ImporterId, Is.EqualTo("blender"));
			Assert.That(browserFile.IsManaged, Is.True);
			Assert.That(browserFile.ManagedAssetId, Is.EqualTo(created.SourceId));
			Assert.That(File.Exists(Path.Combine(unity, "Assets", "Characters", "Hero.blend")), Is.False);
		});
	}

	[Test]
	public void CreateFromTemplateValidatesNamesAndLeavesNoPartialAsset()
	{
		var unity = CreateUnityProject();
		var projects = new WolfieProjectService();
		projects.Create(unity, _root, "WolfieArt", out var projectFile);
		File.WriteAllText(Path.Combine(_root, "WolfieArt", "Templates", "Default.spp"), "template");
		var service = new ManagedAssetService();

		Assert.That(() => service.CreateFromTemplate(projectFile, "Assets/Materials",
			"Templates/Default.spp", "../Invalid.spp"), Throws.ArgumentException);
		Assert.That(() => service.CreateFromTemplate(projectFile, "Templates",
			"Templates/Default.spp", "Valid"), Throws.ArgumentException);

		var assets = Path.Combine(_root, "WolfieArt", "Assets");
		Assert.Multiple(() =>
		{
			Assert.That(Directory.EnumerateFiles(assets, "*", SearchOption.AllDirectories), Is.Empty);
			Assert.That(Directory.EnumerateDirectories(assets, "*", SearchOption.AllDirectories), Is.Empty);
		});
	}

	[Test]
	public void BlenderLauncherBuildsSafeArgumentListForConfiguredApplication()
	{
		var blender = OperatingSystem.IsMacOS() ? Path.Combine(_root, "Blender.app") : Path.Combine(_root, "blender");
		if (OperatingSystem.IsMacOS()) Directory.CreateDirectory(blender); else File.WriteAllText(blender, string.Empty);
		var source = Path.Combine(_root, "My Model.blend");

		var startInfo = BlenderLauncher.CreateStartInfo(blender, source);

		if (OperatingSystem.IsMacOS())
		{
			Assert.That(startInfo.FileName, Is.EqualTo("/usr/bin/open"));
			Assert.That(startInfo.ArgumentList, Is.EqualTo(new[] { "-a", blender, source }));
		}
		else
		{
			Assert.That(startInfo.FileName, Is.EqualTo(blender));
			Assert.That(startInfo.ArgumentList, Is.EqualTo(new[] { source }));
		}
		Assert.That(startInfo.UseShellExecute, Is.False);
	}

	[Test]
	public void BlenderLauncherRequiresConfiguredBlenderBeforeStartingProcess()
	{
		var unity = CreateUnityProject();
		var projects = new WolfieProjectService();
		projects.Create(unity, _root, "WolfieArt", out var projectFile);
		var source = Path.Combine(_root, "WolfieArt", "Assets", "Model.blend");
		File.WriteAllText(source, "blend");
		File.WriteAllText(source + ".meta", "{}");

		Assert.That(() => new BlenderLauncher().Open(projectFile, "Assets/Model.blend", null),
			Throws.InvalidOperationException.With.Message.Contains("Preferences"));
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
	public void PreferencesPersistAndValidateBlenderPath()
	{
		var preferencesFile = Path.Combine(_root, "Preferences", "WolfiePreferences.json");
		var blender = Path.Combine(_root, OperatingSystem.IsWindows() ? "blender.exe" : "Blender.app");
		if (OperatingSystem.IsWindows()) File.WriteAllText(blender, string.Empty);
		else Directory.CreateDirectory(blender);
		var preferences = new WolfiePreferences(preferencesFile);

		preferences.SetBlenderPath(blender);

		Assert.That(new WolfiePreferences(preferencesFile).BlenderPath,
			Is.EqualTo(WolfiePath.NormalizeAbsolute(blender)));
		Assert.That(() => preferences.SetBlenderPath(Path.Combine(_root, "MissingBlender")),
			Throws.ArgumentException.With.Message.Contains("existing Blender"));
		Assert.That(preferences.BlenderPath, Is.EqualTo(WolfiePath.NormalizeAbsolute(blender)));

		preferences.SetBlenderPath(null);
		Assert.That(new WolfiePreferences(preferencesFile).BlenderPath, Is.Null);
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
		var browserRoot = new UnityAssetScanner().Scan(project, projectFile, service).Root;
		var browserEntry = browserRoot.Children.Single(entry => entry.RelativePath == "Assets").Children
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
			Assert.That(browserRoot.Children.Select(entry => entry.RelativePath), Is.EqualTo(new[] { "Assets", "Templates" }));
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
