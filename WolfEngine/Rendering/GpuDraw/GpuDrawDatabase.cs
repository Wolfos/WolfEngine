#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using WolfEngine.ECS;

namespace WolfEngine.Rendering;

public sealed class GpuDrawDatabase
{
	private readonly Dictionary<Entity, DrawRecord> _records = new(new EntityComparer());
	private readonly Dictionary<Mesh, ResourceId> _meshHandles = new(new ReferenceComparer<Mesh>());
	private readonly Dictionary<Material, ResourceId> _materialHandles = new(new ReferenceComparer<Material>());
	private readonly List<GpuDrawUpdate> _updates = new();
	private readonly GpuDrawHandlePool _drawHandlePool = new(GpuDrawResources.MaxDrawCount - 1);
	private readonly GpuDrawHandlePool _instanceHandlePool = new(GpuDrawResources.MaxInstanceCount - 1);
	private readonly GpuDrawHandlePool _meshHandlePool = new(GpuDrawResources.MaxMeshCount - 1);
	private readonly GpuDrawHandlePool _materialHandlePool = new(GpuDrawResources.MaxMaterialCount - 1);
	private int _syncStamp;
	private int _maxActiveDrawIndex;
	private bool _maxActiveDrawIndexDirty;

	public void BeginSync()
	{
		_syncStamp++;
	}

	public void ResetForSnapshotWrite()
	{
		_updates.Clear();
		_syncStamp++;
	}

	public void Touch(Entity entity, Mesh mesh, Material material, in Matrix4x4 worldTransform)
	{
		if (_records.TryGetValue(entity, out var record))
		{
			ApplyChanges(record, mesh, material, worldTransform);
			record.LastSeenStamp = _syncStamp;
			return;
		}

		var newRecord = CreateRecord(entity, mesh, material, worldTransform);
		_records.Add(entity, newRecord);
		if (newRecord.DrawHandle.Index > _maxActiveDrawIndex)
		{
			_maxActiveDrawIndex = newRecord.DrawHandle.Index;
		}

		_updates.Add(GpuDrawUpdate.CreateAdd(
			newRecord.DrawHandle,
			newRecord.InstanceHandle,
			newRecord.MeshHandle,
			newRecord.MaterialHandle,
			newRecord.PreviousWorld,
			newRecord.World,
			newRecord.BoundsCenterRadius,
			mesh,
			material));
	}

	public void EndSync()
	{
		var toRemove = new List<Entity>();
		foreach (var (entity, record) in _records)
		{
			if (record.LastSeenStamp != _syncStamp)
			{
				toRemove.Add(entity);
			}
		}

		foreach (var entity in toRemove)
		{
			if (_records.TryGetValue(entity, out var record) == false)
			{
				continue;
			}

			_records.Remove(entity);
			_updates.Add(GpuDrawUpdate.CreateRemove(record.DrawHandle, record.InstanceHandle));
			ReleaseRecord(record);
			if (record.DrawHandle.Index == _maxActiveDrawIndex)
			{
				_maxActiveDrawIndexDirty = true;
			}
		}
	}

	public void NotifyMaterialChanged(Material material)
	{
		ArgumentNullException.ThrowIfNull(material);

		foreach (var record in _records.Values)
		{
			if (ReferenceEquals(record.Material, material) == false)
			{
				continue;
			}

			_updates.Add(GpuDrawUpdate.CreateMaterialUpdate(
				record.DrawHandle,
				record.InstanceHandle,
				record.MeshHandle,
				record.MaterialHandle,
				record.World,
				record.World,
				record.BoundsCenterRadius,
				record.Mesh,
				material));
		}
	}

	public void CollectDrawEntries(List<GpuDrawEntry> destination)
	{
		destination.Clear();
		foreach (var record in _records.Values)
		{
			destination.Add(new GpuDrawEntry(
				record.DrawHandle,
				record.InstanceHandle,
				record.MeshHandle,
				record.MaterialHandle,
				record.Mesh,
				record.Material,
				record.PreviousWorld,
				record.World,
				record.BoundsCenterRadius));
		}
	}

	public void ConsumeUpdates(List<GpuDrawUpdate> destination)
	{
		destination.Clear();
		destination.AddRange(_updates);
		_updates.Clear();
	}

