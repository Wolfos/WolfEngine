using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering;

/// <summary>
/// Groups the instances in the shared draw tables into batches that each draw with one indirect record. A batch
/// holds every instance that executes in the same lane with the same mesh, so its record draws them all as
/// instances of one draw; culling appends each visible member to the batch's slice of the visible instance list.
/// </summary>
/// <remarks>
/// Alpha-blended instances get a batch each, so blending keeps a stable order between them.
/// <para>
/// A batch's index is the draw index of its records. Records depend only on the batch's lane, mesh and capacity,
/// so they are created, re-encoded or reset only when a batch is created, changes capacity or dies, once per batch
/// per frame however many members moved. Visible list offsets are prefix sums of the capacities and live only in
/// the batch table, so moving them never touches a record.
/// </para>
/// <para>
/// An instance whose mesh has coarser levels of detail is also a member of one batch per coarser level, keyed by the
/// same lane and that level's mesh, so its slot exists in whichever level the cull picks. The level of detail table
/// tells the cull, per level 0 batch, which batch draws each level. See docs/mesh-lod.md.
/// </para>
/// <para>Render thread only.</para>
/// </remarks>
internal sealed class GpuDrawBatches : ILiveDrawSource
{
	private const int NoBatch = 0;
	private const int InitialSlotCount = 1024;

	private readonly Action<Mesh> _ensureMeshResident;
	private readonly GpuDrawHandlePool _batchPool;
	private readonly Dictionary<BatchKey, int> _batchIndicesByKey = new();
	private readonly int _maxInstanceCount;
	private readonly List<int> _dirtyBatchIndices = new();
	// Indexed by batch index, and grown as the pool hands out higher indices.
	private Batch[] _batches;
	private bool[] _batchDirty;
	private GpuDrawBatchData[] _batchTable;
	private GpuDrawBatchLodData[] _batchLodTable;
	// Indexed by instance index, and grown as higher instances are assigned.
	private Member[] _members;
	private int _batchCount;
	private int _maxBatchIndex;
	private bool _maxBatchIndexDirty;
	private bool _batchTableDirty = true;
	private uint _totalCapacity;

	public GpuDrawBatches(Action<Mesh> ensureMeshResident)
		: this(ensureMeshResident, GpuDrawResources.MaxDrawCount, GpuDrawResources.MaxInstanceCount)
	{
	}

	/// <param name="maxBatchCount">Batch table size, including the reserved index zero.</param>
	/// <param name="maxInstanceCount">Instance table size, including the reserved index zero.</param>
	internal GpuDrawBatches(Action<Mesh> ensureMeshResident, int maxBatchCount, int maxInstanceCount)
	{
		_ensureMeshResident = ensureMeshResident ?? throw new ArgumentNullException(nameof(ensureMeshResident));
		_batchPool = new GpuDrawHandlePool(maxBatchCount - 1);
		_maxInstanceCount = maxInstanceCount;
		var initialSlotCount = Math.Min(InitialSlotCount, Math.Min(maxBatchCount, maxInstanceCount));
		_batches = new Batch[initialSlotCount];
		_batchDirty = new bool[initialSlotCount];
		_batchTable = new GpuDrawBatchData[initialSlotCount];
		_batchLodTable = new GpuDrawBatchLodData[initialSlotCount];
		_members = new Member[initialSlotCount];
	}

	/// <summary>Raised whenever the batch table changes, so each frame slot knows to upload it again.</summary>
	public ulong Version { get; private set; } = 1;

	/// <summary>Live batches.</summary>
	public int BatchCount => _batchCount;

	/// <summary>Members across every live batch, which is the length of one view's visible instance list.</summary>
	public uint TotalCapacity => _totalCapacity;

	/// <summary>One past the highest live batch index; draw records and per-batch dispatches stop there.</summary>
	public uint ActiveBatchUpperBound
	{
		get
		{
			if (_maxBatchIndexDirty)
			{
				_maxBatchIndex = 0;
				for (var batchIndex = _batches.Length - 1; batchIndex > 0; batchIndex--)
				{
					if (_batches[batchIndex].Capacity > 0)
					{
						_maxBatchIndex = batchIndex;
						break;
					}
				}

				_maxBatchIndexDirty = false;
			}

			return (uint)(_maxBatchIndex + 1);
		}
	}

