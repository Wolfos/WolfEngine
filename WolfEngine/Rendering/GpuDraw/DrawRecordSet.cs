using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering;

public readonly struct DrawRecordPage
{
	public DrawRecordPage(
		uint pageIndex,
		uint pageStartCommandIndex,
		uint pageCommandCapacity,
		IGfxIndirectCommandBuffer commandBuffer)
	{
		PageIndex = pageIndex;
		PageStartCommandIndex = pageStartCommandIndex;
		PageCommandCapacity = pageCommandCapacity;
		CommandBuffer = commandBuffer ?? throw new ArgumentNullException(nameof(commandBuffer));
	}

	public uint PageIndex { get; }
	public uint PageStartCommandIndex { get; }
	public uint PageCommandCapacity { get; }
	public IGfxIndirectCommandBuffer CommandBuffer { get; }
}

/// <summary>
/// The draw records of one pass for one view, across every frame slot and execution lane. Sets are created and
/// kept current by <see cref="IndirectDrawRecords"/>; passes only read their pages to execute them.
/// </summary>
public sealed class DrawRecordSet : IDisposable
{
	public const uint IndirectCommandPageCapacity = 2048;

	/// <summary>Highest page index a draw id can land on, which bounds the per-page count table.</summary>
	public static readonly uint MaxPageCount = ((GpuDrawResources.MaxDrawCount - 1) / IndirectCommandPageCapacity) + 1;

	private readonly SortedDictionary<uint, IGfxIndirectCommandBuffer>[] _commandPages =
		new SortedDictionary<uint, IGfxIndirectCommandBuffer>[GpuDrawResources.IndirectCommandBufferSlotCount * GpuDrawExecutionLanes.ExecutionLaneCount];
	private IGfxBuffer? _compactedExecutionRanges;
	private readonly ulong[] _appliedStructuralVersions = new ulong[GpuDrawResources.IndirectCommandBufferSlotCount];
	private readonly DrawRecordStalenessInputs[] _encodedStalenessInputs = new DrawRecordStalenessInputs[GpuDrawResources.IndirectCommandBufferSlotCount];
	private readonly ulong[] _encodedLaneGenerations = new ulong[GpuDrawResources.IndirectCommandBufferSlotCount];
	private readonly int[] _frameBindings = new int[GpuDrawResources.IndirectCommandBufferSlotCount];
	private readonly IDrawRecordEncoder _encoder;
	private MeshIndexStream _indexStream;

	internal DrawRecordSet(IDrawRecordEncoder encoder, DrawPassParticipation participation, ILaneBindingProvider lanes)
	{
		_encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
		Participation = participation;
		Lanes = lanes ?? throw new ArgumentNullException(nameof(lanes));
		Array.Fill(_frameBindings, -1);
		for (var i = 0; i < _commandPages.Length; i++)
		{
			_commandPages[i] = new SortedDictionary<uint, IGfxIndirectCommandBuffer>();
		}
	}

	/// <summary>
	/// Per-page compacted execution ranges, one entry per (slot, lane, page). Written by the compaction
	/// dispatch and read by the following execute to bound how many commands it walks.
	/// </summary>
	public IGfxBuffer? CompactedExecutionRangeBuffer => _compactedExecutionRanges;

	public void EnsureCreated(IGfxDevice device)
	{
		ArgumentNullException.ThrowIfNull(device);
		_compactedExecutionRanges ??= device.CreateBuffer(new BufferDescriptor(
			(ulong)CompactedExecutionRangeEntryCount * IndirectCompactionExecutionRange.StrideInBytes,
			BufferUsage.Indirect,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource,
			name: "SharedDrawCompactedExecutionRanges"));
	}

	/// <summary>The pass whose lanes this set's records draw.</summary>
	public DrawPassParticipation Participation { get; }

	internal ILaneBindingProvider Lanes { get; }

	internal void SetIndexStream(MeshIndexStream stream)
	{
		if (_indexStream == stream) return;
		_indexStream = stream;
		InvalidateEncoding();
	}

