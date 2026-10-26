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
	// Every tracked set, and when each was last prepared was last prepared, in frames counted by BeginFrame. A set idle for a full turn of the slot ring
	// stops holding back replay-log compaction and re-encodes in full on its next use.
	private readonly Dictionary<DrawRecordSet, long> _setLastUse = new();
	private readonly List<StructuralChange> _replayLog = new();
	private readonly List<LiveDraw> _liveDrawScratch = new();
	// Each lane's bindings as resolved by the set being prepared, filled on first use and cleared by Prepare. Pass
	// binding sets are rebuilt from reflection on every request, so resolving them per record would allocate per draw.
	private readonly GraphicsPassBindingSet?[] _lanePassBindings = new GraphicsPassBindingSet?[GpuDrawExecutionLanes.ExecutionLaneCount];
	private readonly SharedDrawPerDrawBindings[] _lanePerDrawBindings = new SharedDrawPerDrawBindings[GpuDrawExecutionLanes.ExecutionLaneCount];
	private long _frame;
	private ulong _latestStructuralVersion;

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

	/// <param name="participation">The pass whose lanes the set's records draw.</param>
	/// <param name="lanes">How that pass binds each lane, for as long as the set lives.</param>
	public DrawRecordSet CreateSet(DrawPassParticipation participation, ILaneBindingProvider lanes) =>
		new(_encoder, participation, lanes);

	/// <summary>
	/// Stops tracking a set that is about to be retired, so it no longer holds back replay-log compaction. The
	/// caller still owns the set and retires it.
	/// </summary>
	public void Release(DrawRecordSet set)
	{
		ArgumentNullException.ThrowIfNull(set);
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
		_latestStructuralVersion++;
		_replayLog.Add(new StructuralChange(_latestStructuralVersion, type, drawHandle, executionKey, mesh));
	}

	/// <summary>Brings one slot of a set up to date before its pass executes it.</summary>
	public void Prepare(
		DrawRecordSet set,
		IGfxDevice device,
		int slotIndex,
		int frameSlot,
		in SharedDrawIndirectEncodeResources resources)
	{
		ArgumentNullException.ThrowIfNull(set);
		ArgumentNullException.ThrowIfNull(device);

		_setLastUse[set] = _frame;
		set.SetIndexStream(resources.IndexStream);
		Array.Clear(_lanePassBindings);
		PrepareLanePipelines(set, device);

		var stalenessInputs = _stalenessInputs.Read();
		var laneGeneration = set.Lanes.Generation;
		if (set.RequiresFullReencode(slotIndex, frameSlot, stalenessInputs, laneGeneration))
		{
			using (FrameProfiler.Instance.Measure("GpuDraw.FullSlotReencode"))
			{
				ReencodeSlot(set, slotIndex, device, resources);
			}

			set.MarkSlotEncoded(slotIndex, frameSlot, stalenessInputs, laneGeneration, _latestStructuralVersion);
			CompactReplayLog();
			return;
		}

		using (FrameProfiler.Instance.Measure("GpuDraw.StructuralReplay"))
		{
			ReplayPendingChanges(set, slotIndex, device, resources);
		}

		CompactReplayLog();
	}

	/// <summary>
	/// Lets the encoder make current what the set's records will capture, once per drawn lane, before the set is
	/// checked for staleness. Anything this replaces then counts towards that check instead of going unnoticed until
	/// the next prepare.
	/// </summary>
	private void PrepareLanePipelines(DrawRecordSet set, IGfxDevice device)
	{
		var definitions = GpuDrawExecutionLanes.Definitions;
		for (var i = 0; i < definitions.Length; i++)
		{
			var lane = definitions[i];
			if (lane.SupportsPass(set.Participation) == false ||
			    set.Lanes.IsLaneDrawn(lane) == false)
			{
				continue;
			}

			var pipeline = set.Lanes.GetPipeline(lane);
			if (pipeline is not null)
			{
				_encoder.PrepareForPipeline(device, pipeline);
			}
		}
	}

	private void ReplayPendingChanges(
		DrawRecordSet set,
		int slotIndex,
		IGfxDevice device,
		in SharedDrawIndirectEncodeResources resources)
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

			ApplyStructuralChange(change, set, slotIndex, device, resources);
		}

		set.SetAppliedStructuralVersion(slotIndex, _latestStructuralVersion);
	}

	private void ApplyStructuralChange(
		in StructuralChange change,
		DrawRecordSet set,
		int slotIndex,
		IGfxDevice device,
		in SharedDrawIndirectEncodeResources resources)
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
			resources);
	}

	private void ReencodeSlot(
		DrawRecordSet set,
		int slotIndex,
		IGfxDevice device,
		in SharedDrawIndirectEncodeResources resources)
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
				resources);
		}
	}

	/// <summary>
	/// Drops the changes every tracked set has replayed. A set unused for a full turn of the slot ring would otherwise
	/// pin the log forever, so it is invalidated instead and re-encodes in full on its next use.
	/// </summary>
	private void CompactReplayLog()
	{
		if (_replayLog.Count == 0 || _setLastUse.Count == 0)
		{
			return;
		}

		var minAppliedVersion = ulong.MaxValue;
		foreach (var (set, lastUse) in _setLastUse)
		{
			if (_frame - lastUse > GpuDrawResources.IndirectCommandBufferSlotCount)
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
		in SharedDrawIndirectEncodeResources resources)
	{
		ResetCommandInEveryLane(set, slotIndex, commandIndex);
		if (GpuDrawExecutionLanes.TryGetDefinition(executionKey, out var targetLane) == false ||
		    targetLane.SupportsPass(set.Participation) == false ||
		    set.Lanes.IsLaneDrawn(targetLane) == false)
		{
			return;
		}

		var passBindings = ResolveLaneBindings(set.Lanes, targetLane, out var perDrawBindings);
		var commandBuffer = set.EnsurePageForCommand(
			device,
			slotIndex,
			targetLane.ExecutionIndex,
			commandIndex,
			out var pageCommandIndex);
		var laneResources = resources.ForLane(targetLane);
		if (_encoder.TryEncodeIndexedDrawCommand(
			    device,
			    commandBuffer,
			    pageCommandIndex,
			    commandIndex,
			    mesh,
			    laneResources,
			    passBindings,
			    perDrawBindings) == false)
		{
			_encoder.ResetCommand(commandBuffer, pageCommandIndex);
		}
	}

	private GraphicsPassBindingSet ResolveLaneBindings(
		ILaneBindingProvider lanes,
		GpuDrawExecutionLaneDefinition lane,
		out SharedDrawPerDrawBindings perDrawBindings)
	{
		var executionIndex = lane.ExecutionIndex;
		var passBindings = _lanePassBindings[executionIndex];
		if (passBindings is not null)
		{
			perDrawBindings = _lanePerDrawBindings[executionIndex];
			return passBindings;
		}

		if (lanes.GetPipeline(lane) is null)
		{
			throw new InvalidOperationException($"Missing pipeline for execution lane '{lane.DebugName}'.");
		}

		var bufferBindings = lanes.GetBufferBindings(lane)
			?? throw new InvalidOperationException(
				$"Missing reflected shared-draw buffer bindings for execution lane '{lane.DebugName}'.");
		passBindings = lanes.GetPassBindings(lane)
			?? throw new InvalidOperationException(
				$"Missing reflected graphics pass bindings for execution lane '{lane.DebugName}'.");

		perDrawBindings = bufferBindings.ToPerDrawBindings();
		_lanePassBindings[executionIndex] = passBindings;
		_lanePerDrawBindings[executionIndex] = perDrawBindings;
		return passBindings;
	}
}
