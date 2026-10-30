using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using NSubstitute;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;
using WolfEngine.Vfx;

namespace WolfEngine.Editor.Tests;

[NonParallelizable]
public sealed class DataAssetEditorArrayTests
{
	private nint _context;
	private IPropertyDrawerRegistry _drawers = null!;
	private DataAssetEditor _editor = null!;

	[SetUp]
	public void SetUp()
	{
		_context = ImGui.CreateContext();
		var io = ImGui.GetIO();
		io.DisplaySize = new Vector2(800, 800);
		io.DeltaTime = 1.0f / 60.0f;
		io.Fonts.GetTexDataAsRGBA32(out nint pixels, out var width, out var height);
		_drawers = Substitute.For<IPropertyDrawerRegistry>();
		_editor = CreateEditor(_drawers);
	}

	[TearDown]
	public void TearDown()
	{
		ImGui.DestroyContext(_context);
	}

	[Test]
	public void ObjectArrayPropertiesUseBaseThenDerivedDeclarationOrderAndDrawDecorations()
	{
		var seen = new List<(PropertyDrawerContext Context, float Y)>();
		_drawers.Draw(Arg.Any<PropertyDrawerContext>()).Returns(call =>
		{
			var context = call.Arg<PropertyDrawerContext>();
			if (context.ValueType == typeof(DecoratedEntry))
			{
				ImGui.SetNextItemOpen(true, ImGuiCond.Always);
				return new PropertyDrawerResult(false, false, context.Value);
			}

			seen.Add((context, ImGui.GetCursorPosY()));
			ImGui.TextUnformatted(context.Label);

			return new PropertyDrawerResult(true, false, context.Value);
		});

		var result = DrawOpenArray("Entries", typeof(DecoratedEntry[]), new[] { new DecoratedEntry() });

		Assert.Multiple(() =>
		{
			Assert.That(result.Changed, Is.False);
			Assert.That(seen.Select(entry => entry.Context.Label), Is.EqualTo(new[] { "Zebra", "Alpha", "Middle" }));
			Assert.That(seen.All(entry => entry.Context.Member is not null), Is.True);
			Assert.That(seen[2].Y - seen[1].Y, Is.GreaterThan(seen[1].Y - seen[0].Y + 20.0f));
		});
	}

	[Test]
	public void ComponentDecorationsPreserveFieldOrderAndEditsAndSkipHiddenFields()
	{
		var seen = new List<(string Name, float Y)>();
		_drawers.Draw(Arg.Any<PropertyDrawerContext>()).Returns(call =>
		{
			var context = call.Arg<PropertyDrawerContext>();
			seen.Add((context.Label, ImGui.GetCursorPosY()));
			ImGui.TextUnformatted(context.Label);

			return context.Label == nameof(DecoratedComponent.Alpha)
				? new PropertyDrawerResult(true, true, 42)
				: new PropertyDrawerResult(true, false, context.Value);
		});
		object component = new DecoratedComponent();
		var edits = new List<RuntimeComponentFieldEditor.FieldEdit>();

		BeginFrame();
		var changed = RuntimeComponentFieldEditor.ApplyPublicFields(
			typeof(DecoratedComponent),
			_drawers,
			ref component,
			edits: edits);
		EndFrame();

		Assert.Multiple(() =>
		{
			Assert.That(changed, Is.True);
			Assert.That(((DecoratedComponent)component).Alpha, Is.EqualTo(42));
			Assert.That(edits, Has.Count.EqualTo(1));
			Assert.That(edits[0].Path.Single().Name, Is.EqualTo(nameof(DecoratedComponent.Alpha)));
			Assert.That(seen.Select(entry => entry.Name), Is.EqualTo(new[] { "Zebra", "Alpha", "Middle" }));
			Assert.That(seen[2].Y - seen[1].Y, Is.GreaterThan(seen[1].Y - seen[0].Y + 20.0f));
		});
	}

