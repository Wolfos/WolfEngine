namespace WolfEngine.Rendering;

/// <summary>
/// GPU table slots shared by every snapshot database in the same buffer.
/// </summary>
/// <remarks>
/// The instance, mesh, material and generation tables on the GPU are shared between the snapshots, so a
/// slot has to mean the same draw or resource whichever database wrote it last. The databases do not see
/// identical histories: an entity created and destroyed between two snapshots only ever reaches one of
/// them. Allocating per database let the two hand the same slot to different draws, so slots are allocated
/// here instead, keyed by draw and by resource, and released once no database references them.
/// <para>
/// Only the thread that builds frames touches this. Each database copies the state the render thread needs
/// when it finishes syncing.
/// </para>
/// </remarks>
internal sealed class GpuDrawHandleRegistry
{
	private readonly GpuDrawHandlePool _drawHandlePool = new(GpuDrawResources.MaxDrawCount - 1);
	private readonly GpuDrawHandlePool _instanceHandlePool = new(GpuDrawResources.MaxInstanceCount - 1);
	private readonly GpuDrawHandlePool _meshHandlePool = new(GpuDrawResources.MaxMeshCount - 1);
	private readonly GpuDrawHandlePool _materialHandlePool = new(GpuDrawResources.MaxMaterialCount - 1);
	private readonly Dictionary<GpuDrawDatabase.DrawRecordKey, DrawSlot> _drawSlots = new();
	private readonly Dictionary<Mesh, ResourceSlot> _meshSlots = new(ReferenceEqualityComparer.Instance);
	private readonly Dictionary<Material, ResourceSlot> _materialSlots = new(ReferenceEqualityComparer.Instance);
	private int _maxDrawIndex;
	private bool _maxDrawIndexDirty;

	/// <summary>Changes whenever a generation table or the active draw range changes.</summary>
	public int Version { get; private set; }

	public void AcquireDraw(in GpuDrawDatabase.DrawRecordKey key, out GpuDrawHandle drawHandle, out GpuDrawHandle instanceHandle)
	{
		if (_drawSlots.TryGetValue(key, out var slot))
		{
			slot.RefCount++;
		}
		else
		{
			slot = new DrawSlot
			{
				DrawHandle = _drawHandlePool.Acquire(),
				InstanceHandle = _instanceHandlePool.Acquire(),
				RefCount = 1
			};
			if (slot.DrawHandle.Index > _maxDrawIndex)
			{
				_maxDrawIndex = slot.DrawHandle.Index;
			}

			Version++;
		}

		_drawSlots[key] = slot;
		drawHandle = slot.DrawHandle;
		instanceHandle = slot.InstanceHandle;
	}

	public void ReleaseDraw(in GpuDrawDatabase.DrawRecordKey key)
	{
		if (_drawSlots.TryGetValue(key, out var slot) == false)
		{
			return;
		}

		if (--slot.RefCount > 0)
		{
			_drawSlots[key] = slot;
			return;
		}

		_drawSlots.Remove(key);
		_drawHandlePool.Release(slot.DrawHandle);
		_instanceHandlePool.Release(slot.InstanceHandle);
		if (slot.DrawHandle.Index == _maxDrawIndex)
		{
			_maxDrawIndexDirty = true;
		}

		Version++;
	}

	public GpuDrawHandle AcquireMesh(Mesh mesh) => AcquireResource(_meshSlots, _meshHandlePool, mesh);

	public void ReleaseMesh(Mesh mesh) => ReleaseResource(_meshSlots, _meshHandlePool, mesh);

	public GpuDrawHandle AcquireMaterial(Material material) => AcquireResource(_materialSlots, _materialHandlePool, material);

	public void ReleaseMaterial(Material material) => ReleaseResource(_materialSlots, _materialHandlePool, material);

	public uint GetActiveDrawCommandUpperBound()
	{
		if (_drawSlots.Count == 0)
		{
			_maxDrawIndex = 0;
			_maxDrawIndexDirty = false;
			return 1;
		}

		if (_maxDrawIndexDirty)
		{
			var maxDrawIndex = 0;
			foreach (var slot in _drawSlots.Values)
			{
				if (slot.DrawHandle.Index > maxDrawIndex)
				{
					maxDrawIndex = slot.DrawHandle.Index;
				}
			}

			_maxDrawIndex = maxDrawIndex;
			_maxDrawIndexDirty = false;
		}

		return (uint)(_maxDrawIndex + 1);
	}

	public void CopyGenerationTables(
		List<uint> drawGenerations,
		List<uint> instanceGenerations,
		List<uint> meshGenerations,
		List<uint> materialGenerations)
	{
		_drawHandlePool.WriteGenerations(drawGenerations);
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

	private struct DrawSlot
	{
		public GpuDrawHandle DrawHandle;
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
