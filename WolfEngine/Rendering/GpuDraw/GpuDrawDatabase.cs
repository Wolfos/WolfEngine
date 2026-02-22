#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using WolfEngine.ECS;

namespace WolfEngine.Rendering;

public sealed class GpuDrawDatabase
{
	private const bool DisableIdReuse = false;
	private readonly object _lock = new();
	private readonly Dictionary<Entity, DrawRecord> _records = new(new EntityComparer());
	private readonly Dictionary<Mesh, ResourceId> _meshIds = new(new ReferenceComparer<Mesh>());
	private readonly Dictionary<Material, ResourceId> _materialIds = new(new ReferenceComparer<Material>());
	private readonly Stack<int> _freeDrawIds = new();
	private readonly Stack<int> _freeInstanceIds = new();
	private readonly Stack<int> _freeMeshIds = new();
	private readonly Stack<int> _freeMaterialIds = new();
	private readonly List<GpuDrawUpdate> _updates = new();
	private int _nextDrawId = 1;
	private int _nextInstanceId = 1;
	private int _nextMeshId = 1;
	private int _nextMaterialId = 1;
	private int _syncStamp;
	private int _maxActiveDrawId;
	private bool _maxActiveDrawIdDirty;

	public void BeginSync()
	{
		lock (_lock)
		{
			_syncStamp++;
		}
	}

	public void Touch(Entity entity, Mesh mesh, Material material, in Matrix4x4 worldTransform)
	{
		lock (_lock)
		{
			if (_records.TryGetValue(entity, out var record))
			{
				ApplyChanges(record, mesh, material, worldTransform);
				record.LastSeenStamp = _syncStamp;
				return;
			}

			var newRecord = CreateRecord(entity, mesh, material, worldTransform);
			_records.Add(entity, newRecord);
			if (newRecord.DrawId > _maxActiveDrawId)
			{
				_maxActiveDrawId = newRecord.DrawId;
			}
			_updates.Add(GpuDrawUpdate.CreateAdd(
				newRecord.DrawId,
				newRecord.InstanceId,
				newRecord.MeshId,
				newRecord.MaterialId,
				newRecord.World,
				newRecord.boundsCenterRadius,
				mesh,
				material));
		}
	}

	public void EndSync()
	{
		lock (_lock)
		{
			var toRemove = new List<Entity>();
			foreach (var (entity, record) in _records)
			{
				if (record.LastSeenStamp != _syncStamp)
				{
					toRemove.Add(entity);
				}
			}

			for (var i = 0; i < toRemove.Count; i++)
			{
				var entity = toRemove[i];
				if (_records.TryGetValue(entity, out var record) == false)
				{
					continue;
				}

				_records.Remove(entity);
				ReleaseRecord(record);
				if (record.DrawId == _maxActiveDrawId)
				{
					_maxActiveDrawIdDirty = true;
				}
				_updates.Add(GpuDrawUpdate.CreateRemove(record.DrawId));
			}
		}
	}

	public void CollectDrawEntries(List<GpuDrawEntry> destination)
	{
		lock (_lock)
		{
			destination.Clear();
			foreach (var record in _records.Values)
			{
				destination.Add(new GpuDrawEntry(
					record.DrawId,
					record.InstanceId,
					record.MeshId,
					record.MaterialId,
					record.Mesh,
					record.Material,
					record.World,
					record.boundsCenterRadius));
			}
		}
	}

	public void ConsumeUpdates(List<GpuDrawUpdate> destination)
	{
		lock (_lock)
		{
			destination.Clear();
			destination.AddRange(_updates);
			_updates.Clear();
		}
	}

	public uint GetActiveDrawCommandUpperBound()
	{
		lock (_lock)
		{
			if (_records.Count == 0)
			{
				_maxActiveDrawId = 0;
				_maxActiveDrawIdDirty = false;
				return 1;
			}

			if (_maxActiveDrawIdDirty)
			{
				var maxDrawId = 0;
				foreach (var record in _records.Values)
				{
					if (record.DrawId > maxDrawId)
					{
						maxDrawId = record.DrawId;
					}
				}

				_maxActiveDrawId = maxDrawId;
				_maxActiveDrawIdDirty = false;
			}

			return (uint)(_maxActiveDrawId + 1);
		}
	}