	/// <summary>
	/// Puts an instance in the batch for its lane and mesh, moving it out of the batch it was in before.
	/// </summary>
	/// <returns>The batch index, or zero when the instance cannot be drawn by any lane.</returns>
	public uint Assign(in GpuDrawHandle instanceHandle, in GpuDrawExecutionKey executionKey, Mesh mesh)
	{
		ArgumentNullException.ThrowIfNull(mesh);
		var instanceIndex = ValidateInstance(instanceHandle);

		if (GpuDrawClassification.SupportsMeshBackedGeometry(executionKey.DrawKind) == false ||
		    GpuDrawExecutionLanes.TryGetDefinition(executionKey, out var lane) == false)
		{
			Remove(instanceHandle);
			return NoBatch;
		}

		var key = new BatchKey(
			executionKey,
			mesh,
			executionKey.BucketId == GpuDrawBucketId.AlphaBlend ? instanceIndex : 0);
		EnsureMemberSlot(instanceIndex);
		ref var member = ref _members[instanceIndex];
		if (member.BatchIndex != NoBatch)
		{
			if (member.InstanceHandle.Equals(instanceHandle) && _batches[member.BatchIndex].Key == key)
			{
				return (uint)member.BatchIndex;
			}

			LeaveBatch(ref member);
		}

		var batchIndex = JoinBatch(key, lane.ExecutionIndex);
		if (batchIndex == NoBatch)
		{
			return NoBatch;
		}

		member = new Member(instanceHandle, batchIndex, JoinLevelBatches(key, lane.ExecutionIndex, mesh));
		return (uint)batchIndex;
	}

	/// <summary>
	/// Joins the batch for each coarser level of the mesh, finest first. Stops at the first level whose batch cannot be
	/// created, so the cull only ever picks levels the instance has a slot in.
	/// </summary>
	private int[]? JoinLevelBatches(in BatchKey levelZeroKey, int laneIndex, Mesh mesh)
	{
		var levels = mesh.LodLevels;
		var coarserLevelCount = Math.Min(levels.Count, GpuDrawBatchLodData.MaxLevelCount - 1);
		if (coarserLevelCount == 0)
		{
			return null;
		}

		var levelBatchIndices = new List<int>(coarserLevelCount);
		for (var level = 0; level < coarserLevelCount; level++)
		{
			var levelBatchIndex = JoinBatch(levelZeroKey with { Mesh = levels[level] }, laneIndex);
			if (levelBatchIndex == NoBatch)
			{
				break;
			}

			levelBatchIndices.Add(levelBatchIndex);
		}

		return levelBatchIndices.Count > 0 ? levelBatchIndices.ToArray() : null;
	}

	private int JoinBatch(in BatchKey key, int laneIndex)
	{
		if (_batchIndicesByKey.TryGetValue(key, out var batchIndex) == false)
		{
			batchIndex = CreateBatch(key, laneIndex);
			if (batchIndex == NoBatch)
			{
				return NoBatch;
			}
		}

		ref var batch = ref _batches[batchIndex];
		batch.Capacity++;
		_totalCapacity++;
		MarkDirty(batchIndex);
		return batchIndex;
	}

	/// <summary>Takes an instance out of its batch. Ignored when the slot now holds a newer instance.</summary>
	public void Remove(in GpuDrawHandle instanceHandle)
	{
		var instanceIndex = ValidateInstance(instanceHandle);
		if (instanceIndex >= _members.Length)
		{
			return;
		}

		ref var member = ref _members[instanceIndex];
		if (member.BatchIndex == NoBatch || member.InstanceHandle.Equals(instanceHandle) == false)
		{
			return;
		}

		LeaveBatch(ref member);
	}

	/// <summary>
	/// True when the instance's slot is batched under a different handle, meaning an update for
	/// <paramref name="instanceHandle"/> is older than the instance now in the slot and must not be applied.
	/// </summary>
	public bool IsSuperseded(in GpuDrawHandle instanceHandle)
	{
		var instanceIndex = ValidateInstance(instanceHandle);
		if (instanceIndex >= _members.Length)
		{
			return false;
		}

		ref readonly var member = ref _members[instanceIndex];
		return member.BatchIndex != NoBatch && member.InstanceHandle.Equals(instanceHandle) == false;
	}

	/// <returns>The batch an instance is in, or zero when it is in none.</returns>
	public uint GetBatchIndex(int instanceIndex)
	{
		if ((uint)instanceIndex >= (uint)_members.Length)
		{
			return NoBatch;
		}

		return (uint)_members[instanceIndex].BatchIndex;
	}

	/// <summary>Members of a live batch, which its records draw as instances.</summary>
	public uint GetCapacity(int batchIndex) =>
		(uint)batchIndex < (uint)_batches.Length ? _batches[batchIndex].Capacity : 0u;

