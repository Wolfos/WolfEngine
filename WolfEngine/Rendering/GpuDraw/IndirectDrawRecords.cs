using WolfEngine.Profiling;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering;

/// <summary>
/// Owns every <see cref="DrawRecordSet"/> and keeps each one current. A set is brought up to date when a pass is
/// about to execute it: in full when any staleness input, its frame slot or its index stream changed since it was
/// encoded, and otherwise by replaying the structural changes it has not yet seen.
/// </summary>
/// <remarks>
/// Staleness inputs are read at that moment rather than once per frame, because a buffer can be replaced while
/// passes are still recording, and a set encoded after the replacement must not keep the old buffer.
/// </remarks>
public sealed class IndirectDrawRecords
{
	private readonly IDrawRecordEncoder _encoder;
	private readonly ILiveDrawSource _liveDraws;
	private readonly IDrawRecordStalenessInputSource _stalenessInputs;
	private readonly List<DrawRecordSet> _sets = new();
	// When each set was last prepared, in frames counted by BeginFrame. A set idle for a full turn of the slot ring
	// stops holding back replay-log compaction and re-encodes in full on its next use.
	private readonly Dictionary<DrawRecordSet, long> _setLastUse = new();
	private readonly List<StructuralChange> _replayLog = new();
	private readonly List<LiveDraw> _liveDrawScratch = new();
	private long _frame;
	private ulong _latestStructuralVersion;
	private ulong _nextStructuralVersion = 1;

	private readonly record struct StructuralChange(
		ulong Version,
		GpuDrawUpdateType Type,
		GpuDrawHandle DrawHandle,
		GpuDrawExecutionKey ExecutionKey,
		Mesh? Mesh);

	public IndirectDrawRecords(
		IDrawRecordEncoder encoder,
		ILiveDrawSource liveDraws,
		IDrawRecordStalenessInputSource stalenessInputs)
	{
		_encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
		_liveDraws = liveDraws ?? throw new ArgumentNullException(nameof(liveDraws));
		_stalenessInputs = stalenessInputs ?? throw new ArgumentNullException(nameof(stalenessInputs));
	}

	/// <summary>Structural changes not yet replayed into every set that is still in use.</summary>
	internal int PendingStructuralChangeCount => _replayLog.Count;

	public DrawRecordSet CreateSet() => new(_encoder);

	/// <summary>
	/// Stops tracking a set that is about to be retired, so it no longer holds back replay-log compaction. The
	/// caller still owns the set and retires it.
	/// </summary>
	public void Release(DrawRecordSet set)
	{
		ArgumentNullException.ThrowIfNull(set);
		_sets.Remove(set);
		_setLastUse.Remove(set);
	}

	/// <summary>Counts one frame. Call once per frame, before that frame's structural changes are recorded.</summary>
	public void BeginFrame()
	{
		_frame++;
	}

	/// <summary>
	/// Records a draw being added, removed, or moved to another lane, mesh or material, for every set to replay the
	/// next time it is prepared.
	/// </summary>
	public void RecordStructuralChange(
		GpuDrawUpdateType type,
		in GpuDrawHandle drawHandle,
		in GpuDrawExecutionKey executionKey,
		Mesh? mesh)
	{
		var version = _nextStructuralVersion++;
		_replayLog.Add(new StructuralChange(version, type, drawHandle, executionKey, mesh));
		_latestStructuralVersion = version;
	}

	/// <summary>Brings one slot of a set up to date before its pass executes it.</summary>
	public void Prepare(
		DrawRecordSet set,
		IGfxDevice device,
		int slotIndex,
		int frameSlot,
		DrawPassParticipation participation,
		in SharedDrawIndirectEncodeResources resources,
		ILaneBindingProvider lanes)
	{
		ArgumentNullException.ThrowIfNull(set);
		ArgumentNullException.ThrowIfNull(device);
		ArgumentNullException.ThrowIfNull(lanes);

		Track(set);
		set.SetIndexStream(resources.IndexStream);
		var stalenessInputs = _stalenessInputs.Read();
		if (set.RequiresFullReencode(slotIndex, frameSlot, stalenessInputs))
		{
			using (FrameProfiler.Instance.Measure("GpuDraw.FullSlotReencode"))
			{
				ReencodeSlot(set, slotIndex, device, participation, resources, lanes);
			}

			set.MarkSlotEncoded(slotIndex, frameSlot, stalenessInputs, _latestStructuralVersion);
			CompactReplayLog();
			return;
		}

		using (FrameProfiler.Instance.Measure("GpuDraw.StructuralReplay"))
		{
			ReplayPendingChanges(set, slotIndex, device, participation, resources, lanes);
		}

		CompactReplayLog();
	}