	public static int CompactedExecutionRangeEntryCount =>
		GpuDrawResources.IndirectCommandBufferSlotCount * GpuDrawExecutionLanes.ExecutionLaneCount * (int)MaxPageCount;

	public static int GetCompactedExecutionRangeIndex(int slotIndex, int executionLaneIndex, uint pageIndex)
	{
		ValidateSlot(slotIndex);
		ValidateLane(executionLaneIndex);
		ValidatePage(pageIndex);
		return (FlattenSlotLaneIndex(slotIndex, executionLaneIndex) * (int)MaxPageCount) + (int)pageIndex;
	}

	internal IGfxIndirectCommandBuffer EnsurePage(
		IGfxDevice device,
		int slotIndex,
		int executionLaneIndex,
		uint pageIndex)
	{
		ArgumentNullException.ThrowIfNull(device);
		ValidateSlot(slotIndex);
		ValidateLane(executionLaneIndex);
		ValidatePage(pageIndex);

		var pages = _commandPages[FlattenSlotLaneIndex(slotIndex, executionLaneIndex)];
		if (pages.TryGetValue(pageIndex, out var commandBuffer))
		{
			return commandBuffer;
		}

		var laneName = ResolveLaneDebugName(executionLaneIndex);
		commandBuffer = _encoder.CreatePage(
			device,
			IndirectCommandPageCapacity,
			$"{laneName} slot{slotIndex} page{pageIndex}");
		pages.Add(pageIndex, commandBuffer);
		return commandBuffer;
	}

	internal IGfxIndirectCommandBuffer EnsurePageForCommand(
		IGfxDevice device,
		int slotIndex,
		int executionLaneIndex,
		uint commandIndex,
		out uint pageCommandIndex)
	{
		pageCommandIndex = GetPageCommandIndex(commandIndex);
		return EnsurePage(device, slotIndex, executionLaneIndex, GetPageIndex(commandIndex));
	}

	public bool TryGetPage(
		int slotIndex,
		int executionLaneIndex,
		uint pageIndex,
		out IGfxIndirectCommandBuffer commandBuffer)
	{
		ValidateSlot(slotIndex);
		ValidateLane(executionLaneIndex);
		ValidatePage(pageIndex);
		return _commandPages[FlattenSlotLaneIndex(slotIndex, executionLaneIndex)].TryGetValue(pageIndex, out commandBuffer!);
	}

	public bool TryGetPageForCommand(
		int slotIndex,
		int executionLaneIndex,
		uint commandIndex,
		out IGfxIndirectCommandBuffer commandBuffer,
		out uint pageCommandIndex)
	{
		pageCommandIndex = GetPageCommandIndex(commandIndex);
		return TryGetPage(slotIndex, executionLaneIndex, GetPageIndex(commandIndex), out commandBuffer);
	}

	public DrawRecordPage[] GetAllocatedPages(int slotIndex, int executionLaneIndex)
	{
		ValidateSlot(slotIndex);
		ValidateLane(executionLaneIndex);

		var pages = _commandPages[FlattenSlotLaneIndex(slotIndex, executionLaneIndex)];
		if (pages.Count == 0)
		{
			return Array.Empty<DrawRecordPage>();
		}

		var result = new DrawRecordPage[pages.Count];
		var i = 0;
		foreach (var page in pages)
		{
			result[i++] = new DrawRecordPage(
				page.Key,
				page.Key * IndirectCommandPageCapacity,
				IndirectCommandPageCapacity,
				page.Value);
		}

		return result;
	}

	internal ulong GetAppliedStructuralVersion(int slotIndex)
	{
		ValidateSlot(slotIndex);
		return _appliedStructuralVersions[slotIndex];
	}

	internal void SetAppliedStructuralVersion(int slotIndex, ulong version)
	{
		ValidateSlot(slotIndex);
		_appliedStructuralVersions[slotIndex] = version;
	}