	public uint GetActiveDrawCommandUpperBound()
	{
		if (_records.Count == 0)
		{
			_maxActiveDrawIndex = 0;
			_maxActiveDrawIndexDirty = false;
			return 1;
		}

		if (_maxActiveDrawIndexDirty)
		{
			var maxDrawIndex = 0;
			foreach (var record in _records.Values)
			{
				if (record.DrawHandle.Index > maxDrawIndex)
				{
					maxDrawIndex = record.DrawHandle.Index;
				}
			}

			_maxActiveDrawIndex = maxDrawIndex;
			_maxActiveDrawIndexDirty = false;
		}

		return (uint)(_maxActiveDrawIndex + 1);
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

	public GpuDrawHandle FallbackMeshHandle => _meshHandlePool.FallbackHandle;

	public GpuDrawHandle FallbackMaterialHandle => _materialHandlePool.FallbackHandle;

	public bool IsCurrentDrawHandle(in GpuDrawHandle handle) => _drawHandlePool.IsCurrent(handle);

	private void ApplyChanges(DrawRecord record, Mesh mesh, Material material, in Matrix4x4 worldTransform)
	{
		var transformChanged = record.World.Equals(worldTransform) == false;
		var meshChanged = ReferenceEquals(record.Mesh, mesh) == false;
		var materialChanged = ReferenceEquals(record.Material, material) == false;
		var materialResourceChanged = materialChanged == false && record.MaterialResourceRevision != material.ResourceRevision;
		var settlePreviousTransform = transformChanged == false && record.PreviousWorld.Equals(record.World) == false;

		if ((transformChanged || meshChanged || materialChanged || materialResourceChanged || settlePreviousTransform) == false)
		{
			return;
		}

		var uploadPreviousWorld = record.World;

		if (meshChanged)
		{
			ReleaseMesh(record.MeshHandle, record.Mesh);
			record.Mesh = mesh;
			record.MeshHandle = AcquireMeshHandle(mesh);
		}

		if (materialChanged)
		{
			ReleaseMaterial(record.MaterialHandle, record.Material);
			record.Material = material;
			record.MaterialHandle = AcquireMaterialHandle(material);
			record.MaterialResourceRevision = material.ResourceRevision;
		}
		else if (materialResourceChanged)
		{
			record.MaterialResourceRevision = material.ResourceRevision;
		}

		if (transformChanged || meshChanged)
		{
			record.World = worldTransform;
			ComputeBounds(record, mesh);
		}

		if (meshChanged)
		{
			_updates.Add(GpuDrawUpdate.CreateMeshUpdate(
				record.DrawHandle,
				record.InstanceHandle,
				record.MeshHandle,
				record.MaterialHandle,
				record.World,
				record.World,
				record.BoundsCenterRadius,
				mesh));
		}

		if (materialChanged || materialResourceChanged)
		{
			_updates.Add(GpuDrawUpdate.CreateMaterialUpdate(
				record.DrawHandle,
				record.InstanceHandle,
				record.MeshHandle,
				record.MaterialHandle,
				record.World,
				record.World,
				record.BoundsCenterRadius,
				record.Mesh,
				material));
		}

		if (transformChanged)
		{
			_updates.Add(GpuDrawUpdate.CreateTransformUpdate(
				record.DrawHandle,
				record.InstanceHandle,
				record.MeshHandle,
				record.MaterialHandle,
				uploadPreviousWorld,
				record.World,
				record.BoundsCenterRadius));
			record.PreviousWorld = uploadPreviousWorld;
		}

		if (settlePreviousTransform)
		{
			_updates.Add(GpuDrawUpdate.CreateTransformUpdate(
				record.DrawHandle,
				record.InstanceHandle,
				record.MeshHandle,
				record.MaterialHandle,
				record.World,
				record.World,
				record.BoundsCenterRadius));
			record.PreviousWorld = record.World;
		}
	}

	private DrawRecord CreateRecord(Entity entity, Mesh mesh, Material material, in Matrix4x4 worldTransform)
	{
		var record = new DrawRecord
		{
			Entity = entity,
			DrawHandle = _drawHandlePool.Acquire(),
			InstanceHandle = _instanceHandlePool.Acquire(),
			Mesh = mesh,
			Material = material,
			MeshHandle = AcquireMeshHandle(mesh),
			MaterialHandle = AcquireMaterialHandle(material),
			MaterialResourceRevision = material.ResourceRevision,
			World = worldTransform,
			PreviousWorld = worldTransform,
			LastSeenStamp = _syncStamp
		};

		ComputeBounds(record, mesh);
		return record;
	}

	private void ReleaseRecord(DrawRecord record)
	{
		_drawHandlePool.Release(record.DrawHandle);
		_instanceHandlePool.Release(record.InstanceHandle);
		ReleaseMesh(record.MeshHandle, record.Mesh);
		ReleaseMaterial(record.MaterialHandle, record.Material);
	}

	private GpuDrawHandle AcquireMeshHandle(Mesh mesh)
	{
		if (_meshHandles.TryGetValue(mesh, out var entry))
		{
			entry.RefCount++;
			_meshHandles[mesh] = entry;
			return entry.Handle;
		}

		var handle = _meshHandlePool.Acquire();
		_meshHandles[mesh] = new ResourceId(handle, 1);
		return handle;
	}

	private void ReleaseMesh(GpuDrawHandle handle, Mesh mesh)
	{
		if (_meshHandles.TryGetValue(mesh, out var entry) == false)
		{
			return;
		}

		entry.RefCount--;
		if (entry.RefCount > 0)
		{
			_meshHandles[mesh] = entry;
			return;
		}

		_meshHandles.Remove(mesh);
		_meshHandlePool.Release(handle);
	}

	private GpuDrawHandle AcquireMaterialHandle(Material material)
	{
		if (_materialHandles.TryGetValue(material, out var entry))
		{
			entry.RefCount++;
			_materialHandles[material] = entry;
			return entry.Handle;
		}

		var handle = _materialHandlePool.Acquire();
		_materialHandles[material] = new ResourceId(handle, 1);
		return handle;
	}

	private void ReleaseMaterial(GpuDrawHandle handle, Material material)
	{
		if (_materialHandles.TryGetValue(material, out var entry) == false)
		{
			return;
		}

		entry.RefCount--;
		if (entry.RefCount > 0)
		{
			_materialHandles[material] = entry;
			return;
		}

		_materialHandles.Remove(material);
		_materialHandlePool.Release(handle);
	}

	private static void ComputeBounds(DrawRecord record, Mesh mesh)
	{
		var bounds = mesh.BoundingSphere;
		record.BoundsCenterRadius = new(Vector3.Transform(bounds.Center, record.World),
			bounds.Radius * GetMaxScale(record.World));
	}

	private static float GetMaxScale(in Matrix4x4 matrix)
	{
		var scaleX = new Vector3(matrix.M11, matrix.M12, matrix.M13).Length();
		var scaleY = new Vector3(matrix.M21, matrix.M22, matrix.M23).Length();
		var scaleZ = new Vector3(matrix.M31, matrix.M32, matrix.M33).Length();
		return MathF.Max(scaleX, MathF.Max(scaleY, scaleZ));
	}

	internal sealed class DrawRecord
	{
		public Entity Entity = default;
		public GpuDrawHandle DrawHandle;
		public GpuDrawHandle InstanceHandle;
		public GpuDrawHandle MeshHandle;
		public GpuDrawHandle MaterialHandle;
		public Mesh Mesh = null!;
		public Material Material = null!;
		public int MaterialResourceRevision;
		public Matrix4x4 PreviousWorld;
		public Matrix4x4 World;
		public Vector4 BoundsCenterRadius;
		public int LastSeenStamp;
	}

	private struct ResourceId
	{
		public ResourceId(GpuDrawHandle handle, int refCount)
		{
			Handle = handle;
			RefCount = refCount;
		}

		public GpuDrawHandle Handle { get; set; }

		public int RefCount { get; set; }
	}

	private sealed class EntityComparer : IEqualityComparer<Entity>
	{
		public bool Equals(Entity x, Entity y) => x.Equals(y);

		public int GetHashCode(Entity obj) => HashCode.Combine(obj.Index, obj.Generation);
	}

	private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
	{
		public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

		public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
	}
}

public readonly struct GpuDrawEntry
{
	public GpuDrawEntry(
		GpuDrawHandle drawHandle,
		GpuDrawHandle instanceHandle,
		GpuDrawHandle meshHandle,
		GpuDrawHandle materialHandle,
		Mesh mesh,
		Material material,
		Matrix4x4 previousWorld,
		Matrix4x4 world,
		Vector4 boundsCenterRadius)
	{
		DrawHandle = drawHandle;
		InstanceHandle = instanceHandle;
		MeshHandle = meshHandle;
		MaterialHandle = materialHandle;
		Mesh = mesh;
		Material = material;
		PreviousWorld = previousWorld;
		World = world;
		BoundsCenterRadius = boundsCenterRadius;
	}

	public GpuDrawHandle DrawHandle { get; }
	public GpuDrawHandle InstanceHandle { get; }
	public GpuDrawHandle MeshHandle { get; }
	public GpuDrawHandle MaterialHandle { get; }
	public Mesh Mesh { get; }
	public Material Material { get; }
	public Matrix4x4 PreviousWorld { get; }
	public Matrix4x4 World { get; }
	public Vector4 BoundsCenterRadius { get; }

	public int DrawIndex => DrawHandle.Index;
	public int InstanceIndex => InstanceHandle.Index;
	public int MeshIndex => MeshHandle.Index;
	public int MaterialIndex => MaterialHandle.Index;
}

public readonly struct GpuDrawUpdate
{
	private GpuDrawUpdate(
		GpuDrawUpdateType type,
		GpuDrawHandle drawHandle,
		GpuDrawHandle instanceHandle,
		GpuDrawHandle meshHandle,
		GpuDrawHandle materialHandle,
		Matrix4x4 previousWorld,
		Matrix4x4 world,
		Vector4 boundsCenterRadius,
		Mesh? mesh,
		Material? material)
	{
		Type = type;
		DrawHandle = drawHandle;
		InstanceHandle = instanceHandle;
		MeshHandle = meshHandle;
		MaterialHandle = materialHandle;
		PreviousWorld = previousWorld;
		World = world;
		BoundsCenterRadius = boundsCenterRadius;
		Mesh = mesh;
		Material = material;
	}

	public GpuDrawUpdateType Type { get; }
	public GpuDrawHandle DrawHandle { get; }
	public GpuDrawHandle InstanceHandle { get; }
	public GpuDrawHandle MeshHandle { get; }
	public GpuDrawHandle MaterialHandle { get; }
	public Matrix4x4 PreviousWorld { get; }
	public Matrix4x4 World { get; }
	public Vector4 BoundsCenterRadius { get; }
	public Mesh? Mesh { get; }
	public Material? Material { get; }

	public int DrawIndex => DrawHandle.Index;
	public int InstanceIndex => InstanceHandle.Index;
	public int MeshIndex => MeshHandle.Index;
	public int MaterialIndex => MaterialHandle.Index;

	public static GpuDrawUpdate CreateAdd(
		GpuDrawHandle drawHandle,
		GpuDrawHandle instanceHandle,
		GpuDrawHandle meshHandle,
		GpuDrawHandle materialHandle,
		in Matrix4x4 previousWorld,
		in Matrix4x4 world,
		Vector4 boundsCenterRadius,
		Mesh mesh,
		Material material)
	{
		return new GpuDrawUpdate(
			GpuDrawUpdateType.Add,
			drawHandle,
			instanceHandle,
			meshHandle,
			materialHandle,
			previousWorld,
			world,
			boundsCenterRadius,
			mesh,
			material);
	}

	public static GpuDrawUpdate CreateRemove(GpuDrawHandle drawHandle, GpuDrawHandle instanceHandle)
	{
		return new GpuDrawUpdate(
			GpuDrawUpdateType.Remove,
			drawHandle,
			instanceHandle,
			GpuDrawHandle.Invalid,
			GpuDrawHandle.Invalid,
			Matrix4x4.Identity,
			Matrix4x4.Identity,
			Vector4.Zero,
			null,
			null);
	}

	public static GpuDrawUpdate CreateTransformUpdate(
		GpuDrawHandle drawHandle,
		GpuDrawHandle instanceHandle,
		GpuDrawHandle meshHandle,
		GpuDrawHandle materialHandle,
		in Matrix4x4 previousWorld,
		in Matrix4x4 world,
		Vector4 boundsCenterRadius)
	{
		return new GpuDrawUpdate(
			GpuDrawUpdateType.UpdateTransform,
			drawHandle,
			instanceHandle,
			meshHandle,
			materialHandle,
			previousWorld,
			world,
			boundsCenterRadius,
			null,
			null);
	}

	public static GpuDrawUpdate CreateMaterialUpdate(
		GpuDrawHandle drawHandle,
		GpuDrawHandle instanceHandle,
		GpuDrawHandle meshHandle,
		GpuDrawHandle materialHandle,
		in Matrix4x4 previousWorld,
		in Matrix4x4 world,
		Vector4 boundsCenterRadius,
		Mesh mesh,
		Material material)
	{
		return new GpuDrawUpdate(
			GpuDrawUpdateType.UpdateMaterial,
			drawHandle,
			instanceHandle,
			meshHandle,
			materialHandle,
			previousWorld,
			world,
			boundsCenterRadius,
			mesh,
			material);
	}

	public static GpuDrawUpdate CreateMeshUpdate(
		GpuDrawHandle drawHandle,
		GpuDrawHandle instanceHandle,
		GpuDrawHandle meshHandle,
		GpuDrawHandle materialHandle,
		in Matrix4x4 previousWorld,
		in Matrix4x4 world,
		Vector4 boundsCenterRadius,
		Mesh mesh)
	{
		return new GpuDrawUpdate(
			GpuDrawUpdateType.UpdateMesh,
			drawHandle,
			instanceHandle,
			meshHandle,
			materialHandle,
			previousWorld,
			world,
			boundsCenterRadius,
			mesh,
			null);
	}
}

public enum GpuDrawUpdateType
{
	Add,
	Remove,
	UpdateTransform,
	UpdateMaterial,
	UpdateMesh
}