	private void Track(DrawRecordSet set)
	{
		_setLastUse[set] = _frame;
		for (var i = 0; i < _sets.Count; i++)
		{
			if (ReferenceEquals(_sets[i], set))
			{
				return;
			}
		}

		_sets.Add(set);
	}

	private void ReplayPendingChanges(
		DrawRecordSet set,
		int slotIndex,
		IGfxDevice device,
		DrawPassParticipation participation,
		in SharedDrawIndirectEncodeResources resources,
		ILaneBindingProvider lanes)
	{
		var appliedVersion = set.GetAppliedStructuralVersion(slotIndex);
		if (_latestStructuralVersion <= appliedVersion)
		{
			return;
		}

		for (var i = 0; i < _replayLog.Count; i++)
		{
			var change = _replayLog[i];
			if (change.Version <= appliedVersion)
			{
				continue;
			}

			ApplyStructuralChange(change, set, slotIndex, device, participation, resources, lanes);
		}

		set.SetAppliedStructuralVersion(slotIndex, _latestStructuralVersion);
	}

	private void ApplyStructuralChange(
		in StructuralChange change,
		DrawRecordSet set,
		int slotIndex,
		IGfxDevice device,
		DrawPassParticipation participation,
		in SharedDrawIndirectEncodeResources resources,
		ILaneBindingProvider lanes)
	{
		if (_liveDraws.IsCurrent(change.DrawHandle) == false)
		{
			return;
		}

		if (change.DrawHandle.Index == 0 || change.DrawHandle.Index >= GpuDrawResources.MaxDrawCount)
		{
			return;
		}

		var commandIndex = (uint)change.DrawHandle.Index;
		if (change.Type == GpuDrawUpdateType.Remove ||
		    GpuDrawClassification.SupportsMeshBackedGeometry(change.ExecutionKey.DrawKind) == false ||
		    change.Mesh is null)
		{
			ResetCommandInEveryLane(set, slotIndex, commandIndex);
			return;
		}

		_liveDraws.EnsureResident(change.Mesh);
		EncodeCommand(
			commandIndex,
			change.ExecutionKey,
			change.Mesh,
			set,
			slotIndex,
			device,
			participation,
			resources,
			lanes);
	}

	private void ReencodeSlot(
		DrawRecordSet set,
		int slotIndex,
		IGfxDevice device,
		DrawPassParticipation participation,
		in SharedDrawIndirectEncodeResources resources,
		ILaneBindingProvider lanes)
	{
		for (var executionIndex = 0; executionIndex < GpuDrawExecutionLanes.ExecutionLaneCount; executionIndex++)
		{
			var pages = set.GetAllocatedPages(slotIndex, executionIndex);
			for (var pageIndex = 0; pageIndex < pages.Length; pageIndex++)
			{
				var page = pages[pageIndex];
				for (var commandIndex = 0u; commandIndex < page.PageCommandCapacity; commandIndex++)
				{
					_encoder.ResetCommand(page.CommandBuffer, commandIndex);
				}
			}
		}

		// Every command was just reset, so every live draw has to be re-encoded, not only those of the view whose
		// pass noticed the slot needed it.
		_liveDraws.CollectResidentDraws(_liveDrawScratch);
		for (var i = 0; i < _liveDrawScratch.Count; i++)
		{
			var draw = _liveDrawScratch[i];
			EncodeCommand(
				draw.DrawIndex,
				draw.ExecutionKey,
				draw.Mesh,
				set,
				slotIndex,
				device,
				participation,
				resources,
				lanes);
		}
	}

