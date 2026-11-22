using System.Numerics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;
using WolfEngine.Editor.UI;
using WolfEngine.Importing;
using WolfEngine.Rendering;

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

	[Test]
	public void Sculpt_MovesFoliageUnderTheBrushWithTheGroundInOneUndoStep()
	{
		using var fixture = new Fixture();
		var foliage = fixture.AttachFoliage();
		foliage.Types.Add(fixture.Beech);
		var onGround = Placed(0.0f, 0.0f, 0);
		var sunk = Placed(0.1f, 0.05f, 0) with { Position = new Vector3(0.1f, -0.5f, 0.05f) };
		var outside = Placed(24.0f, 24.0f, 0);
		foliage.SetInstances([onGround, sunk, outside]);
		var before = Pack(foliage);
		var terrainAsset = fixture.TerrainAsset;
		var heightsBefore = terrainAsset.Heightmap.MipLevels[0].Data.ToArray();
		var sculpt = fixture.CreateTerrainAuthoring();

		Assert.That(
			sculpt.BeginStroke(
				fixture.Scene,
				fixture.TerrainEntity,
				new TerrainBrushStrokeRequest(
					TerrainAuthoringSurfaceTarget.Heightmap,
					TerrainBrushOperation.RaiseLower,
					new TerrainBrushSettings(4.0f, 1.0f, 1.0f, 0, null))),
			Is.True);
		sculpt.AppendStamp(Vector3.Zero, 1.0f, new TerrainBrushModifierState(false));
		Assert.That(sculpt.EndStroke(), Is.True);

		ref var terrain = ref fixture.Scene.World.GetComponent<TerrainComponent>(fixture.TerrainEntity);
		var surface = TerrainRuntimeRegistry.GetOrCreateRuntime(fixture.Scene.World, fixture.TerrainEntity);
		Assert.That(surface.EnsureSampling(terrain), Is.True);
		var moved = Instances(foliage).ToArray();
		Assert.That(surface.TrySampleLocalSurface(0.0f, 0.0f, out var centre, out _), Is.True);
		Assert.That(surface.TrySampleLocalSurface(0.1f, 0.05f, out var nearCentre, out _), Is.True);
		Assert.That(centre.Y, Is.GreaterThan(0.1f));
		Assert.That(moved[0].Position.Y, Is.EqualTo(centre.Y).Within(0.001f));
		Assert.That(moved[1].Position.Y, Is.EqualTo(nearCentre.Y - 0.5f).Within(0.001f));
		// The outside instance shares the cell, which is quantised again, so it may drift by a fraction of a millimetre.
		Assert.That(Vector3.Distance(moved[2].Position, outside.Position), Is.LessThan(0.0001f));

		Assert.That(fixture.History.Undo(), Is.True);
		Assert.That(fixture.History.CanUndo, Is.False);
		Assert.That(terrainAsset.Heightmap.MipLevels[0].Data, Is.EqualTo(heightsBefore));
		Assert.That(Pack(foliage), Is.EqualTo(before));

		Assert.That(fixture.History.Redo(), Is.True);
		Assert.That(Instances(foliage).Select(instance => instance.Position.Y), Is.EqualTo(moved.Select(instance => instance.Position.Y)));
	}

	[Test]
	public void SnapToTerrain_PutsInstancesOnTheSurfaceAndUndoRestoresThem()
	{
		using var fixture = new Fixture();
		var foliage = fixture.AttachFoliage();
		foliage.Types.Add(fixture.Beech);
		foliage.SetInstances([Placed(-20.0f, 3.0f, 0), Placed(5.0f, -11.0f, 0), Placed(18.0f, 22.0f, 0)]);
		var before = Pack(foliage);
		fixture.SetSlopedHeightmap();

		var moved = fixture.Authoring.SnapToTerrain(fixture.Scene, fixture.TerrainEntity);

		ref var terrain = ref fixture.Scene.World.GetComponent<TerrainComponent>(fixture.TerrainEntity);
		var surface = TerrainRuntimeRegistry.GetOrCreateRuntime(fixture.Scene.World, fixture.TerrainEntity);
		Assert.That(surface.EnsureSampling(terrain), Is.True);
		Assert.That(moved, Is.EqualTo(3));
		foreach (var instance in Instances(foliage))
		{
			Assert.That(surface.TrySampleLocalSurface(instance.Position.X, instance.Position.Z, out var point, out _), Is.True);
			Assert.That(instance.Position.Y, Is.EqualTo(point.Y).Within(0.001f));
		}

		Assert.That(fixture.Authoring.SnapToTerrain(fixture.Scene, fixture.TerrainEntity), Is.Zero);
		Assert.That(fixture.History.Undo(), Is.True);
		Assert.That(Pack(foliage), Is.EqualTo(before));
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
			Persistence = new TerrainAssetPersistenceService(
				Project,
				TexturePreviews,
				GpuExecutor,
				pendingAssets);
			var persistence = Persistence;
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
		public TerrainAssetPersistenceService Persistence { get; }
		public TerrainTexturePreviewRegistry TexturePreviews { get; } = new();
		public TerrainAuthoringServiceTests.TestTerrainBrushGpuExecutor GpuExecutor { get; } = new();
		public TerrainAsset TerrainAsset => Scene.World.GetComponent<TerrainComponent>(TerrainEntity).TerrainAsset.Asset!;

		public TerrainAuthoringService CreateTerrainAuthoring()
		{
			return new TerrainAuthoringService(
				History,
				InteractionState,
				Persistence,
				TexturePreviews,
				GpuExecutor,
				Authoring);
		}

		/// <summary>Replaces the heightmap with a ramp rising along X, as if it had been sculpted.</summary>
		public void SetSlopedHeightmap()
		{
			var heightmap = TerrainAsset.Heightmap;
			var data = new byte[heightmap.Width * heightmap.Height * 2];
			for (var y = 0; y < heightmap.Height; y++)
			{
				for (var x = 0; x < heightmap.Width; x++)
				{
					var encoded = (ushort)(x * 65535 / (heightmap.Width - 1));
					var offset = ((y * heightmap.Width) + x) * 2;
					data[offset] = (byte)(encoded & 0xff);
					data[offset + 1] = (byte)(encoded >> 8);
				}
			}

			heightmap.ApplyTextureData(
				heightmap.Width,
				heightmap.Height,
				false,
				TextureFormat.R16Unorm,
				[new TextureMipData(heightmap.Width, heightmap.Height, data)]);
		}
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
