using System.Numerics;
using Moq;
using WolfEngine.ECS;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class GpuDrawBatchesTests
{
	private GpuDrawBatches _batches = null!;
	private List<Mesh> _madeResident = null!;
	private GpuDrawExecutionKey _opaque;
	private GpuDrawExecutionKey _alphaBlend;
	private Mesh _meshA = null!;
	private Mesh _meshB = null!;

	[SetUp]
	public void SetUp()
	{
		_madeResident = new List<Mesh>();
		_batches = new GpuDrawBatches(_madeResident.Add);
		_opaque = new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.Opaque, GpuDrawSidedness.SingleSided);
		_alphaBlend = new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.AlphaBlend, GpuDrawSidedness.SingleSided);
		_meshA = CreateMesh();
		_meshB = CreateMesh();
	}

	[Test]
	public void InstancesOfOneMeshInOneLane_ShareABatch()
	{
		var first = _batches.Assign(Instance(1), _opaque, _meshA);
		var second = _batches.Assign(Instance(2), _opaque, _meshA);
		var otherMesh = _batches.Assign(Instance(3), _opaque, _meshB);

		Assert.Multiple(() =>
		{
			Assert.That(first, Is.Not.Zero);
			Assert.That(second, Is.EqualTo(first));
			Assert.That(otherMesh, Is.Not.EqualTo(first));
			Assert.That(_batches.GetCapacity((int)first), Is.EqualTo(2u));
			Assert.That(_batches.GetCapacity((int)otherMesh), Is.EqualTo(1u));
			Assert.That(_batches.BatchCount, Is.EqualTo(2));
			Assert.That(_batches.TotalCapacity, Is.EqualTo(3u));
		});
	}

	[Test]
	public void AlphaBlendedInstances_GetABatchEach()
	{
		var first = _batches.Assign(Instance(1), _alphaBlend, _meshA);
		var second = _batches.Assign(Instance(2), _alphaBlend, _meshA);

		Assert.Multiple(() =>
		{
			Assert.That(second, Is.Not.EqualTo(first), "blending order is kept by drawing transparents one at a time");
			Assert.That(_batches.GetCapacity((int)first), Is.EqualTo(1u));
			Assert.That(_batches.GetCapacity((int)second), Is.EqualTo(1u));
		});
	}

	[Test]
	public void ReassigningTheSameKey_ChangesNothing()
	{
		var batch = _batches.Assign(Instance(1), _opaque, _meshA);
		var version = _batches.Version;

		Assert.That(_batches.Assign(Instance(1), _opaque, _meshA), Is.EqualTo(batch));
		Assert.That(_batches.Version, Is.EqualTo(version), "a transform-only update must not dirty the batch table");
	}

	[Test]
	public void ChangingMesh_MovesTheInstance_AndAnEmptiedBatchDies()
	{
		_batches.Assign(Instance(1), _opaque, _meshA);
		var newBatch = _batches.Assign(Instance(1), _opaque, _meshB);
		var meshABatch = _batches.Assign(Instance(2), _opaque, _meshA);

		Assert.Multiple(() =>
		{
			Assert.That(newBatch, Is.Not.Zero);
			Assert.That(_batches.GetBatchIndex(1), Is.EqualTo(newBatch));
			Assert.That(meshABatch, Is.Not.EqualTo(newBatch), "the mesh A batch died, so mesh A gets a new one");
			Assert.That(_batches.GetCapacity((int)newBatch), Is.EqualTo(1u));
			Assert.That(_batches.GetCapacity((int)meshABatch), Is.EqualTo(1u));
			Assert.That(_batches.BatchCount, Is.EqualTo(2));
			Assert.That(_batches.TotalCapacity, Is.EqualTo(2u));
		});
	}

	[Test]
	public void Remove_TakesTheInstanceOut()
	{
		var batch = _batches.Assign(Instance(1), _opaque, _meshA);
		_batches.Assign(Instance(2), _opaque, _meshA);

		_batches.Remove(Instance(1));

		Assert.Multiple(() =>
		{
			Assert.That(_batches.GetCapacity((int)batch), Is.EqualTo(1u));
			Assert.That(_batches.GetBatchIndex(1), Is.Zero);
		});
	}

	[Test]
	public void RemoveForAnOlderInstanceInTheSameSlot_IsIgnored()
	{
		var batch = _batches.Assign(Instance(1, generation: 2), _opaque, _meshA);

		Assert.That(_batches.IsSuperseded(Instance(1, generation: 1)), Is.True);
		_batches.Remove(Instance(1, generation: 1));

		Assert.Multiple(() =>
		{
			Assert.That(_batches.IsSuperseded(Instance(1, generation: 2)), Is.False);
			Assert.That(_batches.GetCapacity((int)batch), Is.EqualTo(1u));
		});
	}

	[Test]
	public void ANewInstanceInAReusedSlot_ReplacesTheOldMember()
	{
		var batch = _batches.Assign(Instance(1, generation: 1), _opaque, _meshA);

		Assert.That(_batches.Assign(Instance(1, generation: 2), _opaque, _meshA), Is.EqualTo(batch));
		Assert.That(_batches.GetCapacity((int)batch), Is.EqualTo(1u), "the old member was not counted twice");
	}

	[TestCase(false)]
	[TestCase(true)]
	public void ReusedInstanceSlot_AcceptsCurrentAddAndRejectsStaleUpdates(bool removeFirst)
	{
		var handles = new GpuDrawHandleRegistry();
		var oldDatabase = new GpuDrawDatabase(new GpuDrawTransformHistory(), handles);
		var newDatabase = new GpuDrawDatabase(new GpuDrawTransformHistory(), handles);
		var material = new Material("opaque");
		var updates = new List<GpuDrawUpdate>();

		oldDatabase.BeginSync();
		oldDatabase.TouchMesh(new Entity(1, 1), _meshA, material, Matrix4x4.Identity);
		oldDatabase.EndSync();
		oldDatabase.ConsumeUpdates(updates);
		var oldAdd = updates.Single();
		_batches.Assign(oldAdd.InstanceHandle, _opaque, _meshA);

		oldDatabase.BeginSync();
		oldDatabase.EndSync();
		oldDatabase.ConsumeUpdates(updates);
		var oldRemove = updates.Single();

		newDatabase.BeginSync();
		newDatabase.TouchMesh(new Entity(2, 1), _meshB, material, Matrix4x4.Identity);
		newDatabase.EndSync();
		newDatabase.ConsumeUpdates(updates);
		var currentAdd = updates.Single();
		oldDatabase.RefreshSharedHandleState();

		Assert.That(currentAdd.InstanceIndex, Is.EqualTo(oldAdd.InstanceIndex));
		Assert.That(currentAdd.InstanceHandle, Is.Not.EqualTo(oldAdd.InstanceHandle));

		if (removeFirst)
		{
			Assert.That(GpuDrawPass.ShouldApplyInstanceUpdate(oldRemove, oldDatabase, _batches), Is.True);
			_batches.Remove(oldRemove.InstanceHandle);
		}

		Assert.That(GpuDrawPass.ShouldApplyInstanceUpdate(currentAdd, newDatabase, _batches), Is.True);
		var batch = _batches.Assign(currentAdd.InstanceHandle, _opaque, _meshB);

		Assert.Multiple(() =>
		{
			Assert.That(GpuDrawPass.ShouldApplyInstanceUpdate(oldRemove, oldDatabase, _batches), Is.False);
			Assert.That(GpuDrawPass.ShouldApplyInstanceUpdate(oldAdd, oldDatabase, _batches), Is.False);
			Assert.That(_batches.GetBatchIndex(currentAdd.InstanceIndex), Is.EqualTo(batch));
			Assert.That(_batches.GetCapacity((int)batch), Is.EqualTo(1u));
		});
	}

	[Test]
	public void UndrawableInstances_AreInNoBatch()
	{
		var unknownKind = new GpuDrawExecutionKey((GpuDrawKind)99, GpuDrawBucketId.Opaque, GpuDrawSidedness.SingleSided);

		Assert.That(_batches.Assign(Instance(1), unknownKind, _meshA), Is.Zero);
		Assert.That(_batches.BatchCount, Is.Zero);
	}

	[Test]
	public void BatchTable_OffsetsArePrefixSumsOfCapacity()
	{
		var first = _batches.Assign(Instance(1), _opaque, _meshA);
		_batches.Assign(Instance(2), _opaque, _meshA);
		_batches.Assign(Instance(3), _opaque, _meshA);
		var second = _batches.Assign(Instance(4), _opaque, _meshB);
		GpuDrawExecutionLanes.TryGetDefinition(_opaque, out var lane);

		var table = _batches.GetBatchTable().ToArray();

		Assert.Multiple(() =>
		{
			Assert.That(table, Has.Length.EqualTo((int)_batches.ActiveBatchUpperBound));
			Assert.That(table[first].VisibleOffset, Is.Zero);
			Assert.That(table[first].Capacity, Is.EqualTo(3u));
			Assert.That(table[second].VisibleOffset, Is.EqualTo(3u));
			Assert.That(table[second].Capacity, Is.EqualTo(1u));
			Assert.That(table[first].LaneIndex, Is.EqualTo((uint)lane.ExecutionIndex));
		});
	}

	[Test]
	public void BatchTable_IsRecomputedAfterMembershipChanges()
	{
		var first = _batches.Assign(Instance(1), _opaque, _meshA);
		var second = _batches.Assign(Instance(2), _opaque, _meshB);
		_ = _batches.GetBatchTable();

		_batches.Assign(Instance(3), _opaque, _meshA);
		var table = _batches.GetBatchTable();

		Assert.That(table[(int)second].VisibleOffset, Is.EqualTo(2u));
		Assert.That(table[(int)first].Capacity, Is.EqualTo(2u));
	}

	[Test]
	public void ActiveBatchUpperBound_ShrinksWhenTheHighestBatchDies()
	{
		_batches.Assign(Instance(1), _opaque, _meshA);
		var high = _batches.Assign(Instance(2), _opaque, _meshB);
		Assert.That(_batches.ActiveBatchUpperBound, Is.EqualTo(high + 1));

		_batches.Remove(Instance(2));

		Assert.That(_batches.ActiveBatchUpperBound, Is.EqualTo(high));
	}

	[Test]
	public void AFullBatchTable_LeavesNewKeysUnbatched()
	{
		var batches = new GpuDrawBatches(_madeResident.Add, maxBatchCount: 2, maxInstanceCount: 8);

		Assert.That(batches.Assign(Instance(1), _opaque, _meshA), Is.Not.Zero);
		Assert.That(batches.Assign(Instance(2), _opaque, _meshB), Is.Zero);
		Assert.That(batches.Assign(Instance(3), _opaque, _meshA), Is.Not.Zero, "an existing batch still takes members");
	}

	[Test]
	public void Records_AreEncodedOncePerBatch_WithItsCapacityAsInstanceCount()
	{
		var encoder = new FakeDrawRecordEncoder();
		var records = new IndirectDrawRecords(encoder, _batches, new FakeStalenessInputSource());
		var set = records.CreateSet(DrawPassParticipation.GBuffer, new FakeLaneBindingProvider());
		var batch = _batches.Assign(Instance(1), _opaque, _meshA);
		_batches.Assign(Instance(2), _opaque, _meshA);
		_batches.PublishStructuralChanges(records);

		records.BeginFrame();
		Prepare(records, set);

		Assert.Multiple(() =>
		{
			Assert.That(encoder.EncodedDraws, Is.EqualTo(new[] { batch }), "a full encode walks batches, not instances");
			Assert.That(encoder.EncodedInstanceCounts[batch], Is.EqualTo(2u));
			Assert.That(_madeResident, Does.Contain(_meshA));
		});
	}

	[Test]
	public void ManyMembershipChangesInAFrame_ReplayAsOneChangePerBatch()
	{
		var encoder = new FakeDrawRecordEncoder();
		var records = new IndirectDrawRecords(encoder, _batches, new FakeStalenessInputSource());
		var set = records.CreateSet(DrawPassParticipation.GBuffer, new FakeLaneBindingProvider());
		var batch = _batches.Assign(Instance(1), _opaque, _meshA);
		_batches.PublishStructuralChanges(records);
		records.BeginFrame();
		Prepare(records, set);
		encoder.ClearLog();

		for (var instanceIndex = 2; instanceIndex <= 10; instanceIndex++)
		{
			_batches.Assign(Instance(instanceIndex), _opaque, _meshA);
		}

		_batches.Remove(Instance(10));
		var pendingBefore = records.PendingStructuralChangeCount;
		_batches.PublishStructuralChanges(records);
		var published = records.PendingStructuralChangeCount - pendingBefore;
		records.BeginFrame();
		Prepare(records, set);

		Assert.Multiple(() =>
		{
			Assert.That(published, Is.EqualTo(1));
			Assert.That(encoder.EncodedDraws, Is.EqualTo(new[] { batch }));
			Assert.That(encoder.EncodedInstanceCounts[batch], Is.EqualTo(9u));
		});
	}

	[Test]
	public void ADyingBatch_HasItsRecordReset()
	{
		var encoder = new FakeDrawRecordEncoder();
		var records = new IndirectDrawRecords(encoder, _batches, new FakeStalenessInputSource());
		var set = records.CreateSet(DrawPassParticipation.GBuffer, new FakeLaneBindingProvider());
		_batches.Assign(Instance(1), _opaque, _meshA);
		_batches.Assign(Instance(2), _opaque, _meshB);
		_batches.PublishStructuralChanges(records);
		records.BeginFrame();
		Prepare(records, set);

		_batches.Remove(Instance(2));
		_batches.PublishStructuralChanges(records);
		records.BeginFrame();
		Prepare(records, set);

		Assert.That(RecordedDraws(set), Is.EqualTo(new[] { _batches.GetBatchIndex(1) }));
	}

	[Test]
	public void Clear_EmptiesEveryBatch_AndResetsTheirRecords()
	{
		var encoder = new FakeDrawRecordEncoder();
		var records = new IndirectDrawRecords(encoder, _batches, new FakeStalenessInputSource());
		var set = records.CreateSet(DrawPassParticipation.GBuffer, new FakeLaneBindingProvider());
		_batches.Assign(Instance(1), _opaque, _meshA);
		_batches.Assign(Instance(2), _opaque, _meshB);
		_batches.PublishStructuralChanges(records);
		records.BeginFrame();
		Prepare(records, set);

		_batches.Clear();
		_batches.PublishStructuralChanges(records);
		records.BeginFrame();
		Prepare(records, set);

		Assert.Multiple(() =>
		{
			Assert.That(_batches.BatchCount, Is.Zero);
			Assert.That(_batches.TotalCapacity, Is.Zero);
			Assert.That(_batches.ActiveBatchUpperBound, Is.EqualTo(1u));
			Assert.That(RecordedDraws(set), Is.Empty);
		});
	}

	private static void Prepare(IndirectDrawRecords records, DrawRecordSet set)
	{
		records.Prepare(
			set,
			Mock.Of<IGfxDevice>(),
			slotIndex: 0,
			frameSlot: 0,
			new SharedDrawIndirectEncodeResources(null, null, null, 0, null, null));
	}

	private List<uint> RecordedDraws(DrawRecordSet set)
	{
		var draws = new List<uint>();
		for (var laneIndex = 0; laneIndex < GpuDrawExecutionLanes.ExecutionLaneCount; laneIndex++)
		{
			foreach (var page in set.GetAllocatedPages(0, laneIndex))
			{
				draws.AddRange(((FakeRecordPage)page.CommandBuffer).Records.Values);
			}
		}

		return draws;
	}

	private static GpuDrawHandle Instance(int index, ushort generation = 1) => GpuDrawHandle.Create(index, generation);

	private static Mesh CreateMesh() =>
		new(
			[new Vector4(0, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(0, 1, 0, 1)],
			[0, 1, 2]);
}