	/// <summary>Empties every batch. Each batch that was live is reported as removed by the next publish.</summary>
	public void Clear()
	{
		for (var batchIndex = 1; batchIndex < _batches.Length; batchIndex++)
		{
			if (_batches[batchIndex].Capacity == 0)
			{
				continue;
			}

			_batches[batchIndex].Capacity = 0;
			_batchPool.Release(_batches[batchIndex].Handle);
			MarkDirty(batchIndex);
		}

		Array.Clear(_members);
		_batchIndicesByKey.Clear();
		_batchCount = 0;
		_totalCapacity = 0;
		_maxBatchIndex = 0;
		_maxBatchIndexDirty = false;
		_batchTableDirty = true;
		Version++;
	}

	/// <summary>
	/// Records one structural change per batch that changed since the last publish: an add carrying the batch's
	/// capacity for a live batch, a remove for one that died.
	/// </summary>
	public void PublishStructuralChanges(IndirectDrawRecords records)
	{
		ArgumentNullException.ThrowIfNull(records);
		for (var i = 0; i < _dirtyBatchIndices.Count; i++)
		{
			var batchIndex = _dirtyBatchIndices[i];
			_batchDirty[batchIndex] = false;
			ref readonly var batch = ref _batches[batchIndex];
			if (batch.Capacity == 0)
			{
				records.RecordStructuralChange(
					GpuDrawUpdateType.Remove,
					batch.Handle,
					batch.Key.ExecutionKey,
					mesh: null,
					instanceCount: 0);
				continue;
			}

			records.RecordStructuralChange(
				GpuDrawUpdateType.Add,
				batch.Handle,
				batch.Key.ExecutionKey,
				batch.Key.Mesh,
				batch.Capacity);
		}

		_dirtyBatchIndices.Clear();
	}

	/// <summary>
	/// The batch table, indexed by batch index up to <see cref="ActiveBatchUpperBound"/>. Visible offsets are
	/// recomputed here when membership changed since the last call.
	/// </summary>
	public ReadOnlySpan<GpuDrawBatchData> GetBatchTable()
	{
		RebuildTablesIfDirty();
		return _batchTable.AsSpan(0, (int)ActiveBatchUpperBound);
	}

	/// <summary>
	/// The level of detail table, indexed like <see cref="GetBatchTable"/>: for each level 0 batch, the batch drawing
	/// each level and the screen sizes it switches at.
	/// </summary>
	public ReadOnlySpan<GpuDrawBatchLodData> GetBatchLodTable()
	{
		RebuildTablesIfDirty();
		return _batchLodTable.AsSpan(0, (int)ActiveBatchUpperBound);
	}

	private void RebuildTablesIfDirty()
	{
		if (_batchTableDirty == false)
		{
			return;
		}

		var upperBound = (int)ActiveBatchUpperBound;
		var visibleOffset = 0u;
		_batchTable[0] = default;
		_batchLodTable[0] = default;
		for (var batchIndex = 1; batchIndex < upperBound; batchIndex++)
		{
			ref readonly var batch = ref _batches[batchIndex];
			_batchTable[batchIndex] = new GpuDrawBatchData(visibleOffset, batch.Capacity, (uint)batch.LaneIndex);
			_batchLodTable[batchIndex] = batch.Capacity > 0
				? CreateLodEntry(batchIndex, batch.Key)
				: default;
			visibleOffset += batch.Capacity;
		}

		_batchTableDirty = false;
	}

	private GpuDrawBatchLodData CreateLodEntry(int batchIndex, in BatchKey key)
	{
		var entry = new GpuDrawBatchLodData();
		entry.LevelBatches[0] = (uint)batchIndex;
		entry.LevelCount = 1;

		var levels = key.Mesh.LodLevels;
		var screenSizes = key.Mesh.LodScreenSizes;
		var coarserLevelCount = Math.Min(levels.Count, GpuDrawBatchLodData.MaxLevelCount - 1);
		for (var level = 0; level < coarserLevelCount; level++)
		{
			// Every member of this batch joined the level batches too, so they exist while it does. One that
			// could not be created ends the chain here, as it did for the members.
			if (_batchIndicesByKey.TryGetValue(key with { Mesh = levels[level] }, out var levelBatchIndex) == false)
			{
				break;
			}

			entry.LevelBatches[level + 1] = (uint)levelBatchIndex;
			entry.ScreenSizes[level] = screenSizes[level];
			entry.LevelCount++;
		}

		return entry;
	}

	public bool IsCurrent(in GpuDrawHandle drawHandle) => _batchPool.IsCurrent(drawHandle);

