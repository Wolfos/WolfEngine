namespace WolfEngine.Rendering;

/// <summary>
/// GPU table slots shared by every snapshot database in the same buffer.
/// </summary>
/// <remarks>
/// The instance, mesh, material and generation tables on the GPU are shared between the snapshots, so a
/// slot has to mean the same draw or resource whichever database wrote it last. The databases do not see
/// identical histories: an entity created and destroyed between two snapshots only ever reaches one of
/// them. Allocating per database let the two hand the same slot to different records, so slots are allocated
/// here instead, keyed by record and by resource, and released once no database references them.
/// <para>
/// Only the thread that builds frames touches this. Each database copies the state the render thread needs
/// when it finishes syncing.
/// </para>
/// </remarks>
internal sealed class GpuDrawHandleRegistry
{
	private readonly GpuDrawHandlePool _instanceHandlePool = new(GpuDrawResources.MaxInstanceCount - 1);
	private readonly GpuDrawHandlePool _meshHandlePool = new(GpuDrawResources.MaxMeshCount - 1);
	private readonly GpuDrawHandlePool _materialHandlePool = new(GpuDrawResources.MaxMaterialCount - 1);
	private readonly Dictionary<GpuDrawDatabase.DrawRecordKey, InstanceSlot> _instanceSlots = new();
	private readonly Dictionary<Mesh, ResourceSlot> _meshSlots = new(ReferenceEqualityComparer.Instance);
	private readonly Dictionary<Material, ResourceSlot> _materialSlots = new(ReferenceEqualityComparer.Instance);
	private int _maxInstanceIndex;
	private bool _maxInstanceIndexDirty;

	/// <summary>Changes whenever a generation table or the active instance range changes.</summary>
	public int Version { get; private set; }

	/// <returns>
	/// The instance slot every database shares for <paramref name="key"/>, or <see cref="GpuDrawHandle.Invalid"/>
	/// when the instance table is full. A database that got an invalid handle must not release the key.
	/// </returns>
	public GpuDrawHandle AcquireInstance(in GpuDrawDatabase.DrawRecordKey key)
	{
		if (_instanceSlots.TryGetValue(key, out var slot))
		{
			slot.RefCount++;
			_instanceSlots[key] = slot;
			return slot.InstanceHandle;
		}

		if (_instanceHandlePool.TryAcquire(out var instanceHandle) == false)
		{
			return GpuDrawHandle.Invalid;
		}

		slot = new InstanceSlot
		{
			InstanceHandle = instanceHandle,
			RefCount = 1
		};
		if (instanceHandle.Index > _maxInstanceIndex)
		{
			_maxInstanceIndex = instanceHandle.Index;
		}

		_instanceSlots[key] = slot;
		Version++;
		return instanceHandle;
	}

	public void ReleaseInstance(in GpuDrawDatabase.DrawRecordKey key)
	{
		if (_instanceSlots.TryGetValue(key, out var slot) == false)
		{
			return;
		}

		if (--slot.RefCount > 0)
		{
			_instanceSlots[key] = slot;
			return;
		}

		_instanceSlots.Remove(key);
		_instanceHandlePool.Release(slot.InstanceHandle);
		if (slot.InstanceHandle.Index == _maxInstanceIndex)
		{
			_maxInstanceIndexDirty = true;
		}

		Version++;
	}

	public GpuDrawHandle AcquireMesh(Mesh mesh) => AcquireResource(_meshSlots, _meshHandlePool, mesh);

	public void ReleaseMesh(Mesh mesh) => ReleaseResource(_meshSlots, _meshHandlePool, mesh);

	public GpuDrawHandle AcquireMaterial(Material material) => AcquireResource(_materialSlots, _materialHandlePool, material);

	public void ReleaseMaterial(Material material) => ReleaseResource(_materialSlots, _materialHandlePool, material);

	/// <summary>One past the highest instance index in use, so per-instance dispatches can stop there.</summary>
	public uint GetActiveInstanceUpperBound()
	{
		if (_instanceSlots.Count == 0)
		{
			_maxInstanceIndex = 0;
			_maxInstanceIndexDirty = false;
			return 1;
		}

		if (_maxInstanceIndexDirty)
		{
			var maxInstanceIndex = 0;
			foreach (var slot in _instanceSlots.Values)
			{
				if (slot.InstanceHandle.Index > maxInstanceIndex)
				{
					maxInstanceIndex = slot.InstanceHandle.Index;
				}
			}

			_maxInstanceIndex = maxInstanceIndex;
			_maxInstanceIndexDirty = false;
		}

		return (uint)(_maxInstanceIndex + 1);
	}

	public void CopyGenerationTables(
		List<uint> instanceGenerations,
		List<uint> meshGenerations,
		List<uint> materialGenerations)
	{
		_instanceHandlePool.WriteGenerations(instanceGenerations);
		_meshHandlePool.WriteGenerations(meshGenerations);
		_materialHandlePool.WriteGenerations(materialGenerations);
	}

	private GpuDrawHandle AcquireResource<T>(Dictionary<T, ResourceSlot> slots, GpuDrawHandlePool pool, T resource)
		where T : class
	{
		if (slots.TryGetValue(resource, out var slot))
		{
			slot.RefCount++;
		}
		else
		{
			slot = new ResourceSlot { Handle = pool.Acquire(), RefCount = 1 };
			Version++;
		}

		slots[resource] = slot;
		return slot.Handle;
	}

	private void ReleaseResource<T>(Dictionary<T, ResourceSlot> slots, GpuDrawHandlePool pool, T resource)
		where T : class
	{
		if (slots.TryGetValue(resource, out var slot) == false)
		{
			return;
		}

		if (--slot.RefCount > 0)
		{
			slots[resource] = slot;
			return;
		}

		slots.Remove(resource);
		pool.Release(slot.Handle);
		Version++;
	}

	private struct InstanceSlot
	{
		public GpuDrawHandle InstanceHandle;
		// Number of databases holding a record for this key.
		public int RefCount;
	}

	private struct ResourceSlot
	{
		public GpuDrawHandle Handle;
		// Number of databases referencing this resource.
		public int RefCount;
	}
}
