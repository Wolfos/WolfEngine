using Microsoft.Data.Sqlite;
using NSubstitute;
using WolfEngine.AssetPipeline;
using WolfEngine.Dialogue;
using WolfEngine.Editor.Projects;
using WolfEngine.Importing;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class YarnProjectImportTests
{
	private const string ProjectFile = """
		{
		  "projectFileVersion": 3,
		  "sourceFiles": ["**/*.yarn"],
		  "excludeFiles": [],
		  "baseLanguage": "en",
		  "definitions": "Dialogue.ysls.json",
		  "localisation": {}
		}
		""";

	private const string Definitions = """
		{
		  "functions": [
		    { "yarnName": "has_item", "parameters": [ { "name": "itemId", "type": "string" } ], "return": { "type": "bool" } }
		  ],
		  "commands": []
		}
		""";

	private string _projectRoot = string.Empty;
	private string _dialogueFolder = string.Empty;
	private ProjectAssetPipelineService _pipeline = null!;

	[SetUp]
	public void SetUp()
	{
		_projectRoot = Path.Combine(Path.GetTempPath(), "WolfEngineYarnImportTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_projectRoot);
		_pipeline = new ProjectAssetPipelineService(
			new AssetPipelineIndex(),
			new AssetMetadataStore(),
			Substitute.For<IImageLoader>(),
			new DataAssetStore(),
			new MaterialAssetStore(),
			Substitute.For<IThreeDFileImporter>(),
			Substitute.For<ITextureGpuCompressionService>(),
			new ProjectTypeCatalog(() => throw new InvalidOperationException("No gameplay types are needed.")));
		_pipeline.InitializeProject(_projectRoot);
		_dialogueFolder = Path.Combine(AssetPipelinePaths.GetAssetsPath(_projectRoot), "Dialogue");
		Directory.CreateDirectory(_dialogueFolder);
		File.WriteAllText(Path.Combine(_dialogueFolder, "Dialogue.yarnproject"), ProjectFile);
		File.WriteAllText(Path.Combine(_dialogueFolder, "Dialogue.ysls.json"), Definitions);
	}

	[TearDown]
	public void TearDown()
	{
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_projectRoot)) Directory.Delete(_projectRoot, recursive: true);
	}

	[Test]
	public void RebuildCompilesEveryYarnFileIntoOneProject()
	{
		WriteYarn("Blacksmith.yarn", "title: Blacksmith\n---\nBlacksmith: Welcome.\n<<jump Guard>>\n===");
		WriteYarn("Town/Guard.yarn", "title: Guard\n---\n<<if has_item(\"small_key\")>>\nGuard: A key!\n<<endif>>\nGuard: Halt.\n===");

		var database = _pipeline.RebuildProject(_projectRoot);

		var asset = database.Assets.Single(entry => entry.Type == AssetType.YarnProject);
		Assert.That(asset.Name, Is.EqualTo("Dialogue"));
		Assert.That(asset.TryGetSummary<YarnProjectSummary>(out var summary), Is.True);
		Assert.That(summary!.NodeNames, Is.EqualTo(new[] { "Blacksmith", "Guard" }));
		Assert.That(summary.SourceFiles, Has.Length.EqualTo(2));

		using var runner = new DialogueRunner();
		runner.AddFunction("has_item", (string _) => false);
		runner.Start(Resolve(asset), "Blacksmith");
		Assert.That(runner.CurrentLine!.Text, Is.EqualTo("Welcome."));
		runner.Advance();
		Assert.That(runner.CurrentLine!.Speaker, Is.EqualTo("Guard"));
		Assert.That(runner.CurrentLine.Text, Is.EqualTo("Halt."));
	}

	[Test]
	public void EachYarnFileIsListedWithTheNodesItDeclares()
	{
		WriteYarn("Blacksmith.yarn", "title: Blacksmith\n---\nHi.\n===\n\ntitle: BlacksmithMenu\ntags: menu\n---\ntitle: not a header\n===");
		WriteYarn("Town/Guard.yarn", "title: Guard\n---\nHalt.\n===");

		var database = _pipeline.RebuildProject(_projectRoot);

		var scripts = database.Assets.Where(entry => entry.Type == AssetType.YarnScript)
			.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToArray();
		Assert.That(scripts.Select(entry => entry.RelativeSourcePath),
			Is.EqualTo(new[] { "Assets/Dialogue/Blacksmith.yarn", "Assets/Dialogue/Town/Guard.yarn" }));
		Assert.That(scripts[0].TryGetSummary<YarnScriptSummary>(out var summary), Is.True);
		Assert.That(summary!.NodeNames, Is.EqualTo(new[] { "Blacksmith", "BlacksmithMenu" }));
		Assert.That(scripts[0].Artifacts, Is.Empty);
	}

	[Test]
	public void EditingOnlyAYarnFileReimportsTheProject()
	{
		WriteYarn("Blacksmith.yarn", "title: Blacksmith\n---\nBlacksmith: Welcome.\n===");
		_pipeline.RebuildProject(_projectRoot);

		var unchanged = _pipeline.RefreshProjectIncrementalWithChanges(_projectRoot);
		Assert.That(unchanged.ReimportedNodeIds, Is.Empty);

		WriteYarn("Blacksmith.yarn", "title: Blacksmith\n---\nBlacksmith: Back again?\n===");
		var refresh = _pipeline.RefreshProjectIncrementalWithChanges(_projectRoot);

		var asset = refresh.Database.Assets.Single(entry => entry.Type == AssetType.YarnProject);
		Assert.That(refresh.ReimportedNodeIds, Does.Contain(asset.Id));
		Assert.That(Resolve(asset).Lines.Values, Does.Contain("Blacksmith: Back again?"));
	}

	[Test]
	public void AddingAYarnFileReimportsTheProject()
	{
		WriteYarn("Blacksmith.yarn", "title: Blacksmith\n---\nBlacksmith: Welcome.\n===");
		_pipeline.RebuildProject(_projectRoot);

		WriteYarn("Guard.yarn", "title: Guard\n---\nGuard: Halt.\n===");
		var refresh = _pipeline.RefreshProjectIncrementalWithChanges(_projectRoot);

		var asset = refresh.Database.Assets.Single(entry => entry.Type == AssetType.YarnProject);
		Assert.That(asset.TryGetSummary<YarnProjectSummary>(out var summary), Is.True);
		Assert.That(summary!.NodeNames, Does.Contain("Guard"));
	}

	[Test]
	public void CompileErrorsNameTheFileAndLine()
	{
		WriteYarn("Broken.yarn", "title: Broken\n---\n<<if mystery()>>\nHi\n<<endif>>\n===");

		var exception = Assert.Throws<InvalidDataException>(() => YarnProjectCompiler.Compile(
			_projectRoot,
			Path.Combine(_dialogueFolder, "Dialogue.yarnproject"),
			Path.Combine(_projectRoot, "out.wolfyarn")));

		Assert.That(exception!.Message, Does.Contain("Assets/Dialogue/Broken.yarn:3"));
		Assert.That(exception.Message, Does.Contain("mystery"));
	}

	[Test]
	public void ProjectThatFailsToCompileIsSkippedWithoutBreakingTheRefresh()
	{
		WriteYarn("Broken.yarn", "title: Broken\n---\n<<if mystery()>>\nHi\n<<endif>>\n===");

		var database = _pipeline.RebuildProject(_projectRoot);

		Assert.That(database.Assets.Any(entry => entry.Type == AssetType.YarnProject), Is.False);
	}

	private void WriteYarn(string relativePath, string source)
	{
		var path = Path.Combine(_dialogueFolder, relativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, source);
	}

	private YarnProject Resolve(AssetDatabaseEntry asset) => (YarnProject)new YarnProjectRuntimeResolver().Resolve(
		new RuntimeAssetResolveContext(asset.Id, asset, typeof(YarnProject), _projectRoot, static (_, _) => null));
}
