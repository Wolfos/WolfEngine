using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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
}
