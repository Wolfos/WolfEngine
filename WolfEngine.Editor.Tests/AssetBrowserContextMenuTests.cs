using System.Numerics;
using ImGuiNET;
using NSubstitute;
using WolfEngine.AssetPipeline;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;
using WolfEngine.Utility;

namespace WolfEngine.Editor.Tests;

[NonParallelizable]
public sealed class AssetBrowserContextMenuTests
{
	private const string ModelPath = "Assets/Rig_Large/Rig_Large_Combatmelee.fbx";
	private nint _context;
	private string _root = string.Empty;
	private AssetsWindow _window = null!;
	private readonly EditorScene _scene = new();
	private Guid _sourceId;

	[SetUp]
	public void SetUp()
	{
		_context = ImGui.CreateContext();
		var io = ImGui.GetIO();
		io.DisplaySize = new Vector2(1200.0f, 900.0f);
		io.DeltaTime = 1.0f / 60.0f;
		io.Fonts.GetTexDataAsRGBA32(out nint pixels, out var width, out var height);
		ImGui.LoadIniSettingsFromMemory("[Window][Assets]\nPos=0,0\nSize=1180,860\nCollapsed=0\n");
		_root = Path.Combine(Path.GetTempPath(), "AssetBrowserContextMenuTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Path.Combine(_root, "Assets/Rig_Large/Inside"));
		Directory.CreateDirectory(Path.Combine(_root, "Assets/Rig_Small"));
		_sourceId = Guid.NewGuid();
		var model = new AssetDatabaseEntry
		{
			Id = Guid.NewGuid(),
			SourceId = _sourceId,
			Type = AssetType.Model3D,
			Name = "Rig_Large_Combatmelee",
			NodeKey = "main",
			RelativeSourcePath = ModelPath,
			RelativeAssetPath = ModelPath
		};
		var clip = new AssetDatabaseEntry
		{
			Id = Guid.NewGuid(),
			SourceId = _sourceId,
			Type = AssetType.AnimationClip,
			Name = "Attack",
			NodeKey = "attack",
			IsGenerated = true,
			RelativeSourcePath = ModelPath,
			RelativeAssetPath = ModelPath
		};
		var database = new AssetDatabase
		{
			Assets = [model, clip]
		};
		var project = Substitute.For<IEditorProjectService>();
		project.HasOpenProject.Returns(true);
		project.ProjectRootPath.Returns(_root);
		project.AssetsPath.Returns(Path.Combine(_root, "Assets"));
		project.CurrentAssetDatabase.Returns(database);
		project.CurrentAssetCatalog.Returns(new AssetCatalog(
			[new DirectoryAssetMount("project", "Project", _root, false, database)]));
		project.TryGetAsset(Arg.Any<Guid>(), out Arg.Any<AssetDatabaseEntry>()).Returns(call =>
		{
			var asset = database.Assets.FirstOrDefault(candidate => candidate.Id == call.ArgAt<Guid>(0));
			call[1] = asset;

			return asset is not null;
		});
		var files = Substitute.For<IFileManagerService>();
		files.FileManagerName.Returns("Finder");
		_window = new AssetsWindow(
			project,
			Substitute.For<IProjectAssetPipelineService>(),
			Substitute.For<IAssetThumbnailLoader>(),
			new AssetSelectionService(),
			Substitute.For<IEditorAssetHandlerRegistry>(),
			Substitute.For<IIconManager>(),
			new EditorInteractionState(),
			Substitute.For<IEditorCommandService>(),
			files);
		_window.SetSelectedFolderForTesting("Assets/Rig_Large");
		DrawFrame();
		DrawFrame();
	}

	[TearDown]
	public void TearDown()
	{
		ImGui.DestroyContext(_context);
		Directory.Delete(_root, true);
	}

	[TestCase("Source", "Source", ModelPath, "Rig_Large_Combatmelee")]
	[TestCase("Folder", "Folder", "Assets/Rig_Large/Inside", "Inside")]
	[TestCase("Tree", "Folder", "Assets/Rig_Small", "Rig_Small")]
	[TestCase("Background", "Folder", "Assets/Rig_Large", "Rig_Large")]
	[TestCase("SubAsset", "Source", ModelPath, "Rig_Large_Combatmelee")]
	public void RightClickReleaseKeepsTheClickedTargetForRename(
		string clickedKind,
		string renameKind,
		string expectedPath,
		string expectedName)
	{
		if (clickedKind == "SubAsset")
		{
			_window.ConfigureBrowser("Assets/Rig_Large", expandedSourceId: _sourceId);
			DrawFrame();
			DrawFrame();
		}

		var state = _window.GetBrowserState();
		var rect = clickedKind switch
		{
			"Tree" => state.Controls["tree:Assets/Rig_Small"],
			"Background" => state.Controls["grid"],
			_ => state.Items.First(item => item.Kind == clickedKind).Bounds
		};
		var position = clickedKind == "Background"
			? new Vector2(rect.X + rect.Width - 12.0f, rect.Y + rect.Height - 12.0f)
			: new Vector2(rect.X + 40.0f, rect.Y + 12.0f);
		ImGui.GetIO().MousePos = position;
		DrawFrame();
		ImGui.GetIO().MouseDown[1] = true;
		DrawFrame();
		Assert.That(_window.GetBrowserState().ContextMenu, Is.Not.Null);

		ImGui.GetIO().MouseDown[1] = false;
		for (var frame = 0; frame < 30; frame++)
		{
			DrawFrame();
		}

		state = _window.GetBrowserState();
		Assert.That(state.ContextMenu?.Path, Is.EqualTo(expectedPath));
		var rename = state.Controls["context:rename"];
		ImGui.GetIO().MousePos = new Vector2(rename.X + rename.Width * 0.5f, rename.Y + rename.Height * 0.5f);
		DrawFrame();
		ImGui.GetIO().MouseDown[0] = true;
		DrawFrame();
		ImGui.GetIO().MouseDown[0] = false;
		DrawFrame();
		DrawFrame();

		var dialog = _window.GetBrowserState().RenameDialog;
		Assert.That(dialog, Is.Not.Null);
		Assert.Multiple(() =>
		{
			Assert.That(dialog!.Kind, Is.EqualTo(renameKind));
			Assert.That(dialog.Path, Is.EqualTo(expectedPath));
			Assert.That(dialog.Name, Is.EqualTo(expectedName));
		});
	}

	private void DrawFrame()
	{
		ImGui.NewFrame();
		_window.Draw(_scene);
		ImGui.Render();
	}
}