	private void ApplyChanges(DrawRecord record, Mesh mesh, Material material, in Matrix4x4 worldTransform)
	{
		var transformChanged = record.World.Equals(worldTransform) == false;
		var meshChanged = ReferenceEquals(record.Mesh, mesh) == false;
		var materialChanged = ReferenceEquals(record.Material, material) == false;

		if (meshChanged)
		{
			ReleaseMesh(record.MeshId, record.Mesh);
			record.Mesh = mesh;
			record.MeshId = AcquireMeshId(mesh);
		}

		if (materialChanged)
		{
			ReleaseMaterial(record.MaterialId, record.Material);
			record.Material = material;
			record.MaterialId = AcquireMaterialId(material);
		}

		if (transformChanged || meshChanged)
		{
			record.World = worldTransform;
			ComputeBounds(record, mesh);
		}

		if (meshChanged)
		{
			_updates.Add(GpuDrawUpdate.CreateMeshUpdate(
				record.DrawId,
				record.InstanceId,
				record.MeshId,
				record.MaterialId,
				record.World,
				record.boundsCenterRadius,
				mesh));
		}

		if (materialChanged)
		{
			_updates.Add(GpuDrawUpdate.CreateMaterialUpdate(
				record.DrawId,
				record.InstanceId,
				record.MeshId,
				record.MaterialId,
				record.World,
				record.boundsCenterRadius,
				material));
		}

		if (transformChanged || meshChanged)
		{
			_updates.Add(GpuDrawUpdate.CreateTransformUpdate(
				record.DrawId,
				record.InstanceId,
				record.MeshId,
				record.MaterialId,
				worldTransform,
				record.boundsCenterRadius));
		}
	}

	private DrawRecord CreateRecord(Entity entity, Mesh mesh, Material material, in Matrix4x4 worldTransform)
	{
		var record = new DrawRecord
		{
			Entity = entity,
			DrawId = AcquireId(_freeDrawIds, ref _nextDrawId),
			InstanceId = AcquireId(_freeInstanceIds, ref _nextInstanceId),
			Mesh = mesh,
			Material = material,
			MeshId = AcquireMeshId(mesh),
			MaterialId = AcquireMaterialId(material),
			World = worldTransform,
			LastSeenStamp = _syncStamp
		};

		ComputeBounds(record, mesh);
		return record;
	}

	private void ReleaseRecord(DrawRecord record)
	{
		if (DisableIdReuse == false)
		{
			_freeDrawIds.Push(record.DrawId);
			_freeInstanceIds.Push(record.InstanceId);
		}
		ReleaseMesh(record.MeshId, record.Mesh);
		ReleaseMaterial(record.MaterialId, record.Material);
	}

	private int AcquireMeshId(Mesh mesh)
	{
		if (_meshIds.TryGetValue(mesh, out var entry))
		{
			entry.RefCount++;
			_meshIds[mesh] = entry;
			return entry.Id;
		}

		var id = AcquireId(_freeMeshIds, ref _nextMeshId);
		_meshIds[mesh] = new ResourceId(id, 1);
		return id;
	}

	private void ReleaseMesh(int id, Mesh mesh)
	{
		if (_meshIds.TryGetValue(mesh, out var entry) == false)
		{
			return;
		}

		entry.RefCount--;
		if (entry.RefCount > 0)
		{
			_meshIds[mesh] = entry;
			return;
		}

		_meshIds.Remove(mesh);
		_freeMeshIds.Push(id);
	}

	private int AcquireMaterialId(Material material)
	{
		if (_materialIds.TryGetValue(material, out var entry))
		{
			entry.RefCount++;
			_materialIds[material] = entry;
			return entry.Id;
		}

		var id = AcquireId(_freeMaterialIds, ref _nextMaterialId);
		_materialIds[material] = new ResourceId(id, 1);
		return id;
	}

	private void ReleaseMaterial(int id, Material material)
	{
		if (_materialIds.TryGetValue(material, out var entry) == false)
		{
			return;
		}

		entry.RefCount--;
		if (entry.RefCount > 0)
		{
			_materialIds[material] = entry;
			return;
		}

		_materialIds.Remove(material);
		if (DisableIdReuse == false)
		{
			_freeMaterialIds.Push(id);
		}
	}

