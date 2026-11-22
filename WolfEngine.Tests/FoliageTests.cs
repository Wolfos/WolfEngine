using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class FoliageTests
{
	private static readonly Vector3 BoundsMin = new(-10.0f, 0.0f, 20.0f);
	private static readonly Vector3 BoundsMax = new(54.0f, 8.0f, 84.0f);

	[Test]
	public void Packing_RoundTripsWithinItsPrecision()
	{
		var rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(1.1f, 0.3f, -0.2f));
		var instance = new FoliageInstance(new Vector3(13.37f, 2.5f, 61.2f), rotation, 1.234f, 7);
		var packed = new byte[FoliageInstancePacking.SizeInBytes];

		FoliageInstancePacking.Pack(instance, BoundsMin, BoundsMax, packed);
		var unpacked = FoliageInstancePacking.Unpack(packed, BoundsMin, BoundsMax);

		var angle = 2.0f * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(rotation, unpacked.Rotation)), 0.0f, 1.0f));
		Assert.Multiple(() =>
		{
			Assert.That(Vector3.Distance(unpacked.Position, instance.Position), Is.LessThan(0.001f), "1 mm across a 64 m cell");
			Assert.That(unpacked.Scale, Is.EqualTo(instance.Scale).Within(0.001f));
			Assert.That(angle, Is.LessThan(0.2f * MathF.PI / 180.0f));
			Assert.That(unpacked.TypeIndex, Is.EqualTo(7));
		});
	}

	[Test]
	public void Packing_KeepsARotationAboutOneAxisAboutThatAxis()
	{
		// Zero must be exactly representable: otherwise every yawed tree comes back tilted, which moves leaves far
		// from the trunk by a pixel or more.
		var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 2.7f);

		var unpacked = FoliageInstancePacking.UnpackQuaternion(FoliageInstancePacking.PackQuaternion(rotation));

		Assert.Multiple(() =>
		{
			Assert.That(unpacked.X, Is.EqualTo(0.0f));
			Assert.That(unpacked.Z, Is.EqualTo(0.0f));
		});
	}

	[Test]
	public void Packing_TreatsANegatedQuaternionAsTheSameRotation()
	{
		var rotation = Quaternion.Normalize(new Quaternion(0.1f, -0.7f, 0.2f, -0.6f));

		var unpacked = FoliageInstancePacking.UnpackQuaternion(FoliageInstancePacking.PackQuaternion(rotation));

		Assert.That(MathF.Abs(Quaternion.Dot(rotation, unpacked)), Is.GreaterThan(0.9999f));
	}

	[Test]
	public void SetInstances_SortsThemIntoCellsOfTheCellSize()
	{
		var foliage = CreateFoliage(typeCount: 2);
		foliage.SetInstances(
		[
			Placed(1.0f, 1.0f, typeIndex: 0),
			Placed(63.0f, 10.0f, typeIndex: 1),
			Placed(65.0f, 1.0f, typeIndex: 0),
			Placed(-1.0f, 1.0f, typeIndex: 1)
		]);

		var cells = foliage.Cells.ToDictionary(cell => (cell.X, cell.Z));
		Assert.Multiple(() =>
		{
			Assert.That(cells.Keys, Is.EquivalentTo(new[] { (0, 0), (1, 0), (-1, 0) }));
			Assert.That(cells[(0, 0)].InstanceCount, Is.EqualTo(2));
			Assert.That(cells[(0, 0)].BoundsMin, Is.EqualTo(new Vector3(1.0f, 0.0f, 1.0f)));
			Assert.That(cells[(0, 0)].BoundsMax, Is.EqualTo(new Vector3(63.0f, 0.0f, 10.0f)));
			Assert.That(cells[(0, 0)].GetInstance(1).TypeIndex, Is.EqualTo(1));
			Assert.That(foliage.InstanceCount, Is.EqualTo(4));
		});
	}

	[Test]
	public void SetInstances_RejectsATypeTheAssetDoesNotList()
	{
		var foliage = CreateFoliage(typeCount: 1);

		Assert.Throws<ArgumentOutOfRangeException>(() => foliage.SetInstances([Placed(0.0f, 0.0f, typeIndex: 1)]));
	}

	[Test]
	public void SetInstances_ChangesTheRevision()
	{
		var foliage = CreateFoliage(typeCount: 1);
		var revision = foliage.Revision;

		foliage.SetInstances([Placed(0.0f, 0.0f, typeIndex: 0)]);

		Assert.That(foliage.Revision, Is.Not.EqualTo(revision), "runtime copies rebuild when the revision changes");
	}

	[Test]
	public void Runtime_SplitsCellsIntoChunks_AndKeepsEveryInstance()
	{
		var foliage = CreateFoliage(typeCount: 1);
		var random = new Random(3);
		var instances = new List<FoliageInstance>();
		for (var i = 0; i < 150; i++)
		{
			instances.Add(Placed(random.NextSingle() * 60.0f, random.NextSingle() * 60.0f, typeIndex: 0));
		}

		instances.Add(Placed(100.0f, 5.0f, typeIndex: 0));
		foliage.SetInstances(instances);

		var runtime = new TerrainFoliageRuntime(foliage, [null]);

		var original = new HashSet<(uint, uint, uint, uint)>();
		foreach (var cell in foliage.Cells)
		{
			var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(cell.Instances);
			for (var i = 0; i < cell.InstanceCount; i++)
			{
				original.Add((words[i * 4], words[(i * 4) + 1], words[(i * 4) + 2], words[(i * 4) + 3]));
			}
		}

		var reordered = new HashSet<(uint, uint, uint, uint)>();
		for (var i = 0; i < runtime.InstanceCount; i++)
		{
			var words = runtime.PackedInstances;
			reordered.Add((words[i * 4], words[(i * 4) + 1], words[(i * 4) + 2], words[(i * 4) + 3]));
		}

		Assert.Multiple(() =>
		{
			Assert.That(runtime.InstanceCount, Is.EqualTo(151));
			Assert.That(runtime.Chunks.Select(chunk => (int)chunk.InstanceCount), Is.EquivalentTo(new[] { 64, 64, 22, 1 }));
			Assert.That(runtime.Chunks.Sum(chunk => (int)chunk.InstanceCount), Is.EqualTo(151));
			Assert.That(reordered, Is.EquivalentTo(original), "reordering within a cell keeps every instance");
			Assert.That(runtime.TypeInstanceCounts, Is.EqualTo(new[] { 0 }), "an unresolved type counts no instances");
			Assert.That(runtime.RendererInstanceCount, Is.Zero);
		});
	}

	[Test]
	public void SubdrawIds_OfAnchorsAndRayTracingInstances_NeverMeet()
	{
		var lastAnchor = FoliageDrawGather.GetAnchorSubdrawId(FoliageInstancePacking.MaxTypeIndex, FoliageTypeRuntime.MaxRendererCount - 1);
		var firstRayTracing = FoliageDrawGather.GetRayTracingSubdrawId(0, 0);
		var largeRayTracing = FoliageDrawGather.GetRayTracingSubdrawId(10_000_000, FoliageTypeRuntime.MaxRendererCount - 1);

		Assert.Multiple(() =>
		{
			Assert.That(firstRayTracing, Is.EqualTo(-1), "terrain chunks use positive ids");
			Assert.That(lastAnchor, Is.LessThan(largeRayTracing));
			Assert.That(FoliageDrawGather.GetRayTracingSubdrawId(5, 1), Is.Not.EqualTo(FoliageDrawGather.GetRayTracingSubdrawId(5, 2)));
		});
	}

	[Test]
	public void Batches_Reserve_SharesTheBatchOfTheSameLaneAndMesh()
	{
		var batches = new GpuDrawBatches(_ => { });
		var mesh = CreateMesh();
		var key = new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.AlphaTest, GpuDrawSidedness.DoubleSided);

		var assigned = batches.Assign(GpuDrawHandle.Create(1, 1), key, mesh);
		var reserved = batches.Reserve(key, mesh, 250);

		Assert.Multiple(() =>
		{
			Assert.That(reserved, Is.EqualTo(assigned));
			Assert.That(batches.GetCapacity((int)reserved), Is.EqualTo(251u));
			Assert.That(batches.TotalCapacity, Is.EqualTo(251u));
		});

		batches.Release(reserved, 250, batches.ClearGeneration);

		Assert.That(batches.GetCapacity((int)reserved), Is.EqualTo(1u));
	}

	[Test]
	public void Batches_ReleaseAfterAClear_IsIgnored()
	{
		var batches = new GpuDrawBatches(_ => { });
		var key = new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.Opaque, GpuDrawSidedness.SingleSided);
		var reserved = batches.Reserve(key, CreateMesh(), 10);
		var generation = batches.ClearGeneration;

		batches.Clear();

		Assert.DoesNotThrow(() => batches.Release(reserved, 10, generation));
		Assert.That(batches.TotalCapacity, Is.Zero);
	}

	[Test]
	public void Batches_ReserveForAlphaBlending_IsRefused()
	{
		var batches = new GpuDrawBatches(_ => { });
		var key = new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.AlphaBlend, GpuDrawSidedness.SingleSided);

		Assert.That(batches.Reserve(key, CreateMesh(), 10), Is.Zero, "blended batches hold one instance each");
	}

	[Test]
	public void Database_RayTracingInstance_StaysUntilRemoved_AndCarriesItsRole()
	{
		var database = new GpuDrawDatabase();
		var mesh = CreateMesh();
		var material = new Material("leaves");
		var entity = new Entity(1, 1);
		var updates = new List<GpuDrawUpdate>();
		var entries = new List<GpuDrawEntry>();

		database.BeginSync();
		database.TouchRayTracingInstance(entity, -1, mesh, material, Matrix4x4.CreateTranslation(3.0f, 0.0f, 0.0f));
		database.EndSync();
		database.ConsumeUpdates(updates);
		Assert.That(updates.Single().Role, Is.EqualTo(GpuDrawRole.RayTracingOnly));

		database.BeginSync();
		database.EndSync();
		database.ConsumeUpdates(updates);
		database.CollectDrawEntries(entries);
		Assert.That(updates, Is.Empty, "an untouched ray-tracing instance stays, so a still camera costs nothing");
		Assert.That(entries.Single().Role, Is.EqualTo(GpuDrawRole.RayTracingOnly));

		database.BeginSync();
		database.RemoveRayTracingInstance(database.CurrentWorldId, entity, -1);
		database.EndSync();
		database.ConsumeUpdates(updates);
		Assert.That(updates.Single().Type, Is.EqualTo(GpuDrawUpdateType.Remove));
	}

	[Test]
	public void Database_ResourceAnchor_IsReleasedWhenNotTouched()
	{
		var database = new GpuDrawDatabase();
		var mesh = CreateMesh();
		var material = new Material("bark");
		var entity = new Entity(1, 1);
		var updates = new List<GpuDrawUpdate>();

		database.BeginSync();
		database.TouchResourceAnchor(entity, int.MinValue, mesh, material);
		database.EndSync();
		database.ConsumeUpdates(updates);
		Assert.That(updates.Single().Role, Is.EqualTo(GpuDrawRole.ResourcesOnly));
		Assert.That(database.TryGetMaterialHandle(material, out _), Is.True);

		database.BeginSync();
		database.EndSync();
		database.ConsumeUpdates(updates);

		Assert.That(updates.Single().Type, Is.EqualTo(GpuDrawUpdateType.Remove));
		Assert.That(database.TryGetMaterialHandle(material, out _), Is.False);
	}

	[Test]
	public void Database_MaterialChange_KeepsGroupMembershipAndRole()
	{
		var database = new GpuDrawDatabase();
		var mesh = CreateMesh();
		var material = new Material("leaves");
		var entity = new Entity(1, 1);
		var updates = new List<GpuDrawUpdate>();
		var grouped = new GpuDrawLod(new Vector4(0.0f, 5.0f, 0.0f, 8.0f), [0.3f], 0, 1);

		database.BeginSync();
		database.TouchPersistentMesh(entity, mesh, material, Matrix4x4.Identity, grouped);
		database.EndSync();
		database.ConsumeUpdates(updates);

		database.NotifyMaterialChanged(material);
		database.ConsumeUpdates(updates);

		Assert.Multiple(() =>
		{
			Assert.That(updates.Single().Type, Is.EqualTo(GpuDrawUpdateType.UpdateMaterial));
			Assert.That(updates.Single().Lod, Is.EqualTo(grouped), "a material update rewrites the instance data, group included");
			Assert.That(updates.Single().Role, Is.EqualTo(GpuDrawRole.Draw));
		});
	}

	[Test]
	public void Database_FoliageSets_LastOneSync()
	{
		var database = new GpuDrawDatabase();
		var runtime = new TerrainFoliageRuntime(CreateFoliage(typeCount: 1), [null]);

		database.BeginSync();
		database.TouchFoliage(new Entity(4, 1), runtime, Matrix4x4.Identity);
		database.EndSync();
		Assert.That(database.FoliageSets.Single().Runtime, Is.SameAs(runtime));

		database.BeginSync();
		database.EndSync();
		Assert.That(database.FoliageSets, Is.Empty);
	}

	private static TerrainFoliage CreateFoliage(int typeCount)
	{
		var foliage = new TerrainFoliage();
		for (var i = 0; i < typeCount; i++)
		{
			foliage.Types.Add(default);
		}

		return foliage;
	}

	private static FoliageInstance Placed(float x, float z, int typeIndex)
	{
		return new FoliageInstance(new Vector3(x, 0.0f, z), Quaternion.Identity, 1.0f, typeIndex);
	}

	private static Mesh CreateMesh() =>
		new(
			[new Vector4(0, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(0, 1, 0, 1)],
			[0, 1, 2]);
}
