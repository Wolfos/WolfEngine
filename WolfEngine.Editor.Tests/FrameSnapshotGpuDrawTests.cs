using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using WolfEngine;
using WolfEngine.ECS;
using WolfEngine.Rendering;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class FrameSnapshotGpuDrawTests
{
	[Test]
	public void FrameSnapshotBuffer_PublishedSnapshotRetainsIndependentGpuDrawDatabase()
	{
		var buffer = new FrameSnapshotBuffer();
		var mesh = CreateTestMesh();
		var material = new Material("test-shader");
		var entityA = new Entity(1, 1);
		var entityB = new Entity(2, 1);

		var snapshotA = buffer.BeginWrite();
		WriteEntity(snapshotA.GpuDrawDatabase, entityA, mesh, material, 1.0f);
		buffer.PublishWrite();

		Assert.That(buffer.TryConsumeLatest(out var consumedSnapshotA), Is.True);

		var snapshotB = buffer.BeginWrite();
		WriteEntity(snapshotB.GpuDrawDatabase, entityB, mesh, material, 5.0f);

		var entries = new List<GpuDrawEntry>();
		consumedSnapshotA.GpuDrawDatabase.CollectDrawEntries(entries);

		Assert.That(entries, Has.Count.EqualTo(1));
		Assert.That(entries[0].DrawHandle.Index, Is.EqualTo(1));
		Assert.That(entries[0].DrawKind, Is.EqualTo(GpuDrawKind.Mesh));
		Assert.That(entries[0].World.Translation.X, Is.EqualTo(1.0f).Within(0.0001f));
	}

	[Test]
	public void GpuDrawDatabase_ResetForSnapshotWrite_ClearsPendingUpdatesButPreservesTrackedEntries()
	{
		var database = new GpuDrawDatabase();
		var mesh = CreateTestMesh();
		var material = new Material("test-shader");
		var entity = new Entity(1, 1);

		WriteEntity(database, entity, mesh, material, 1.0f);
		var updates = new List<GpuDrawUpdate>();
		database.ConsumeUpdates(updates);
		Assert.That(updates, Is.Not.Empty);

		database.ResetForSnapshotWrite();
		database.ConsumeUpdates(updates);

		var entries = new List<GpuDrawEntry>();
		database.CollectDrawEntries(entries);

		Assert.That(updates, Is.Empty);
		Assert.That(entries, Has.Count.EqualTo(1));
		Assert.That(entries[0].DrawKind, Is.EqualTo(GpuDrawKind.Mesh));
		Assert.That(entries[0].World.Translation.X, Is.EqualTo(1.0f).Within(0.0001f));
	}

	[Test]
	public void GpuDrawDatabase_AfterReset_NextTransformUpdateUsesPreservedPreviousWorld()
	{
		var database = new GpuDrawDatabase();
		var mesh = CreateTestMesh();
		var material = new Material("test-shader");
		var entity = new Entity(1, 1);

		WriteEntity(database, entity, mesh, material, 1.0f);
		var updates = new List<GpuDrawUpdate>();
		database.ConsumeUpdates(updates);

		database.ResetForSnapshotWrite();
		WriteEntity(database, entity, mesh, material, 3.0f);
		database.ConsumeUpdates(updates);

		Assert.That(updates, Has.Count.EqualTo(1));
		Assert.That(updates[0].Type, Is.EqualTo(GpuDrawUpdateType.UpdateTransform));
		Assert.That(updates[0].DrawKind, Is.EqualTo(GpuDrawKind.Mesh));
		Assert.That(updates[0].PreviousWorld.Translation.X, Is.EqualTo(1.0f).Within(0.0001f));
		Assert.That(updates[0].World.Translation.X, Is.EqualTo(3.0f).Within(0.0001f));
	}

	[Test]
	public void GpuDrawDatabase_MeshRegistrationAndUpdates_PreserveMeshDrawKind()
	{
		var database = new GpuDrawDatabase();
		var meshA = CreateTestMesh();
		var meshB = CreateOffsetMesh();
		var materialA = new Material("shader-a");
		var materialB = new Material("shader-b");
		var entity = new Entity(1, 1);
		var updates = new List<GpuDrawUpdate>();

		database.BeginSync();
		database.TouchMesh(entity, meshA, materialA, Matrix4x4.Identity);
		database.EndSync();
		database.ConsumeUpdates(updates);

		Assert.That(updates.Select(update => update.Type), Is.EqualTo(new[] { GpuDrawUpdateType.Add }));
		Assert.That(updates.All(update => update.DrawKind == GpuDrawKind.Mesh), Is.True);

		database.BeginSync();
		database.TouchMesh(entity, meshA, materialA, Matrix4x4.CreateTranslation(2.0f, 0.0f, 0.0f));
		database.EndSync();
		database.ConsumeUpdates(updates);

		Assert.That(updates.Select(update => update.Type), Is.EqualTo(new[] { GpuDrawUpdateType.UpdateTransform }));
		Assert.That(updates.All(update => update.DrawKind == GpuDrawKind.Mesh), Is.True);

		database.BeginSync();
		database.TouchMesh(entity, meshA, materialB, Matrix4x4.CreateTranslation(2.0f, 0.0f, 0.0f));
		database.EndSync();
		database.ConsumeUpdates(updates);

		Assert.That(updates.Select(update => update.Type), Does.Contain(GpuDrawUpdateType.UpdateMaterial));
		Assert.That(updates.All(update => update.DrawKind == GpuDrawKind.Mesh), Is.True);

		database.BeginSync();
		database.TouchMesh(entity, meshB, materialB, Matrix4x4.CreateTranslation(2.0f, 0.0f, 0.0f));
		database.EndSync();
		database.ConsumeUpdates(updates);

		Assert.That(updates.Select(update => update.Type), Does.Contain(GpuDrawUpdateType.UpdateMesh));
		Assert.That(updates.All(update => update.DrawKind == GpuDrawKind.Mesh), Is.True);

		database.BeginSync();
		database.EndSync();
		database.ConsumeUpdates(updates);

		Assert.That(updates.Select(update => update.Type), Is.EqualTo(new[] { GpuDrawUpdateType.Remove }));
		Assert.That(updates.All(update => update.DrawKind == GpuDrawKind.Mesh), Is.True);
	}

	[Test]
	public void GpuDrawDatabase_DebugPrimitiveRegistrationAndUpdates_PreserveDebugPrimitiveDrawKind()
	{
		var database = new GpuDrawDatabase();
		var primitiveFactory = new DebugPrimitiveMeshFactory();
		var boxMesh = primitiveFactory.GetMesh(DebugPrimitiveType.Box);
		var sphereMesh = primitiveFactory.GetMesh(DebugPrimitiveType.Sphere);
		var entity = new Entity(3, 1);
		var updates = new List<GpuDrawUpdate>();

		database.BeginSync();
		database.TouchDebugPrimitive(
			entity,
			boxMesh,
			new ColorRGBA(0.2f, 0.4f, 0.8f, 1.0f),
			AlphaMode.Opaque,
			Matrix4x4.Identity);
		database.EndSync();
		database.ConsumeUpdates(updates);

		Assert.That(updates.Select(update => update.Type), Is.EqualTo(new[] { GpuDrawUpdateType.Add }));
		Assert.That(updates.All(update => update.DrawKind == GpuDrawKind.DebugPrimitive), Is.True);
		Assert.That(updates[0].Material, Is.Not.Null);
		Assert.That(updates[0].Material!.AlphaMode, Is.EqualTo(AlphaMode.Opaque));

		database.BeginSync();
		database.TouchDebugPrimitive(
			entity,
			boxMesh,
			new ColorRGBA(0.2f, 0.4f, 0.8f, 1.0f),
			AlphaMode.Opaque,
			Matrix4x4.CreateTranslation(3.0f, 0.0f, 0.0f));
		database.EndSync();
		database.ConsumeUpdates(updates);

		Assert.That(updates.Select(update => update.Type), Is.EqualTo(new[] { GpuDrawUpdateType.UpdateTransform }));
		Assert.That(updates.All(update => update.DrawKind == GpuDrawKind.DebugPrimitive), Is.True);

		database.BeginSync();
		database.TouchDebugPrimitive(
			entity,
			boxMesh,
			new ColorRGBA(1.0f, 0.2f, 0.1f, 0.5f),
			AlphaMode.AlphaBlend,
			Matrix4x4.CreateTranslation(3.0f, 0.0f, 0.0f));
		database.EndSync();
		database.ConsumeUpdates(updates);

		Assert.That(updates.Select(update => update.Type), Does.Contain(GpuDrawUpdateType.UpdateMaterial));
		Assert.That(updates.All(update => update.DrawKind == GpuDrawKind.DebugPrimitive), Is.True);
		var materialUpdate = updates.Single(update => update.Type == GpuDrawUpdateType.UpdateMaterial);
		Assert.That(materialUpdate.Material, Is.Not.Null);
		Assert.That(materialUpdate.Material!.AlphaMode, Is.EqualTo(AlphaMode.AlphaBlend));
		Assert.That(materialUpdate.Material.Color.R, Is.EqualTo(1.0f).Within(0.0001f));
		Assert.That(materialUpdate.Material.Color.A, Is.EqualTo(0.5f).Within(0.0001f));

		database.BeginSync();
		database.TouchDebugPrimitive(
			entity,
			sphereMesh,
			new ColorRGBA(1.0f, 0.2f, 0.1f, 0.5f),
			AlphaMode.AlphaBlend,
			Matrix4x4.CreateTranslation(3.0f, 0.0f, 0.0f));
		database.EndSync();
		database.ConsumeUpdates(updates);

		Assert.That(updates.Select(update => update.Type), Does.Contain(GpuDrawUpdateType.UpdateMesh));
		Assert.That(updates.All(update => update.DrawKind == GpuDrawKind.DebugPrimitive), Is.True);
		var meshUpdate = updates.Single(update => update.Type == GpuDrawUpdateType.UpdateMesh);
		Assert.That(meshUpdate.Mesh, Is.SameAs(sphereMesh));

		database.BeginSync();
		database.EndSync();
		database.ConsumeUpdates(updates);

		Assert.That(updates.Select(update => update.Type), Is.EqualTo(new[] { GpuDrawUpdateType.Remove }));
		Assert.That(updates.All(update => update.DrawKind == GpuDrawKind.DebugPrimitive), Is.True);
	}

	[Test]
	public void GpuDrawDatabase_DualKindRegistration_PreservesIndependentHandlesAndKinds()
	{
		var database = new GpuDrawDatabase();
		var primitiveFactory = new DebugPrimitiveMeshFactory();
		var mesh = CreateTestMesh();
		var material = new Material("mesh-shader");
		var debugMesh = primitiveFactory.GetMesh(DebugPrimitiveType.Quad);
		var meshEntity = new Entity(10, 1);
		var debugEntity = new Entity(11, 1);
		var updates = new List<GpuDrawUpdate>();

		database.BeginSync();
		database.TouchMesh(meshEntity, mesh, material, Matrix4x4.Identity);
		database.TouchDebugPrimitive(
			debugEntity,
			debugMesh,
			new ColorRGBA(0.7f, 0.1f, 0.3f, 1.0f),
			AlphaMode.Opaque,
			Matrix4x4.CreateTranslation(5.0f, 0.0f, 0.0f));
		database.EndSync();
		database.ConsumeUpdates(updates);

		Assert.That(updates, Has.Count.EqualTo(2));
		Assert.That(updates.Select(update => update.DrawKind), Is.EqualTo(new[]
		{
			GpuDrawKind.Mesh,
			GpuDrawKind.DebugPrimitive
		}));
		Assert.That(updates.Select(update => update.Type), Is.EqualTo(new[]
		{
			GpuDrawUpdateType.Add,
			GpuDrawUpdateType.Add
		}));
		Assert.That(updates[0].DrawHandle, Is.Not.EqualTo(updates[1].DrawHandle));
		Assert.That(updates[0].InstanceHandle, Is.Not.EqualTo(updates[1].InstanceHandle));

		var entries = new List<GpuDrawEntry>();
		database.CollectDrawEntries(entries);

		Assert.That(entries, Has.Count.EqualTo(2));
		Assert.That(entries.Count(entry => entry.DrawKind == GpuDrawKind.Mesh), Is.EqualTo(1));
		Assert.That(entries.Count(entry => entry.DrawKind == GpuDrawKind.DebugPrimitive), Is.EqualTo(1));
		Assert.That(entries.Single(entry => entry.DrawKind == GpuDrawKind.DebugPrimitive).Mesh, Is.SameAs(debugMesh));
	}

	[Test]
	public void GpuDrawDatabase_TerrainChunkRegistration_TracksMultipleChunksPerEntity()
	{
		var database = new GpuDrawDatabase();
		var meshA = CreateTestMesh();
		var meshB = CreateOffsetMesh();
		var material = CreateTerrainMaterial();
		var entity = new Entity(20, 1);
		var updates = new List<GpuDrawUpdate>();
		var surface = CreateTerrainSurface();

		database.BeginSync();
		database.TouchTerrainChunk(entity, 0, meshA, material, surface, Matrix4x4.Identity);
		database.TouchTerrainChunk(entity, 1, meshB, material, surface, Matrix4x4.CreateTranslation(10.0f, 0.0f, 0.0f));
		database.EndSync();
		database.ConsumeUpdates(updates);

		Assert.That(updates, Has.Count.EqualTo(2));
		Assert.That(updates.All(update => update.DrawKind == GpuDrawKind.Terrain), Is.True);
		Assert.That(updates.All(update => update.Type == GpuDrawUpdateType.Add), Is.True);
		Assert.That(updates[0].DrawHandle, Is.Not.EqualTo(updates[1].DrawHandle));
		Assert.That(updates[0].MaterialHandle, Is.EqualTo(updates[1].MaterialHandle));

		var entries = new List<GpuDrawEntry>();
		database.CollectDrawEntries(entries);

		Assert.That(entries, Has.Count.EqualTo(2));
		Assert.That(entries.All(entry => entry.DrawKind == GpuDrawKind.Terrain), Is.True);
		Assert.That(entries.All(entry => entry.TerrainSurface.HasValue), Is.True);
		Assert.That(entries.All(entry => ReferenceEquals(entry.Material, material)), Is.True);
		Assert.That(entries[0].MaterialHandle, Is.EqualTo(entries[1].MaterialHandle));
	}

	[Test]
	public void GpuDrawDatabase_TerrainChunkUpdates_PreserveStableSubdrawIdentity()
	{
		var database = new GpuDrawDatabase();
		var mesh = CreateTestMesh();
		var material = CreateTerrainMaterial();
		var entity = new Entity(21, 1);
		var updates = new List<GpuDrawUpdate>();
		var initialSurface = CreateTerrainSurface();
		var updatedSurface = CreateTerrainSurface(heightBlendSharpness: 8.0f);

		database.BeginSync();
		database.TouchTerrainChunk(entity, 0, mesh, material, initialSurface, Matrix4x4.Identity);
		database.EndSync();
		database.ConsumeUpdates(updates);
		var initialDrawHandle = updates[0].DrawHandle;
		var initialMaterialHandle = updates[0].MaterialHandle;

		database.BeginSync();
		database.TouchTerrainChunk(entity, 0, mesh, material, initialSurface, Matrix4x4.CreateTranslation(2.0f, 0.0f, 0.0f));
		database.EndSync();
		database.ConsumeUpdates(updates);

		Assert.That(updates.Select(update => update.Type), Is.EqualTo(new[] { GpuDrawUpdateType.UpdateTransform }));
		Assert.That(updates[0].DrawHandle, Is.EqualTo(initialDrawHandle));
		Assert.That(updates[0].MaterialHandle, Is.EqualTo(initialMaterialHandle));

		database.BeginSync();
		database.TouchTerrainChunk(entity, 0, mesh, material, updatedSurface, Matrix4x4.CreateTranslation(2.0f, 0.0f, 0.0f));
		database.EndSync();
		database.ConsumeUpdates(updates);

		Assert.That(updates.Select(update => update.Type), Does.Contain(GpuDrawUpdateType.UpdateMaterial));
		Assert.That(updates.All(update => update.DrawHandle.Equals(initialDrawHandle)), Is.True);
		Assert.That(updates.All(update => update.MaterialHandle.Equals(initialMaterialHandle)), Is.True);
		Assert.That(updates.Single(update => update.Type == GpuDrawUpdateType.UpdateMaterial).TerrainSurface!.Value.HeightBlendSharpness, Is.EqualTo(8.0f).Within(0.0001f));

		database.BeginSync();
		database.EndSync();
		database.ConsumeUpdates(updates);

		Assert.That(updates.Select(update => update.Type), Is.EqualTo(new[] { GpuDrawUpdateType.Remove }));
		Assert.That(updates[0].DrawHandle, Is.EqualTo(initialDrawHandle));
	}

	[Test]
	public void GpuDrawData_SharedGpuStructSizesRemain16ByteAligned()
	{
		AssertGpuStructSizeIs16ByteAligned<GpuInstanceData>();
		AssertGpuStructSizeIs16ByteAligned<GpuMaterialData>();
		AssertGpuStructSizeIs16ByteAligned<GpuTerrainMaterialData>();
		AssertGpuStructSizeIs16ByteAligned<GpuTerrainLayerData>();
		AssertGpuStructSizeIs16ByteAligned<GpuMeshData>();
		AssertGpuStructSizeIs16ByteAligned<GpuDrawCommand>();
		AssertGpuStructSizeIs16ByteAligned<GpuDrawArgs>();
		AssertGpuStructSizeIs16ByteAligned<GpuDrawInstanceUpdateData>();
		AssertGpuStructSizeIs16ByteAligned<GpuDrawMeshUpdateData>();
		AssertGpuStructSizeIs16ByteAligned<GpuDrawMaterialUpdateData>();
		AssertGpuStructSizeIs16ByteAligned<GpuTerrainMaterialUpdateData>();
		AssertGpuStructSizeIs16ByteAligned<GpuTerrainLayerUpdateData>();
	}

	private static void WriteEntity(
		GpuDrawDatabase database,
		Entity entity,
		Mesh mesh,
		Material material,
		float translationX)
	{
		database.BeginSync();
		database.TouchMesh(entity, mesh, material, Matrix4x4.CreateTranslation(translationX, 0.0f, 0.0f));
		database.EndSync();
	}

	private static Material CreateTerrainMaterial()
	{
		return new Material("__terrain__")
		{
			Color = ColorRGBA.White,
			AlphaMode = AlphaMode.Opaque,
			AlphaCutoff = 0.0f,
			MetallicFactor = 0.0f,
			RoughnessFactor = 1.0f,
			EmissiveFactor = Vector3.Zero,
			EmissiveIntensity = 0.0f
		};
	}

	private static Mesh CreateTestMesh()
	{
		return new Mesh(
			[
				new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
				new Vector4(1.0f, 0.0f, 0.0f, 1.0f),
				new Vector4(0.0f, 1.0f, 0.0f, 1.0f)
			],
			[0u, 1u, 2u]);
	}

	private static Mesh CreateOffsetMesh()
	{
		return new Mesh(
			[
				new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
				new Vector4(2.0f, 0.0f, 0.0f, 1.0f),
				new Vector4(0.0f, 2.0f, 0.0f, 1.0f)
			],
			[0u, 1u, 2u]);
	}

	private static TerrainDrawSurface CreateTerrainSurface(float heightBlendSharpness = 4.0f)
	{
		return new TerrainDrawSurface(
			controlMap: null,
			layerCount: 1,
			heightBlendSharpness: heightBlendSharpness,
			layers:
			[
				new TerrainResolvedLayer(null, null, null, null, 8.0f)
			]);
	}

	private static void AssertGpuStructSizeIs16ByteAligned<T>() where T : unmanaged
	{
		var size = Marshal.SizeOf<T>();
		Assert.That(
			size % 16,
			Is.EqualTo(0),
			$"{typeof(T).Name} size must remain a multiple of 16 bytes to match shader structured-buffer layout. Actual size: {size}.");
	}
}