	/// <summary>
	/// Drops the changes every tracked set has replayed. A set unused for a full turn of the slot ring would otherwise
	/// pin the log forever, so it is invalidated instead and re-encodes in full on its next use.
	/// </summary>
	private void CompactReplayLog()
	{
		if (_replayLog.Count == 0 || _sets.Count == 0)
		{
			return;
		}

		var minAppliedVersion = ulong.MaxValue;
		for (var setIndex = 0; setIndex < _sets.Count; setIndex++)
		{
			var set = _sets[setIndex];
			if (_setLastUse.TryGetValue(set, out var lastUse) &&
			    _frame - lastUse > GpuDrawResources.IndirectCommandBufferSlotCount)
			{
				set.InvalidateEncoding();
				continue;
			}

			for (var slotIndex = 0; slotIndex < GpuDrawResources.IndirectCommandBufferSlotCount; slotIndex++)
			{
				var appliedVersion = set.GetAppliedStructuralVersion(slotIndex);
				if (appliedVersion < minAppliedVersion)
				{
					minAppliedVersion = appliedVersion;
				}
			}
		}

		if (minAppliedVersion == 0)
		{
			return;
		}

		var removeCount = 0;
		for (; removeCount < _replayLog.Count; removeCount++)
		{
			if (_replayLog[removeCount].Version > minAppliedVersion)
			{
				break;
			}
		}

		if (removeCount > 0)
		{
			_replayLog.RemoveRange(0, removeCount);
		}
	}

	private void ResetCommandInEveryLane(DrawRecordSet set, int slotIndex, uint commandIndex)
	{
		for (var executionIndex = 0; executionIndex < GpuDrawExecutionLanes.ExecutionLaneCount; executionIndex++)
		{
			if (set.TryGetPageForCommand(
				    slotIndex,
				    executionIndex,
				    commandIndex,
				    out var commandBuffer,
				    out var pageCommandIndex))
			{
				_encoder.ResetCommand(commandBuffer, pageCommandIndex);
			}
		}
	}

	/// <summary>
	/// Encodes a draw into the lane its execution key selects, and resets it in every other lane so a draw that
	/// moved lanes is not drawn twice.
	/// </summary>
	private void EncodeCommand(
		uint commandIndex,
		GpuDrawExecutionKey executionKey,
		Mesh mesh,
		DrawRecordSet set,
		int slotIndex,
		IGfxDevice device,
		DrawPassParticipation participation,
		in SharedDrawIndirectEncodeResources resources,
		ILaneBindingProvider lanes)
	{
		if (GpuDrawExecutionLanes.TryGetDefinition(executionKey, out var targetLane) == false ||
		    targetLane.SupportsPass(participation) == false ||
		    lanes.IsLaneDrawn(targetLane) == false)
		{
			ResetCommandInEveryLane(set, slotIndex, commandIndex);
			return;
		}

		for (var executionIndex = 0; executionIndex < GpuDrawExecutionLanes.ExecutionLaneCount; executionIndex++)
		{
			if (executionIndex == targetLane.ExecutionIndex)
			{
				var bindings = lanes.GetBufferBindings(targetLane)
				               ?? throw new InvalidOperationException(
					               $"Missing reflected shared-draw buffer bindings for execution lane '{targetLane.DebugName}'.");
				var passBindings = lanes.GetPassBindings(targetLane)
					?? throw new InvalidOperationException($"Missing reflected graphics pass bindings for execution lane '{targetLane.DebugName}'.");
				var pipeline = lanes.GetPipeline(targetLane)
					?? throw new InvalidOperationException($"Missing pipeline for execution lane '{targetLane.DebugName}'.");
				var commandBuffer = set.EnsurePageForCommand(
					device,
					slotIndex,
					executionIndex,
					commandIndex,
					out var pageCommandIndex);
				var laneResources = resources.ForLane(targetLane);
				if (_encoder.TryEncodeIndexedDrawCommand(
					    device,
					    pipeline,
					    commandBuffer,
					    pageCommandIndex,
					    commandIndex,
					    mesh,
					    laneResources,
					    passBindings,
					    bindings.ToPerDrawBindings()) == false)
				{
					_encoder.ResetCommand(commandBuffer, pageCommandIndex);
				}

				continue;
			}

			if (set.TryGetPageForCommand(
				    slotIndex,
				    executionIndex,
				    commandIndex,
				    out var staleCommandBuffer,
				    out var stalePageCommandIndex))
			{
				_encoder.ResetCommand(staleCommandBuffer, stalePageCommandIndex);
			}
		}
	}
}