	public void CollectResidentDraws(List<LiveDraw> destination)
	{
		ArgumentNullException.ThrowIfNull(destination);
		destination.Clear();
		var upperBound = (int)ActiveBatchUpperBound;
		for (var batchIndex = 1; batchIndex < upperBound; batchIndex++)
		{
			ref readonly var batch = ref _batches[batchIndex];
			if (batch.Capacity == 0)
			{
				continue;
			}

			_ensureMeshResident(batch.Key.Mesh);
			destination.Add(new LiveDraw((uint)batchIndex, batch.Key.ExecutionKey, batch.Key.Mesh, batch.Capacity));
		}
	}

	public void EnsureResident(Mesh mesh)
	{
		ArgumentNullException.ThrowIfNull(mesh);
		_ensureMeshResident(mesh);
	}

	private int CreateBatch(in BatchKey key, int laneIndex)
	{
		if (_batchPool.TryAcquire(out var handle) == false)
		{
			return NoBatch;
		}

		var batchIndex = handle.Index;
		EnsureBatchSlot(batchIndex);
		_batches[batchIndex] = new Batch
		{
			Handle = handle,
			Key = key,
			LaneIndex = laneIndex,
			Capacity = 0
		};
		_batchIndicesByKey.Add(key, batchIndex);
		_batchCount++;
		if (batchIndex > _maxBatchIndex)
		{
			_maxBatchIndex = batchIndex;
		}

		return batchIndex;
	}

	private void LeaveBatch(ref Member member)
	{
		var batchIndex = member.BatchIndex;
		var levelBatchIndices = member.LevelBatchIndices;
		member = default;
		LeaveBatchIndex(batchIndex);
		if (levelBatchIndices is null)
		{
			return;
		}

		foreach (var levelBatchIndex in levelBatchIndices)
		{
			LeaveBatchIndex(levelBatchIndex);
		}
	}

	private void LeaveBatchIndex(int batchIndex)
	{
		ref var batch = ref _batches[batchIndex];
		batch.Capacity--;
		_totalCapacity--;
		MarkDirty(batchIndex);
		if (batch.Capacity > 0)
		{
			return;
		}

		// The handle is kept so the publish can still name the batch it removes.
		_batchIndicesByKey.Remove(batch.Key);
		_batchPool.Release(batch.Handle);
		_batchCount--;
		if (batchIndex == _maxBatchIndex)
		{
			_maxBatchIndexDirty = true;
		}
	}

	private void MarkDirty(int batchIndex)
	{
		_batchTableDirty = true;
		Version++;
		if (_batchDirty[batchIndex])
		{
			return;
		}

		_batchDirty[batchIndex] = true;
		_dirtyBatchIndices.Add(batchIndex);
	}

	private void EnsureBatchSlot(int batchIndex)
	{
		if (batchIndex < _batches.Length)
		{
			return;
		}

		var length = GrowLength(_batches.Length, batchIndex);
		Array.Resize(ref _batches, length);
		Array.Resize(ref _batchDirty, length);
		Array.Resize(ref _batchTable, length);
		Array.Resize(ref _batchLodTable, length);
	}

	private void EnsureMemberSlot(int instanceIndex)
	{
		if (instanceIndex < _members.Length)
		{
			return;
		}

		Array.Resize(ref _members, GrowLength(_members.Length, instanceIndex));
	}

	private static int GrowLength(int length, int index)
	{
		var grown = Math.Max(length, 1);
		while (grown <= index)
		{
			grown *= 2;
		}

		return grown;
	}

	private int ValidateInstance(in GpuDrawHandle instanceHandle)
	{
		var instanceIndex = instanceHandle.Index;
		if (instanceHandle.IsValid == false || instanceIndex == 0 || instanceIndex >= _maxInstanceCount)
		{
			throw new ArgumentOutOfRangeException(
				nameof(instanceHandle),
				instanceHandle,
				"Batched instances need a valid instance handle inside the instance table.");
		}

		return instanceIndex;
	}

	private readonly record struct BatchKey(GpuDrawExecutionKey ExecutionKey, Mesh Mesh, int UniqueInstanceIndex)
	{
		public bool Equals(BatchKey other) =>
			ExecutionKey == other.ExecutionKey &&
			ReferenceEquals(Mesh, other.Mesh) &&
			UniqueInstanceIndex == other.UniqueInstanceIndex;

		public override int GetHashCode() =>
			HashCode.Combine(
				ExecutionKey,
				System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Mesh),
				UniqueInstanceIndex);
	}

	private struct Batch
	{
		public GpuDrawHandle Handle;
		public BatchKey Key;
		public int LaneIndex;
		public uint Capacity;
	}

	/// <param name="LevelBatchIndices">The batches of the mesh's coarser levels, finest first, or null without any.</param>
	private readonly record struct Member(GpuDrawHandle InstanceHandle, int BatchIndex, int[]? LevelBatchIndices = null);
}