	[Test]
	public void ObjectArrayDrawsEachEntriesPropertiesIncludingAssetReferencesAndEnums()
	{
		var factionId = Guid.NewGuid();
		var players = new[]
		{
			new PlayerEntry { Id = 1, Faction = new AssetRef<ArrayAsset> { NodeId = factionId } },
			new PlayerEntry { Id = 2, Controller = Controller.Computer }
		};
		_drawers.Draw(Arg.Any<PropertyDrawerContext>()).Returns(call =>
		{
			var context = call.Arg<PropertyDrawerContext>();
			if (context.ValueType == typeof(PlayerEntry))
			{
				ImGui.SetNextItemOpen(true, ImGuiCond.Always);
				return new PropertyDrawerResult(false, false, context.Value);
			}

			return new PropertyDrawerResult(true, false, context.Value);
		});

		var result = DrawOpenArray("Players", typeof(PlayerEntry[]), players);

		Assert.That(result.Changed, Is.False);
		_drawers.Received().Draw(Arg.Is<PropertyDrawerContext>(context =>
			context.Label == nameof(PlayerEntry.Id) && Equals(context.Value, 2)));
		_drawers.Received().Draw(Arg.Is<PropertyDrawerContext>(context =>
			context.ValueType == typeof(Controller) && Equals(context.Value, Controller.Computer)));
		_drawers.Received().Draw(Arg.Is<PropertyDrawerContext>(context =>
			context.ValueType == typeof(AssetRef<ArrayAsset>) &&
			((AssetRef<ArrayAsset>)context.Value!).NodeId == factionId));
	}

	[Test]
	public void StructElementEditsAreWrittenBackIntoTheArray()
	{
		var entries = new[] { new StructEntry { Id = 3 } };
		_drawers.Draw(Arg.Any<PropertyDrawerContext>()).Returns(call =>
		{
			var context = call.Arg<PropertyDrawerContext>();
			if (context.ValueType == typeof(StructEntry))
			{
				ImGui.SetNextItemOpen(true, ImGuiCond.Always);
				return new PropertyDrawerResult(false, false, context.Value);
			}

			return new PropertyDrawerResult(true, true, 7);
		});

		var result = DrawOpenArray("Entries", typeof(StructEntry[]), entries);

		Assert.That(result.Changed, Is.True);
		Assert.That(entries[0].Id, Is.EqualTo(7));
	}

	[Test]
	public void JaggedArraysUseTheElementDrawerRecursively()
	{
		var entries = new[] { new[] { 3 } };
		_drawers.Draw(Arg.Any<PropertyDrawerContext>()).Returns(new PropertyDrawerResult(true, true, 9));

		BeginFrame();
		ImGui.PushID(0);
		ImGui.GetStateStorage().SetInt(ImGui.GetID("###Array"), 1);
		ImGui.PopID();
		ImGui.SetNextItemOpen(true, ImGuiCond.Always);
		var result = _editor.DrawValue("Entries", typeof(int[][]), entries);
		EndFrame();

		Assert.That(result.Changed, Is.True);
		Assert.That(entries[0][0], Is.EqualTo(9));
	}

	[Test]
	public void OpeningNullAndEmptyArraysDoesNotMutateThem()
	{
		var nullResult = DrawOpenArray("Entries", typeof(PlayerEntry[]), null);
		var empty = Array.Empty<PlayerEntry>();
		var emptyResult = DrawOpenArray("Entries", typeof(PlayerEntry[]), empty);

		Assert.Multiple(() =>
		{
			Assert.That(nullResult.Changed, Is.False);
			Assert.That(nullResult.Value, Is.Null);
			Assert.That(emptyResult.Changed, Is.False);
			Assert.That(emptyResult.Value, Is.SameAs(empty));
		});
	}

	[Test]
	public void OpeningNullObjectEntryDoesNotInstantiateIt()
	{
		PlayerEntry?[] entries = [null];
		_drawers.Draw(Arg.Any<PropertyDrawerContext>()).Returns(call =>
		{
			ImGui.SetNextItemOpen(true, ImGuiCond.Always);
			return new PropertyDrawerResult(false, false, call.Arg<PropertyDrawerContext>().Value);
		});

		var result = DrawOpenArray("Entries", typeof(PlayerEntry[]), entries);

		Assert.That(result.Changed, Is.False);
		Assert.That(entries[0], Is.Null);
	}

	[Test]
	public void AddControlCreatesAnObjectEntryWithItsPropertyDefaults()
	{
		DrawOpenArray("Entries", typeof(PlayerEntry[]), null);
		BeginFrame();
		ImGui.SetNextItemOpen(true, ImGuiCond.Always);
		_editor.DrawValue("Entries", typeof(PlayerEntry[]), null);
		var buttonCenter = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) / 2;
		EndFrame();

		ImGui.GetIO().AddMousePosEvent(buttonCenter.X, buttonCenter.Y);
		ImGui.GetIO().AddMouseButtonEvent(0, true);
		DrawOpenArray("Entries", typeof(PlayerEntry[]), null);
		ImGui.GetIO().AddMouseButtonEvent(0, false);
		var result = DrawOpenArray("Entries", typeof(PlayerEntry[]), null);

