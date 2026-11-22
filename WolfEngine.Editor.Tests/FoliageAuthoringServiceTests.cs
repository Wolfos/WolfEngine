using System.Numerics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;
using WolfEngine.Importing;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class FoliageAuthoringServiceTests
{
	[Test]
	public void Paint_ReachesTargetDensityInsideTheBrushOnTheTerrainSurface()
	{
		using var fixture = new Fixture();
		var foliage = fixture.AttachFoliage();
		var center = new Vector2(16.0f, -12.0f);
		var settings = new FoliageBrushSettings
		{
			RadiusMeters = 10.0f,
			Density = 10.0f,
			MinimumSpacingMeters = 0.0f,
			MinScale = 1.5f,
			MaxScale = 1.5f,
			RandomYaw = false
		};

		var result = fixture.Stamp(center, settings, [new(fixture.Beech)]);
		var surface = TerrainRuntimeRegistry.GetOrCreateRuntime(fixture.Scene.World, fixture.TerrainEntity);

		Assert.That(result.Added, Is.InRange(31, 32));
		Assert.That(foliage.InstanceCount, Is.EqualTo(result.Added));
		foreach (var instance in Instances(foliage))
		{
			var point = new Vector2(instance.Position.X, instance.Position.Z);
			Assert.That(Vector2.Distance(point, center), Is.LessThanOrEqualTo(settings.RadiusMeters + 0.001f));
			Assert.That(surface.TrySampleSurface(Matrix4x4.Identity, instance.Position, out var surfacePoint, out _), Is.True);
			Assert.That(instance.Position.Y, Is.EqualTo(surfacePoint.Y).Within(0.001f));
			Assert.That(instance.Scale, Is.EqualTo(1.5f));
			Assert.That(instance.Rotation, Is.EqualTo(Quaternion.Identity));
		}
	}

	[Test]
	public void Paint_RespectsSpacingAcrossCellBoundariesAndBetweenNewInstances()
	{
		using var fixture = new Fixture();
		var foliage = fixture.AttachFoliage();
		foliage.Types.Add(fixture.Beech);
		foliage.CellSizeMeters = 4.0f;
		foliage.SetInstances([Placed(-0.2f, 0.0f, 0), Placed(5.0f, 0.1f, 0)]);
		var settings = new FoliageBrushSettings { RadiusMeters = 10.0f, Density = 30.0f, MinimumSpacingMeters = 1.75f };

		var result = fixture.Stamp(Vector2.Zero, settings, [new(fixture.Fern)]);
		var positions = Instances(foliage).Select(instance => new Vector2(instance.Position.X, instance.Position.Z)).ToArray();

		Assert.That(result.Added, Is.GreaterThan(30));
		for (var i = 0; i < positions.Length; i++)
		{
			for (var j = i + 1; j < positions.Length; j++)
			{
				Assert.That(Vector2.Distance(positions[i], positions[j]), Is.GreaterThanOrEqualTo(settings.MinimumSpacingMeters));
			}
		}
	}

	[Test]
	public void Erase_UsesEnabledTypesInsideTheBrushAndErasesAllWithNoEnabledTypes()
	{
		using var fixture = new Fixture();
		var foliage = fixture.AttachFoliage();
		foliage.Types.AddRange([fixture.Beech, fixture.Fern]);
		foliage.SetInstances([Placed(-2.0f, 0.0f, 0), Placed(1.0f, 0.0f, 1), Placed(9.0f, 0.0f, 0)]);
		var settings = new FoliageBrushSettings { RadiusMeters = 4.0f };

		var filtered = fixture.Stamp(Vector2.Zero, settings, [new(fixture.Beech), new(fixture.Fern, Enabled: false)], erase: true);

		Assert.That(filtered.Removed, Is.EqualTo(1));
		Assert.That(Instances(foliage), Is.EqualTo(new[] { Placed(1.0f, 0.0f, 1), Placed(9.0f, 0.0f, 0) }));

		var all = fixture.Stamp(Vector2.Zero, settings, [new(fixture.Beech, Enabled: false)], erase: true);

		Assert.That(all.Removed, Is.EqualTo(1));
		Assert.That(Instances(foliage), Is.EqualTo(new[] { Placed(9.0f, 0.0f, 0) }));
	}

	[Test]
	public void UndoRedo_OneEntryRestoresPackedCellsAndTypeListAcrossMultipleStamps()
	{
		using var fixture = new Fixture();
		var foliage = fixture.AttachFoliage();
		foliage.Types.Add(fixture.Beech);
		foliage.SetInstances([Placed(2.0f, 0.0f, 0), Placed(12.0f, 3.0f, 0), Placed(-24.0f, 0.0f, 0)]);
		var before = Pack(foliage);
		var settings = new FoliageBrushSettings { RadiusMeters = 6.0f, Density = 12.0f };

		Assert.That(fixture.Authoring.BeginStroke(fixture.Scene, fixture.TerrainEntity, settings, [new(fixture.Fern)], seed: 33), Is.True);
		fixture.Authoring.AppendStamp(Vector2.Zero, new(false));
		fixture.Authoring.AppendStamp(new Vector2(9.0f, 0.0f), new(false));
		Assert.That(fixture.Authoring.EndStroke(), Is.True);
		var after = Pack(foliage);

		Assert.That(foliage.Types, Is.EqualTo(new[] { fixture.Beech, fixture.Fern }));
		Assert.That(after, Is.Not.EqualTo(before));
		Assert.That(fixture.History.Undo(), Is.True);
		Assert.That(fixture.History.CanUndo, Is.False);
		Assert.That(Pack(foliage), Is.EqualTo(before));
		Assert.That(fixture.History.Redo(), Is.True);
		Assert.That(Pack(foliage), Is.EqualTo(after));
	}

	[Test]
	public void FirstPaint_CreatesAndAssignsAnAssetThatCanBeSavedUndoneAndRedone()
	{
		using var fixture = new Fixture();
		var settings = new FoliageBrushSettings { RadiusMeters = 8.0f, Density = 10.0f };
		var sourcePath = fixture.Project.GetAbsolutePath("Assets/Terrain/Hills Foliage.data.json");
		var metaPath = AssetFileExtensions.GetMetaPath(sourcePath);

		var empty = fixture.Stamp(Vector2.Zero, settings, [], erase: true);
		Assert.That(empty, Is.EqualTo(default(FoliageStampResult)));
		Assert.That(File.Exists(sourcePath), Is.False);
		Assert.That(fixture.History.CanUndo, Is.False);

		var paint = fixture.Stamp(Vector2.Zero, settings, [new(fixture.Beech)]);
		var foliageId = fixture.FoliageReference.NodeId;
		var after = Pack(fixture.FoliageReference.Asset!);

		Assert.That(paint.Added, Is.GreaterThan(0));
		Assert.That(foliageId, Is.Not.EqualTo(Guid.Empty));
		Assert.That(File.Exists(sourcePath), Is.True);
		Assert.That(File.Exists(metaPath), Is.True);
		Assert.That(fixture.InteractionState.IsSceneDirty, Is.True);

		fixture.Workspace.SaveCurrentScene();
		var saved = (TerrainFoliage)fixture.Store.LoadAsset(sourcePath).Asset;
		Assert.That(Pack(saved), Is.EqualTo(after));

		Assert.That(fixture.History.Undo(), Is.True);
		Assert.That(fixture.FoliageReference.IsValid, Is.False);
		Assert.That(File.Exists(sourcePath), Is.False);
		Assert.That(File.Exists(metaPath), Is.False);
		Assert.That(fixture.Project.TryGetAsset(foliageId, out _), Is.False);

		Assert.That(fixture.History.Redo(), Is.True);
		Assert.That(fixture.FoliageReference.NodeId, Is.EqualTo(foliageId));
		Assert.That(Pack(fixture.FoliageReference.Asset!), Is.EqualTo(after));
		Assert.That(File.Exists(metaPath), Is.True);

		fixture.Workspace.SaveCurrentScene();
		Assert.That(Pack((TerrainFoliage)fixture.Store.LoadAsset(sourcePath).Asset), Is.EqualTo(after));
	}

	private static FoliageInstance Placed(float x, float z, int type) =>
		new(new Vector3(x, 0.0f, z), Quaternion.Identity, 1.0f, type);

	private static IEnumerable<FoliageInstance> Instances(TerrainFoliage foliage) =>
		foliage.Cells.SelectMany(cell => Enumerable.Range(0, cell.InstanceCount).Select(cell.GetInstance));

	private static byte[] Pack(TerrainFoliage foliage) =>
		JsonSerializer.SerializeToUtf8Bytes(foliage, AssetJson.GetSerializerOptions(typeof(TerrainFoliage)));

	private sealed class Fixture : IDisposable
	{
		private readonly string _parent = Path.Combine(Path.GetTempPath(), "WolfEngineFoliageTests", Guid.NewGuid().ToString("N"));
		private readonly ServiceProvider _services;
		private readonly IDataAssetCreator _creator;

		public Fixture()
		{
			Directory.CreateDirectory(_parent);
			var metadata = new AssetMetadataStore();
			var pipeline = new ProjectAssetPipelineService(
				new AssetPipelineIndex(),
				metadata,
				Substitute.For<Importing.IImageLoader>(),
				Store,
				new MaterialAssetStore(),
				Substitute.For<IThreeDFileImporter>(),
				Substitute.For<ITextureGpuCompressionService>());
			var services = new ServiceCollection()
				.AddSingleton<ITerrainAssetRuntimeResolver, TerrainAssetRuntimeResolver>();
			TerrainFoliageAssetService? pendingAssets = null;
			services.AddSingleton<IDataAssetRuntimeResolver>(_ => new DataAssetRuntimeResolver(Store, pendingAssets));
			_services = services.BuildServiceProvider();
			var registry = new EditorAssetInstanceRegistry(_services);
			AssetDatabase.SetInstanceRegistry(registry);
			Project = new EditorProjectService(pipeline, registry);
			Assert.That(Project.CreateProject(_parent, "Project", out var error), Is.True, error);
			_creator = new DataAssetCreator(Project, Store, metadata, pipeline);

			var snapshots = Substitute.For<IEditorAssetSnapshotService>();
			snapshots.When(service => service.SaveDataAsset(Arg.Any<AssetDatabaseEntry>(), typeof(TerrainFoliage), Arg.Any<TerrainFoliage>()))
				.Do(call =>
				{
					var entry = call.ArgAt<AssetDatabaseEntry>(0);
					Store.SaveAsset(Project.GetAbsoluteAssetPath(entry.Id, entry.RelativeAssetPath), typeof(TerrainFoliage), call.ArgAt<TerrainFoliage>(2));
				});
			pendingAssets = new TerrainFoliageAssetService(Project, _creator, metadata, snapshots);
			var persistence = new TerrainAssetPersistenceService(
				Project,
				new TerrainTexturePreviewRegistry(),
				Substitute.For<ITerrainBrushGpuExecutor>(),
				pendingAssets);
			Workspace = new EditorSceneWorkspace(
				Substitute.For<IEditorSceneFactory>(), persistence, Substitute.For<IWorldManager>());
			Workspace.Initialize(Scene);
			History = new EditorUndoRedoService(
				Workspace,
				InteractionState,
				Substitute.For<IEditorSceneSnapshotService>(),
				snapshots,
				persistence,
				Substitute.For<IEditorPlaySession>());
			Authoring = new FoliageAuthoringService(pendingAssets, History, InteractionState);

			var terrainPath = Project.GetAbsolutePath("Assets/Terrain/Hills.terrain");
			Directory.CreateDirectory(Path.GetDirectoryName(terrainPath)!);
			TerrainAssetSerializer.Write(terrainPath, TerrainAsset.CreateDefault("Hills"));
			Project.RefreshAssetSource("Assets/Terrain/Hills.terrain");
			Assert.That(pipeline.TryGetPrimaryNodeIdForRelativeSourcePath(Project.ProjectRootPath!, "Assets/Terrain/Hills.terrain", out var terrainId), Is.True);

			TerrainEntity = Scene.World.CreateEntity("Terrain");
			Scene.EntityIds.Add(TerrainEntity, Guid.NewGuid());
			Scene.World.AddComponent(TerrainEntity, new TerrainComponent
			{
				TerrainAsset = new AssetRef<TerrainAsset> { NodeId = terrainId },
				WorldSizeMeters = new Vector2(64.0f),
				HeightScaleMeters = 8.0f
			});

			Beech = new AssetRef<FoliageType> { NodeId = CreateAsset(typeof(FoliageType), "Beech") };
			Fern = new AssetRef<FoliageType> { NodeId = CreateAsset(typeof(FoliageType), "Fern") };
		}

		public EditorProjectService Project { get; }
		public DataAssetStore Store { get; } = new();
		public EditorScene Scene { get; } = new();
		public EditorSceneWorkspace Workspace { get; }
		public EditorInteractionState InteractionState { get; } = new();
		public EditorUndoRedoService History { get; }
		public FoliageAuthoringService Authoring { get; }
		public Entity TerrainEntity { get; }
		public AssetRef<FoliageType> Beech { get; }
		public AssetRef<FoliageType> Fern { get; }
		public AssetRef<TerrainFoliage> FoliageReference => Scene.World.GetComponent<TerrainComponent>(TerrainEntity).Foliage;

		public TerrainFoliage AttachFoliage()
		{
			ref var terrain = ref Scene.World.GetComponent<TerrainComponent>(TerrainEntity);
			terrain.Foliage = new AssetRef<TerrainFoliage> { NodeId = CreateAsset(typeof(TerrainFoliage), "Existing foliage") };
			return terrain.Foliage.Asset!;
		}

		public FoliageStampResult Stamp(Vector2 center, FoliageBrushSettings settings, FoliagePaletteEntry[] palette, bool erase = false)
		{
			Assert.That(Authoring.BeginStroke(Scene, TerrainEntity, settings, palette, seed: 42), Is.True);
			var stamp = Authoring.AppendStamp(center, new(erase));
			Authoring.EndStroke();
			return stamp;
		}

		public void Dispose()
		{
			TerrainRuntimeRegistry.RemoveWorld(Scene.World);
			Project.CloseProject();
			AssetDatabase.ClearInstanceRegistry();
			_services.Dispose();
			Directory.Delete(_parent, recursive: true);
		}

		private Guid CreateAsset(Type type, string name)
		{
			var result = _creator.CreateDataAsset(type, "Assets/Terrain", name);
			Assert.That(result.Success, Is.True, result.ErrorMessage);
			return result.AssetId!.Value;
		}
	}
}
