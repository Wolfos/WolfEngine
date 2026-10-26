using System.Numerics;
using Moq;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class IndirectDrawRecordsTests
{
	private const DrawPassParticipation Participation = DrawPassParticipation.GBuffer;

	private FakeDrawRecordEncoder _encoder = null!;
	private FakeLiveDrawSource _liveDraws = null!;
	private FakeStalenessInputSource _stalenessInputs = null!;
	private FakeLaneBindingProvider _lanes = null!;
	private IndirectDrawRecords _records = null!;
	private IGfxDevice _device = null!;
	private GpuDrawExecutionLaneDefinition _lane;
	private Mesh _mesh = null!;

	public enum StalenessInput
	{
		SharedDrawBuffers,
		PackedGeometry,
		BindlessTable
	}

	[SetUp]
	public void SetUp()
	{
		_encoder = new FakeDrawRecordEncoder();
		_liveDraws = new FakeLiveDrawSource();
		_stalenessInputs = new FakeStalenessInputSource();
		_lanes = new FakeLaneBindingProvider();
		_records = new IndirectDrawRecords(_encoder, _liveDraws, _stalenessInputs);
		_device = Mock.Of<IGfxDevice>();
		_lane = FindMeshLane();
		_mesh = new Mesh(
			[new Vector4(0, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(0, 1, 0, 1)],
			[0, 1, 2]);
		_records.BeginFrame();
	}

	[Test]
	public void FirstPrepare_EncodesEveryLiveDraw()
	{
		AddLiveDraw(3);
		AddLiveDraw(5);
		var set = _records.CreateSet();

		Prepare(set);

		Assert.Multiple(() =>
		{
			Assert.That(_encoder.EncodedDraws, Is.EquivalentTo(new[] { 3u, 5u }));
			Assert.That(DrawsIn(set), Is.EquivalentTo(new[] { 3u, 5u }));
		});
	}

	[Test]
	public void StructuralChange_EncodesOnlyThatDraw_InEverySetThatDrawsItsLane()
	{
		AddLiveDraw(3);
		var first = _records.CreateSet();
		var second = _records.CreateSet();
		Prepare(first);
		Prepare(second);
		_encoder.ClearLog();

		AddLiveDraw(7);
		_records.RecordStructuralChange(GpuDrawUpdateType.Add, Handle(7), _lane.Key, _mesh);
		Prepare(first);
		Prepare(second);

		Assert.Multiple(() =>
		{
			Assert.That(_encoder.EncodedDraws, Is.EqualTo(new[] { 7u, 7u }), "only the changed draw, once per set");
			Assert.That(DrawsIn(first), Is.EquivalentTo(new[] { 3u, 7u }));
			Assert.That(DrawsIn(second), Is.EquivalentTo(new[] { 3u, 7u }));
			Assert.That(_liveDraws.MadeResident, Has.Count.EqualTo(2), "the mesh is made resident before each encode");
		});
	}

	[Test]
	public void RemovedDraw_IsResetInEverySet()
	{
		AddLiveDraw(3);
		AddLiveDraw(5);
		var first = _records.CreateSet();
		var second = _records.CreateSet();
		Prepare(first);
		Prepare(second);
		_encoder.ClearLog();

		_records.RecordStructuralChange(GpuDrawUpdateType.Remove, Handle(3), _lane.Key, null);
		Prepare(first);
		Prepare(second);

		Assert.Multiple(() =>
		{
			Assert.That(_encoder.EncodedDraws, Is.Empty);
			Assert.That(DrawsIn(first), Is.EquivalentTo(new[] { 5u }));
			Assert.That(DrawsIn(second), Is.EquivalentTo(new[] { 5u }));
		});
	}

	[Test]
	public void ChangeToADrawNoLongerCurrent_IsNotReplayed()
	{
		AddLiveDraw(3);
		var set = _records.CreateSet();
		Prepare(set);
		_encoder.ClearLog();

		_records.RecordStructuralChange(GpuDrawUpdateType.Add, Handle(9), _lane.Key, _mesh);
		Prepare(set);

		Assert.That(_encoder.EncodedDraws, Is.Empty, "a draw removed or reused before replay must not be encoded");
	}

	[TestCase(StalenessInput.SharedDrawBuffers)]
	[TestCase(StalenessInput.PackedGeometry)]
	[TestCase(StalenessInput.BindlessTable)]
	public void AnyStalenessInputChange_ReencodesTheSetInFull_OnlyInTheSlotBeingPrepared(StalenessInput input)
	{
		AddLiveDraw(3);
		AddLiveDraw(5);
		var set = _records.CreateSet();
		Prepare(set, slotIndex: 0);
		Prepare(set, slotIndex: 1);
		_encoder.ClearLog();

		Bump(input);
		Prepare(set, slotIndex: 0);

		Assert.Multiple(() =>
		{
			Assert.That(_encoder.EncodedDraws, Is.EquivalentTo(new[] { 3u, 5u }));
			Assert.That(DrawsIn(set, slotIndex: 0), Is.EquivalentTo(new[] { 3u, 5u }));
			Assert.That(DrawsIn(set, slotIndex: 1), Is.EquivalentTo(new[] { 3u, 5u }), "slot 1 still holds its records");
		});

		_encoder.ClearLog();
		Prepare(set, slotIndex: 0);
		Assert.That(_encoder.EncodedDraws, Is.Empty, "a slot encoded against the current inputs is not encoded again");
	}

	[Test]
	public void InputChangedBetweenTwoSetsInOneFrame_IsSeenByTheSecondSet()
	{
		AddLiveDraw(3);
		var first = _records.CreateSet();
		var second = _records.CreateSet();
		Prepare(first);
		Prepare(second);
		_encoder.ClearLog();

		_records.BeginFrame();
		Prepare(first);
		Bump(StalenessInput.SharedDrawBuffers);
		Prepare(second);

		Assert.That(_encoder.EncodedDraws, Is.EqualTo(new[] { 3u }), "the set prepared after the change re-encodes");

		_encoder.ClearLog();
		Prepare(first);
		Assert.That(_encoder.EncodedDraws, Is.EqualTo(new[] { 3u }), "the set prepared before it re-encodes on its next use");
	}

	[Test]
	public void FrameSlotChange_ReencodesTheSetInFull()
	{
		AddLiveDraw(3);
		var set = _records.CreateSet();
		Prepare(set, frameSlot: 0);
		_encoder.ClearLog();

		Prepare(set, frameSlot: 1);

		Assert.That(_encoder.EncodedDraws, Is.EqualTo(new[] { 3u }));
	}

	[Test]
	public void IndexStreamChange_ReencodesTheSetInFull()
	{
		AddLiveDraw(3);
		var set = _records.CreateSet();
		Prepare(set);
		_encoder.ClearLog();

		Prepare(set, indexStream: MeshIndexStream.ShadowOpaque);

		Assert.That(_encoder.EncodedDraws, Is.EqualTo(new[] { 3u }));
	}

	[Test]
	public void SetIdleForAFullTurnOfTheSlotRing_StopsHoldingBackTheLog_AndReencodesOnItsNextUse()
	{
		AddLiveDraw(3);
		var busy = _records.CreateSet();
		var idle = _records.CreateSet();
		PrepareEverySlot(busy);
		PrepareEverySlot(idle);

		for (var frame = 0; frame <= GpuDrawResources.IndirectCommandBufferSlotCount; frame++)
		{
			_records.BeginFrame();
			AddLiveDraw((uint)(10 + frame));
			_records.RecordStructuralChange(GpuDrawUpdateType.Add, Handle((uint)(10 + frame)), _lane.Key, _mesh);
			PrepareEverySlot(busy);
		}

		Assert.That(_records.PendingStructuralChangeCount, Is.Zero, "the idle set no longer pins the replay log");

		_encoder.ClearLog();
		Prepare(idle);
		Assert.That(_encoder.EncodedDraws, Has.Count.EqualTo(_liveDraws.Draws.Count), "the idle set re-encodes in full");
	}

	[Test]
	public void ReleasedSet_NoLongerHoldsBackTheLog()
	{
		AddLiveDraw(3);
		var kept = _records.CreateSet();
		var released = _records.CreateSet();
		PrepareEverySlot(kept);
		PrepareEverySlot(released);

		AddLiveDraw(7);
		_records.RecordStructuralChange(GpuDrawUpdateType.Add, Handle(7), _lane.Key, _mesh);
		PrepareEverySlot(kept);
		Assert.That(_records.PendingStructuralChangeCount, Is.EqualTo(1), "the other set has not replayed it yet");

		_records.Release(released);
		PrepareEverySlot(kept);
		Assert.That(_records.PendingStructuralChangeCount, Is.Zero);
	}

	private void Prepare(
		DrawRecordSet set,
		int slotIndex = 0,
		int? frameSlot = null,
		MeshIndexStream indexStream = MeshIndexStream.Main)
	{
		_records.Prepare(
			set,
			_device,
			slotIndex,
			frameSlot ?? slotIndex,
			Participation,
			new SharedDrawIndirectEncodeResources(null, null, null, 0, null, indexStream),
			_lanes);
	}

	private void PrepareEverySlot(DrawRecordSet set)
	{
		for (var slotIndex = 0; slotIndex < GpuDrawResources.IndirectCommandBufferSlotCount; slotIndex++)
		{
			Prepare(set, slotIndex);
		}
	}

	private void AddLiveDraw(uint drawIndex)
	{
		_liveDraws.Draws.Add(new LiveDraw(drawIndex, _lane.Key, _mesh));
		_liveDraws.CurrentHandles.Add(Handle(drawIndex).Value);
	}

	private void Bump(StalenessInput input)
	{
		var inputs = _stalenessInputs.Inputs;
		_stalenessInputs.Inputs = input switch
		{
			StalenessInput.SharedDrawBuffers => inputs with { SharedDrawBufferGeneration = inputs.SharedDrawBufferGeneration + 1 },
			StalenessInput.PackedGeometry => inputs with { PackedGeometryGeneration = inputs.PackedGeometryGeneration + 1 },
			_ => inputs with { BindlessTableGeneration = inputs.BindlessTableGeneration + 1 }
		};
	}

	private List<uint> DrawsIn(DrawRecordSet set, int slotIndex = 0)
	{
		var draws = new List<uint>();
		foreach (var page in set.GetAllocatedPages(slotIndex, _lane.ExecutionIndex))
		{
			draws.AddRange(((FakeRecordPage)page.CommandBuffer).Records.Values);
		}

		return draws;
	}

	private static GpuDrawHandle Handle(uint drawIndex) => GpuDrawHandle.Create((int)drawIndex, 1);

	private static GpuDrawExecutionLaneDefinition FindMeshLane()
	{
		foreach (var lane in GpuDrawExecutionLanes.GetDefinitionsForPass(Participation))
		{
			if (lane.DrawKind == GpuDrawKind.Mesh && lane.IsSkinned == false)
			{
				return lane;
			}
		}

		throw new InvalidOperationException("No rigid mesh lane takes part in the GBuffer pass.");
	}
}