		Assert.That(result.Changed, Is.True);
		var entries = (PlayerEntry[])result.Value!;
		Assert.That(entries, Has.Length.EqualTo(1));
		Assert.That(entries[0].DisplayName, Is.EqualTo("New player"));
	}

	[Test]
	public void ArrayEditsSaveThroughTheInspectorAndCanBeUndoneAndRedone()
	{
		var directory = Path.Combine(Path.GetTempPath(), "WolfEngineArrayTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try
		{
			var path = Path.Combine(directory, "Match.data.json");
			var store = new DataAssetStore();
			store.SaveAsset(path, typeof(ArrayAsset), new ArrayAsset { Players = [new PlayerEntry { Id = 1 }] });
			var asset = new AssetDatabaseEntry
			{
				Id = Guid.NewGuid(),
				Type = AssetType.DataAsset,
				RelativeAssetPath = "Assets/Match.data.json",
				RelativeSourcePath = "Assets/Match.data.json"
			};
			var project = Substitute.For<IEditorProjectService>();
			project.GetAbsoluteAssetPath(asset.Id, asset.RelativeAssetPath).Returns(path);
			var snapshots = Substitute.For<IEditorAssetSnapshotService>();
			snapshots.CaptureDataAssetSnapshot(asset).Returns(_ =>
				new EditorAssetFileSnapshot(asset.Id, asset.RelativeAssetPath, asset.RelativeSourcePath, File.ReadAllText(path)));
			snapshots.CaptureDataAssetSnapshot(asset, typeof(ArrayAsset), Arg.Any<IDataAsset>()).Returns(call =>
			{
				var file = JsonSerializer.Deserialize<DataAssetFile>(File.ReadAllText(path), AssetJson.SerializerOptions)!;
				file.Data = JsonSerializer.SerializeToElement(call.Arg<IDataAsset>(), typeof(ArrayAsset), AssetJson.SerializerOptions);

				return new EditorAssetFileSnapshot(asset.Id, asset.RelativeAssetPath, asset.RelativeSourcePath,
					JsonSerializer.Serialize(file, AssetJson.SerializerOptions));
			});
			snapshots.When(service => service.SaveDataAsset(asset, typeof(ArrayAsset), Arg.Any<IDataAsset>()))
				.Do(call => store.SaveAsset(path, typeof(ArrayAsset), call.Arg<IDataAsset>()));
			snapshots.When(service => service.ApplyDataAssetSnapshot(Arg.Any<EditorAssetFileSnapshot>()))
				.Do(call => File.WriteAllText(path, call.Arg<EditorAssetFileSnapshot>().Json));
			var undoRedo = new EditorUndoRedoService(
				Substitute.For<IEditorSceneWorkspace>(),
				new EditorInteractionState(),
				Substitute.For<IEditorSceneSnapshotService>(),
				snapshots,
				Substitute.For<ITerrainAssetPersistenceService>(),
				Substitute.For<IEditorPlaySession>());
			_editor = new DataAssetEditor(project, store, _drawers, snapshots, undoRedo,
				Substitute.For<IIconManager>(), new AssetSelectionService(), CreateVfxEditor(_drawers));
			_drawers.Draw(Arg.Any<PropertyDrawerContext>()).Returns(call =>
			{
				var context = call.Arg<PropertyDrawerContext>();
				if (context.ValueType == typeof(PlayerEntry))
				{
					ImGui.SetNextItemOpen(true, ImGuiCond.Always);
					return new PropertyDrawerResult(false, false, context.Value);
				}

				return context.Label == nameof(PlayerEntry.Id)
					? new PropertyDrawerResult(true, true, 42)
					: new PropertyDrawerResult(true, false, context.Value);
			});

			BeginFrame();
			ImGui.PushID(nameof(ArrayAsset.Players));
			ImGui.GetStateStorage().SetInt(ImGui.GetID("###Array"), 1);
			ImGui.PopID();
			_editor.Draw(asset);
			EndFrame();

			Assert.That(((ArrayAsset)store.LoadAsset(path).Asset).Players[0].Id, Is.EqualTo(42));
			Assert.That(undoRedo.Undo(), Is.True);
			Assert.That(((ArrayAsset)store.LoadAsset(path).Asset).Players[0].Id, Is.EqualTo(1));
			Assert.That(undoRedo.Redo(), Is.True);
			Assert.That(((ArrayAsset)store.LoadAsset(path).Asset).Players[0].Id, Is.EqualTo(42));
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Test]
	public void RemovalPreservesRemainingEntriesAndTheirOrder()
	{
		var first = new PlayerEntry { Id = 1 };
		var last = new PlayerEntry { Id = 3 };
		var source = new[] { first, new PlayerEntry { Id = 2 }, last };

		var result = (PlayerEntry[])DataAssetArrayEditing.RemoveElement(source, 1);

		Assert.That(result, Is.EqualTo(new[] { first, last }));
		Assert.That(source, Has.Length.EqualTo(3));
	}

	[Test]
	public void AddingElementsSupportsStringsStructsAndJaggedArrays()
	{
		var strings = (string[])DataAssetArrayEditing.AddElement(new[] { "Existing" }, typeof(string));
		var structs = (StructEntry[])DataAssetArrayEditing.AddElement(null, typeof(StructEntry));
		var arrays = (int[][])DataAssetArrayEditing.AddElement(null, typeof(int[]));

		Assert.Multiple(() =>
		{
			Assert.That(strings, Is.EqualTo(new[] { "Existing", string.Empty }));
			Assert.That(structs, Has.Length.EqualTo(1));
			Assert.That(structs[0].Id, Is.Zero);
			Assert.That(arrays, Has.Length.EqualTo(1));
			Assert.That(arrays[0], Is.Empty);
			Assert.That(DataAssetArrayEditing.CanCreateElement(typeof(IDataAsset)), Is.False);
		});
	}

	[Test]
	public void MultidimensionalArraysAreNotRecursedAsObjects()
	{
		var values = new int[2, 3];

		var result = DrawOpenArray("Entries", typeof(int[,]), values);

		Assert.That(result.Changed, Is.False);
		Assert.That(result.Value, Is.SameAs(values));
		_drawers.DidNotReceive().Draw(Arg.Any<PropertyDrawerContext>());
	}

	private PropertyDrawerResult DrawOpenArray(string label, Type type, object? value)
	{
		BeginFrame();
		ImGui.SetNextItemOpen(true, ImGuiCond.Always);
		var result = _editor.DrawValue(label, type, value);
		EndFrame();

		return result;
	}

	private static void BeginFrame()
	{
		ImGui.NewFrame();
		ImGui.SetNextWindowPos(new Vector2(0, 0));
		ImGui.SetNextWindowSize(new Vector2(800, 800));
		ImGui.Begin("Array tests", ImGuiWindowFlags.NoSavedSettings);
	}

	private static void EndFrame()
	{
		ImGui.End();
		ImGui.Render();
	}

	private static DataAssetEditor CreateEditor(IPropertyDrawerRegistry drawers)
	{
		return new DataAssetEditor(
			Substitute.For<IEditorProjectService>(),
			Substitute.For<IDataAssetStore>(),
			drawers,
			Substitute.For<IEditorAssetSnapshotService>(),
			Substitute.For<IEditorUndoRedoService>(),
			Substitute.For<IIconManager>(),
			new AssetSelectionService(),
			CreateVfxEditor(drawers));
	}

	private static VfxAssetEditor CreateVfxEditor(IPropertyDrawerRegistry drawers)
	{
		return new VfxAssetEditor(
			Substitute.For<IRenderViewHost>(),
			new EditorRenderViews(),
			new EditorViewportStateBus(),
			Substitute.For<IVfxService>(),
			Substitute.For<IProjectAssetPipelineService>(),
			Substitute.For<IEditorProjectService>(),
			drawers);
	}

	public sealed class ArrayAsset : IDataAsset
	{
		public PlayerEntry[] Players { get; set; } = [];
	}

	public class DecoratedEntryBase
	{
		public int Zebra { get; set; }
	}

	public sealed class DecoratedEntry : DecoratedEntryBase
	{
		public int Alpha { get; set; }

		[Header("Details")]
		[Space(24)]
		public int Middle { get; set; }
	}

	public struct DecoratedComponent : IEntityComponent
	{
		public int Zebra;
		public int Alpha;

		[HideFromEditor]
		[Space(1000)]
		public int Hidden;

		[Header("Details")]
		[Space(24)]
		public int Middle;
	}

	public sealed class PlayerEntry
	{
		public int Id { get; set; }
		public string DisplayName { get; set; } = "New player";
		public AssetRef<ArrayAsset> Faction { get; set; }
		public Controller Controller { get; set; }
	}

	public struct StructEntry
	{
		public int Id { get; set; }
	}

	public enum Controller
	{
		Human,
		Computer
	}
}