	private static int AcquireId(Stack<int> freeIds, ref int nextId)
	{
		return DisableIdReuse ? nextId++ : (freeIds.Count > 0 ? freeIds.Pop() : nextId++);
	}

	private static void ComputeBounds(DrawRecord record, Mesh mesh)
	{
		var bounds = mesh.BoundingSphere;
		record.boundsCenterRadius = new(Vector3.Transform(bounds.Center, record.World),
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
		public int DrawId;
		public int InstanceId;
		public int MeshId;
		public int MaterialId;
		public Mesh Mesh = null!;
		public Material Material = null!;
		public Matrix4x4 World;
		public Vector4 boundsCenterRadius;
		public int LastSeenStamp;
	}

	private struct ResourceId
	{
		public ResourceId(int id, int refCount)
		{
			Id = id;
			RefCount = refCount;
		}

		public int Id { get; set; }

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
	public GpuDrawEntry(int drawId, int instanceId, int meshId, int materialId, Mesh mesh, Material material,
		Matrix4x4 world, Vector4 boundsCenterRadius)
	{
		DrawId = drawId;
		InstanceId = instanceId;
		MeshId = meshId;
		MaterialId = materialId;
		Mesh = mesh;
		Material = material;
		World = world;
		BoundsCenterRadius = boundsCenterRadius;
	}

	public int DrawId { get; }

	public int InstanceId { get; }

	public int MeshId { get; }

	public int MaterialId { get; }

	public Mesh Mesh { get; }

	public Material Material { get; }

	public Matrix4x4 World { get; }

	public Vector4 BoundsCenterRadius { get; }

}

public readonly struct GpuDrawUpdate
{
	private GpuDrawUpdate(GpuDrawUpdateType type, int drawId, int instanceId, int meshId, int materialId,
		Matrix4x4 world, Vector4 boundsCenterRadius, Mesh? mesh, Material? material)
	{
		Type = type;
		DrawId = drawId;
		InstanceId = instanceId;
		MeshId = meshId;
		MaterialId = materialId;
		World = world;
		BoundsCenterRadius = boundsCenterRadius;
		Mesh = mesh;
		Material = material;
	}

	public GpuDrawUpdateType Type { get; }

	public int DrawId { get; }

	public int InstanceId { get; }

	public int MeshId { get; }

	public int MaterialId { get; }

	public Matrix4x4 World { get; }

	public Vector4 BoundsCenterRadius { get; }
	
	public Mesh? Mesh { get; }

	public Material? Material { get; }

	public static GpuDrawUpdate CreateAdd(int drawId, int instanceId, int meshId, int materialId, in Matrix4x4 world,
		Vector4 boundsCenterRadius, Mesh mesh, Material material)
	{
		return new GpuDrawUpdate(GpuDrawUpdateType.Add, drawId, instanceId, meshId, materialId, world, boundsCenterRadius, mesh, material);
	}

	public static GpuDrawUpdate CreateRemove(int drawId)
	{
		return new GpuDrawUpdate(GpuDrawUpdateType.Remove, drawId, 0, 0, 0, Matrix4x4.Identity, Vector4.Zero, null, null);
	}

	public static GpuDrawUpdate CreateTransformUpdate(int drawId, int instanceId, int meshId, int materialId,
		in Matrix4x4 world, Vector4 boundsCenterRadius)
	{
		return new GpuDrawUpdate(GpuDrawUpdateType.UpdateTransform, drawId, instanceId, meshId, materialId,
			world, boundsCenterRadius, null, null);
	}

	public static GpuDrawUpdate CreateMaterialUpdate(int drawId, int instanceId, int meshId, int materialId,
		in Matrix4x4 world, Vector4 boundsCenterRadius, Material material)
	{
		return new GpuDrawUpdate(GpuDrawUpdateType.UpdateMaterial, drawId, instanceId, meshId, materialId,
			world, boundsCenterRadius, null, material);
	}

	public static GpuDrawUpdate CreateMeshUpdate(int drawId, int instanceId, int meshId, int materialId,
		in Matrix4x4 world, Vector4 boundsCenterRadius, Mesh mesh)
	{
		return new GpuDrawUpdate(GpuDrawUpdateType.UpdateMesh, drawId, instanceId, meshId, materialId,
			world, boundsCenterRadius, mesh, null);
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