	/// <param name="stalenessInputs">
	/// The staleness inputs as they stand right now. A slot encoded against older inputs may hold the identity of
	/// buffers that have since been replaced, and must be re-encoded before it is executed.
	/// </param>
	/// <param name="laneGeneration">The <see cref="ILaneBindingProvider.Generation"/> of this set's lanes right now.</param>
	internal bool RequiresFullReencode(
		int slotIndex,
		int frameSlot,
		in DrawRecordStalenessInputs stalenessInputs,
		ulong laneGeneration)
	{
		ValidateSlot(slotIndex);
		return _frameBindings[slotIndex] != frameSlot ||
		       _encodedStalenessInputs[slotIndex] != stalenessInputs ||
		       _encodedLaneGenerations[slotIndex] != laneGeneration;
	}

	/// <summary>
	/// Forgets what every slot was encoded against, so each slot's next use re-encodes in full rather than
	/// replaying structural records. For a set that stopped being used: the replay log is compacted without
	/// waiting for it, so by the time it is used again the records it would need may be gone.
	/// </summary>
	internal void InvalidateEncoding()
	{
		Array.Fill(_frameBindings, -1);
		Array.Clear(_appliedStructuralVersions);
	}

	internal void MarkSlotEncoded(
		int slotIndex,
		int frameSlot,
		in DrawRecordStalenessInputs stalenessInputs,
		ulong laneGeneration,
		ulong structuralVersion)
	{
		ValidateSlot(slotIndex);
		_frameBindings[slotIndex] = frameSlot;
		_encodedStalenessInputs[slotIndex] = stalenessInputs;
		_encodedLaneGenerations[slotIndex] = laneGeneration;
		_appliedStructuralVersions[slotIndex] = structuralVersion;
	}

	public void Dispose()
	{
		(_compactedExecutionRanges as IDisposable)?.Dispose();
		_compactedExecutionRanges = null;
		for (var i = 0; i < _commandPages.Length; i++)
		{
			foreach (var commandBuffer in _commandPages[i].Values)
			{
				(commandBuffer as IDisposable)?.Dispose();
			}

			_commandPages[i].Clear();
		}
	}

	/// <summary>
	/// Lanes are stored in declaration order, which is not required to match their execution index, so
	/// resolve by the index rather than by position.
	/// </summary>
	private static string ResolveLaneDebugName(int executionLaneIndex)
	{
		var definitions = GpuDrawExecutionLanes.Definitions;
		for (var i = 0; i < definitions.Length; i++)
		{
			if (definitions[i].ExecutionIndex == executionLaneIndex)
			{
				return definitions[i].DebugName;
			}
		}

		return $"lane{executionLaneIndex}";
	}

	public static uint GetPageIndex(uint commandIndex) => commandIndex / IndirectCommandPageCapacity;

	public static uint GetPageCommandIndex(uint commandIndex) => commandIndex % IndirectCommandPageCapacity;

	private static int FlattenSlotLaneIndex(int slotIndex, int executionLaneIndex) =>
		(slotIndex * GpuDrawExecutionLanes.ExecutionLaneCount) + executionLaneIndex;

	private static void ValidateSlot(int slotIndex)
	{
		if (slotIndex < 0 || slotIndex >= GpuDrawResources.IndirectCommandBufferSlotCount)
		{
			throw new ArgumentOutOfRangeException(nameof(slotIndex), slotIndex, "Indirect command buffer slot is out of range.");
		}
	}

	private static void ValidateLane(int executionLaneIndex)
	{
		if (executionLaneIndex < 0 || executionLaneIndex >= GpuDrawExecutionLanes.ExecutionLaneCount)
		{
			throw new ArgumentOutOfRangeException(nameof(executionLaneIndex), executionLaneIndex, "Shared draw execution lane index is out of range.");
		}
	}

	private static void ValidatePage(uint pageIndex)
	{
		var maxPageIndex = (uint)((GpuDrawResources.MaxDrawCount - 1) / IndirectCommandPageCapacity);
		if (pageIndex > maxPageIndex)
		{
			throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex, "Indirect command page index is out of range.");
		}
	}
}
