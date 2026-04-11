using System.Collections.Generic;
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
		Assert.That(updates[0].PreviousWorld.Translation.X, Is.EqualTo(1.0f).Within(0.0001f));
		Assert.That(updates[0].World.Translation.X, Is.EqualTo(3.0f).Within(0.0001f));
	}

	private static void WriteEntity(
		GpuDrawDatabase database,
		Entity entity,
		Mesh mesh,
		Material material,
		float translationX)
	{
		database.BeginSync();
		database.Touch(entity, mesh, material, Matrix4x4.CreateTranslation(translationX, 0.0f, 0.0f));
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
}
