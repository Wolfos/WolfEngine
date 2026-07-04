#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using WolfEngine.ECS;
using WolfEngine.Mathematics;

namespace WolfEngine.Rendering;

public sealed class GpuDrawDatabase
{
	private readonly Dictionary<DrawRecordKey, DrawRecord> _records = new();
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
		TouchMesh(entity, mesh, material, worldTransform);
	}

	public void TouchMesh(Entity entity, Mesh mesh, Material material, in Matrix4x4 worldTransform)
	{
		var key = new DrawRecordKey(entity, 0);
		if (_records.TryGetValue(key, out var record))
		{
			ApplyChanges(record, GpuDrawKind.Mesh, mesh, material, worldTransform);
			record.LastSeenStamp = _syncStamp;
			return;
		}

		var newRecord = CreateRecord(key, GpuDrawKind.Mesh, mesh, material, worldTransform);
		_records.Add(key, newRecord);
		if (newRecord.DrawHandle.Index > _maxActiveDrawIndex)
		{
			_maxActiveDrawIndex = newRecord.DrawHandle.Index;
		}

		_updates.Add(GpuDrawUpdate.CreateAdd(
			newRecord.DrawKind,
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

	public void TouchDebugPrimitive(
		Entity entity,
		Mesh primitiveMesh,
		ColorRGBA tint,
		AlphaMode alphaMode,
		in Matrix4x4 worldTransform,
		TerrainChunkInstanceData instanceData = default)
	{
		var key = new DrawRecordKey(entity, 0);
		var resolvedAlphaMode = alphaMode == AlphaMode.AlphaBlend
			? AlphaMode.AlphaBlend
			: AlphaMode.Opaque;

		if (_records.TryGetValue(key, out var record))
		{
			if (record.DrawKind != GpuDrawKind.DebugPrimitive)
			{
				throw new InvalidOperationException(
					$"Shared draw kind mismatch for entity {record.Entity}. Existing kind={record.DrawKind}, requested kind={GpuDrawKind.DebugPrimitive}.");
			}

			ApplyDebugPrimitiveChanges(
				record,
				primitiveMesh,
				tint,
				resolvedAlphaMode,
				worldTransform,
				instanceData);
			record.LastSeenStamp = _syncStamp;
			return;
		}

		var material = CreateDebugPrimitiveMaterial(tint, resolvedAlphaMode);
		var newRecord = CreateRecord(
			key,
			GpuDrawKind.DebugPrimitive,
			primitiveMesh,
			material,
			worldTransform,
			terrainInstanceData: instanceData);
		_records.Add(key, newRecord);
		if (newRecord.DrawHandle.Index > _maxActiveDrawIndex)
		{
			_maxActiveDrawIndex = newRecord.DrawHandle.Index;
		}

		_updates.Add(GpuDrawUpdate.CreateAdd(
			newRecord.DrawKind,
			newRecord.DrawHandle,
			newRecord.InstanceHandle,
			newRecord.MeshHandle,
			newRecord.MaterialHandle,
			newRecord.PreviousWorld,
			newRecord.World,
			newRecord.BoundsCenterRadius,
			primitiveMesh,
			material,
			newRecord.TerrainInstanceData));
	}

	public void TouchTerrainChunk(
		Entity entity,
		int chunkIndex,
		Mesh mesh,
		Material material,
		in BoundingSphere localBounds,
		in TerrainChunkInstanceData instanceData,
		in TerrainDrawSurface surface,
		in Matrix4x4 worldTransform)
	{
		TouchTerrainChunk(
			entity,
			chunkIndex,
			mesh,
			material,
			localBounds,
			instanceData,
			surface,
			new TerrainRayTracingChunkData(chunkIndex, 16, 1, instanceData.ChunkOriginSize, instanceData.HeightmapUvScaleOffset),
			worldTransform);
	}

	public void TouchTerrainChunk(
		Entity entity,
		int chunkIndex,
		Mesh mesh,
		Material material,
		in BoundingSphere localBounds,
		in TerrainChunkInstanceData instanceData,
		in TerrainDrawSurface surface,
		in TerrainRayTracingChunkData rayTracingChunk,
		in Matrix4x4 worldTransform)
	{
		var key = new DrawRecordKey(entity, chunkIndex + 1);
		if (_records.TryGetValue(key, out var record))
		{
			if (record.DrawKind != GpuDrawKind.Terrain)
			{
				throw new InvalidOperationException(
					$"Shared draw kind mismatch for entity {record.Entity}. Existing kind={record.DrawKind}, requested kind={GpuDrawKind.Terrain}.");
			}

			ApplyTerrainChanges(record, mesh, material, localBounds, instanceData, surface, rayTracingChunk, worldTransform);
			record.LastSeenStamp = _syncStamp;
			return;
		}

		var newRecord = CreateRecord(key, GpuDrawKind.Terrain, mesh, material, worldTransform, localBounds, instanceData);
		newRecord.TerrainSurface = surface;
		newRecord.TerrainRayTracingChunk = rayTracingChunk;
		_records.Add(key, newRecord);
		if (newRecord.DrawHandle.Index > _maxActiveDrawIndex)
		{
			_maxActiveDrawIndex = newRecord.DrawHandle.Index;
		}

		_updates.Add(GpuDrawUpdate.CreateAdd(
			newRecord.DrawKind,
			newRecord.DrawHandle,
			newRecord.InstanceHandle,
			newRecord.MeshHandle,
			newRecord.MaterialHandle,
			newRecord.PreviousWorld,
			newRecord.World,
			newRecord.BoundsCenterRadius,
			mesh,
			material,
			newRecord.TerrainInstanceData,
			surface,
			rayTracingChunk));
	}

	public void EndSync()
	{
		var toRemove = new List<DrawRecordKey>();
		foreach (var (key, record) in _records)
		{
			if (record.LastSeenStamp != _syncStamp)
			{
				toRemove.Add(key);
			}
		}

		foreach (var key in toRemove)
		{
			if (_records.TryGetValue(key, out var record) == false)
			{
				continue;
			}

			_records.Remove(key);
			_updates.Add(GpuDrawUpdate.CreateRemove(record.DrawKind, record.DrawHandle, record.InstanceHandle));
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
				record.DrawKind,
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
				record.DrawKind,
				record.DrawHandle,
				record.InstanceHandle,
				record.MeshHandle,
				record.MaterialHandle,
				record.Mesh,
				record.Material,
				record.TerrainInstanceData,
				record.TerrainSurface,
				record.TerrainRayTracingChunk,
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

	public void CopyUpdates(List<GpuDrawUpdate> destination)
	{
		destination.Clear();
		destination.AddRange(_updates);
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

	private void ApplyChanges(DrawRecord record, GpuDrawKind drawKind, Mesh mesh, Material material, in Matrix4x4 worldTransform)
	{
		if (record.DrawKind != drawKind)
		{
			throw new InvalidOperationException(
				$"Shared draw kind mismatch for entity {record.Entity}. Existing kind={record.DrawKind}, requested kind={drawKind}.");
		}

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
				record.DrawKind,
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
				record.DrawKind,
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
				record.DrawKind,
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
				record.DrawKind,
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

	private void ApplyDebugPrimitiveChanges(
		DrawRecord record,
		Mesh primitiveMesh,
		ColorRGBA tint,
		AlphaMode alphaMode,
		in Matrix4x4 worldTransform,
		in TerrainChunkInstanceData instanceData)
	{
		var material = record.Material;
		var transformChanged = record.World.Equals(worldTransform) == false;
		var meshChanged = ReferenceEquals(record.Mesh, primitiveMesh) == false;
		var tintChanged = material.Color.Equals(tint) == false;
		var alphaModeChanged = material.AlphaMode != alphaMode;
		var instanceDataChanged = TerrainInstanceEquals(record.TerrainInstanceData, instanceData) == false;
		var settlePreviousTransform = transformChanged == false && record.PreviousWorld.Equals(record.World) == false;

		if ((transformChanged || meshChanged || tintChanged || alphaModeChanged || instanceDataChanged || settlePreviousTransform) == false)
		{
			return;
		}

		var uploadPreviousWorld = record.World;

		if (meshChanged)
		{
			ReleaseMesh(record.MeshHandle, record.Mesh);
			record.Mesh = primitiveMesh;
			record.MeshHandle = AcquireMeshHandle(primitiveMesh);
		}

		if (transformChanged || meshChanged)
		{
			record.World = worldTransform;
			ComputeBounds(record, primitiveMesh);
		}
		record.TerrainInstanceData = instanceData;

		if (meshChanged)
		{
			_updates.Add(GpuDrawUpdate.CreateMeshUpdate(
				record.DrawKind,
				record.DrawHandle,
				record.InstanceHandle,
				record.MeshHandle,
				record.MaterialHandle,
				record.World,
				record.World,
				record.BoundsCenterRadius,
				record.Mesh,
				record.TerrainInstanceData));
		}

		if (tintChanged || alphaModeChanged)
		{
			material.Color = tint;
			material.AlphaMode = alphaMode;
			_updates.Add(GpuDrawUpdate.CreateMaterialUpdate(
				record.DrawKind,
				record.DrawHandle,
				record.InstanceHandle,
				record.MeshHandle,
				record.MaterialHandle,
				record.World,
				record.World,
				record.BoundsCenterRadius,
				record.Mesh,
				record.Material,
				record.TerrainInstanceData));
		}

		if (transformChanged || instanceDataChanged)
		{
			_updates.Add(GpuDrawUpdate.CreateTransformUpdate(
				record.DrawKind,
				record.DrawHandle,
				record.InstanceHandle,
				record.MeshHandle,
				record.MaterialHandle,
				uploadPreviousWorld,
				record.World,
				record.BoundsCenterRadius,
				record.TerrainInstanceData));
			record.PreviousWorld = uploadPreviousWorld;
		}

		if (settlePreviousTransform)
		{
			_updates.Add(GpuDrawUpdate.CreateTransformUpdate(
				record.DrawKind,
				record.DrawHandle,
				record.InstanceHandle,
				record.MeshHandle,
				record.MaterialHandle,
				record.World,
				record.World,
				record.BoundsCenterRadius,
				record.TerrainInstanceData));
			record.PreviousWorld = record.World;
		}
	}

	private void ApplyTerrainChanges(
		DrawRecord record,
		Mesh mesh,
		Material material,
		in BoundingSphere localBounds,
		in TerrainChunkInstanceData instanceData,
		in TerrainDrawSurface surface,
		in TerrainRayTracingChunkData rayTracingChunk,
		in Matrix4x4 worldTransform)
	{
		var transformChanged = record.World.Equals(worldTransform) == false;
		var meshChanged = ReferenceEquals(record.Mesh, mesh) == false;
		var materialChanged = ReferenceEquals(record.Material, material) == false;
		var materialResourceChanged = materialChanged == false && record.MaterialResourceRevision != material.ResourceRevision;
		var terrainInstanceChanged = TerrainInstanceEquals(record.TerrainInstanceData, instanceData) == false;
		var boundsChanged = record.HasBoundsOverride == false || record.LocalBoundsOverride.Equals(localBounds) == false;
		var surfaceChanged = record.TerrainSurface.HasValue == false || TerrainSurfaceEquals(record.TerrainSurface.Value, surface) == false;
		var rayTracingChunkChanged = TerrainRayTracingChunkEquals(record.TerrainRayTracingChunk, rayTracingChunk) == false;
		var settlePreviousTransform = transformChanged == false && record.PreviousWorld.Equals(record.World) == false;

		if ((transformChanged || meshChanged || materialChanged || materialResourceChanged || terrainInstanceChanged || boundsChanged || surfaceChanged || rayTracingChunkChanged || settlePreviousTransform) == false)
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

		if (surfaceChanged)
		{
			record.TerrainSurface = surface;
		}

		if (rayTracingChunkChanged || terrainInstanceChanged)
		{
			record.TerrainRayTracingChunk = rayTracingChunk;
		}

		if (terrainInstanceChanged)
		{
			record.TerrainInstanceData = instanceData;
		}

		if (boundsChanged)
		{
			record.HasBoundsOverride = true;
			record.LocalBoundsOverride = localBounds;
		}

		if (transformChanged || meshChanged || boundsChanged || terrainInstanceChanged)
		{
			record.World = worldTransform;
			ComputeBounds(record, mesh);
		}

		if (meshChanged)
		{
			_updates.Add(GpuDrawUpdate.CreateMeshUpdate(
				record.DrawKind,
				record.DrawHandle,
				record.InstanceHandle,
				record.MeshHandle,
				record.MaterialHandle,
				record.World,
				record.World,
				record.BoundsCenterRadius,
				record.Mesh,
				record.TerrainInstanceData,
				record.TerrainSurface,
				record.TerrainRayTracingChunk));
		}

		if (materialChanged || materialResourceChanged || surfaceChanged || rayTracingChunkChanged)
		{
			_updates.Add(GpuDrawUpdate.CreateMaterialUpdate(
				record.DrawKind,
				record.DrawHandle,
				record.InstanceHandle,
				record.MeshHandle,
				record.MaterialHandle,
				record.World,
				record.World,
				record.BoundsCenterRadius,
				record.Mesh,
				record.Material,
				record.TerrainInstanceData,
				record.TerrainSurface,
				record.TerrainRayTracingChunk));
		}

		if (transformChanged || terrainInstanceChanged || boundsChanged)
		{
			_updates.Add(GpuDrawUpdate.CreateTransformUpdate(
				record.DrawKind,
				record.DrawHandle,
				record.InstanceHandle,
				record.MeshHandle,
				record.MaterialHandle,
				uploadPreviousWorld,
				record.World,
				record.BoundsCenterRadius,
				record.TerrainInstanceData,
				record.TerrainRayTracingChunk));
			record.PreviousWorld = uploadPreviousWorld;
		}

		if (settlePreviousTransform)
		{
			_updates.Add(GpuDrawUpdate.CreateTransformUpdate(
				record.DrawKind,
				record.DrawHandle,
				record.InstanceHandle,
				record.MeshHandle,
				record.MaterialHandle,
				record.World,
				record.World,
				record.BoundsCenterRadius,
				record.TerrainInstanceData,
				record.TerrainRayTracingChunk));
			record.PreviousWorld = record.World;
		}
	}

	private DrawRecord CreateRecord(
		DrawRecordKey key,
		GpuDrawKind drawKind,
		Mesh mesh,
		Material material,
		in Matrix4x4 worldTransform,
		BoundingSphere? localBoundsOverride = null,
		TerrainChunkInstanceData? terrainInstanceData = null)
	{
		var record = new DrawRecord
		{
			Entity = key.Entity,
			DrawKind = drawKind,
			DrawHandle = _drawHandlePool.Acquire(),
			InstanceHandle = _instanceHandlePool.Acquire(),
			Mesh = mesh,
			Material = material,
			MeshHandle = AcquireMeshHandle(mesh),
			MaterialHandle = AcquireMaterialHandle(material),
			MaterialResourceRevision = material.ResourceRevision,
			World = worldTransform,
			PreviousWorld = worldTransform,
			LastSeenStamp = _syncStamp,
			HasBoundsOverride = localBoundsOverride.HasValue,
			LocalBoundsOverride = localBoundsOverride ?? default,
			TerrainInstanceData = terrainInstanceData ?? default
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
		var bounds = record.HasBoundsOverride ? record.LocalBoundsOverride : mesh.BoundingSphere;
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

	private static Material CreateDebugPrimitiveMaterial(ColorRGBA tint, AlphaMode alphaMode)
	{
		return new Material("__debug_primitive__")
		{
			Color = tint,
			AlphaMode = alphaMode,
			AlphaCutoff = 0.0f,
			MetallicFactor = 0.0f,
			RoughnessFactor = 1.0f,
			EmissiveFactor = Vector3.Zero,
			EmissiveIntensity = 0.0f
		};
	}

	private static bool TerrainSurfaceEquals(in TerrainDrawSurface left, in TerrainDrawSurface right)
	{
		if (!ReferenceEquals(left.Heightmap, right.Heightmap) ||
		    left.HeightmapResourceRevision != right.HeightmapResourceRevision ||
		    !ReferenceEquals(left.LayerIndexMap, right.LayerIndexMap) ||
		    left.LayerIndexMapResourceRevision != right.LayerIndexMapResourceRevision ||
		    !ReferenceEquals(left.LayerWeightMap, right.LayerWeightMap) ||
		    left.LayerWeightMapResourceRevision != right.LayerWeightMapResourceRevision ||
		    MathF.Abs(left.HeightScale - right.HeightScale) > 0.0001f ||
		    left.LayerCount != right.LayerCount ||
		    MathF.Abs(left.HeightBlendSharpness - right.HeightBlendSharpness) > 0.0001f ||
		    left.Layers.Count != right.Layers.Count)
		{
			return false;
		}

		for (var i = 0; i < left.Layers.Count; i++)
		{
			if (!TerrainLayerEquals(left.Layers[i], right.Layers[i]))
			{
				return false;
			}
		}

		return true;
	}

	private static bool TerrainInstanceEquals(in TerrainChunkInstanceData left, in TerrainChunkInstanceData right)
	{
		return left.ChunkOriginSize.Equals(right.ChunkOriginSize) &&
		       left.HeightmapUvScaleOffset.Equals(right.HeightmapUvScaleOffset);
	}

	private static bool TerrainRayTracingChunkEquals(in TerrainRayTracingChunkData left, in TerrainRayTracingChunkData right)
	{
		return left.ChunkIndex == right.ChunkIndex &&
		       left.ResolutionInQuads == right.ResolutionInQuads &&
		       left.GeometryRevision == right.GeometryRevision;
	}

	private static bool TerrainLayerEquals(in TerrainResolvedLayer left, in TerrainResolvedLayer right)
	{
		return ReferenceEquals(left.Albedo, right.Albedo) &&
		       left.AlbedoResourceRevision == right.AlbedoResourceRevision &&
		       ReferenceEquals(left.Normal, right.Normal) &&
		       left.NormalResourceRevision == right.NormalResourceRevision &&
		       ReferenceEquals(left.Orm, right.Orm) &&
		       left.OrmResourceRevision == right.OrmResourceRevision &&
		       ReferenceEquals(left.Height, right.Height) &&
		       left.HeightResourceRevision == right.HeightResourceRevision &&
		       MathF.Abs(left.Scale - right.Scale) <= 0.0001f;
	}

	internal sealed class DrawRecord
	{
		public Entity Entity = default;
		public GpuDrawKind DrawKind;
		public GpuDrawHandle DrawHandle;
		public GpuDrawHandle InstanceHandle;
		public GpuDrawHandle MeshHandle;
		public GpuDrawHandle MaterialHandle;
		public Mesh Mesh = null!;
		public Material Material = null!;
		public int MaterialResourceRevision;
		public TerrainDrawSurface? TerrainSurface;
		public TerrainChunkInstanceData TerrainInstanceData;
		public TerrainRayTracingChunkData TerrainRayTracingChunk;
		public bool HasBoundsOverride;
		public BoundingSphere LocalBoundsOverride;
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

	private readonly record struct DrawRecordKey(Entity Entity, int SubdrawId);

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
		GpuDrawKind drawKind,
		GpuDrawHandle drawHandle,
		GpuDrawHandle instanceHandle,
		GpuDrawHandle meshHandle,
		GpuDrawHandle materialHandle,
		Mesh mesh,
		Material material,
		TerrainChunkInstanceData terrainInstanceData,
		TerrainDrawSurface? terrainSurface,
		TerrainRayTracingChunkData terrainRayTracingChunk,
		Matrix4x4 previousWorld,
		Matrix4x4 world,
		Vector4 boundsCenterRadius)
	{
		DrawKind = drawKind;
		DrawHandle = drawHandle;
		InstanceHandle = instanceHandle;
		MeshHandle = meshHandle;
		MaterialHandle = materialHandle;
		Mesh = mesh;
		Material = material;
		TerrainInstanceData = terrainInstanceData;
		TerrainSurface = terrainSurface;
		TerrainRayTracingChunk = terrainRayTracingChunk;
		PreviousWorld = previousWorld;
		World = world;
		BoundsCenterRadius = boundsCenterRadius;
	}

	public GpuDrawKind DrawKind { get; }
	public GpuDrawHandle DrawHandle { get; }
	public GpuDrawHandle InstanceHandle { get; }
	public GpuDrawHandle MeshHandle { get; }
	public GpuDrawHandle MaterialHandle { get; }
	public Mesh Mesh { get; }
	public Material Material { get; }
	public TerrainChunkInstanceData TerrainInstanceData { get; }
	public TerrainDrawSurface? TerrainSurface { get; }
	public TerrainRayTracingChunkData TerrainRayTracingChunk { get; }
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
		GpuDrawKind drawKind,
		GpuDrawHandle drawHandle,
		GpuDrawHandle instanceHandle,
		GpuDrawHandle meshHandle,
		GpuDrawHandle materialHandle,
		Matrix4x4 previousWorld,
		Matrix4x4 world,
		Vector4 boundsCenterRadius,
		Mesh? mesh,
		Material? material,
		TerrainChunkInstanceData terrainInstanceData,
		TerrainDrawSurface? terrainSurface,
		TerrainRayTracingChunkData terrainRayTracingChunk)
	{
		Type = type;
		DrawKind = drawKind;
		DrawHandle = drawHandle;
		InstanceHandle = instanceHandle;
		MeshHandle = meshHandle;
		MaterialHandle = materialHandle;
		PreviousWorld = previousWorld;
		World = world;
		BoundsCenterRadius = boundsCenterRadius;
		Mesh = mesh;
		Material = material;
		TerrainInstanceData = terrainInstanceData;
		TerrainSurface = terrainSurface;
		TerrainRayTracingChunk = terrainRayTracingChunk;
	}

	public GpuDrawUpdateType Type { get; }
	public GpuDrawKind DrawKind { get; }
	public GpuDrawHandle DrawHandle { get; }
	public GpuDrawHandle InstanceHandle { get; }
	public GpuDrawHandle MeshHandle { get; }
	public GpuDrawHandle MaterialHandle { get; }
	public Matrix4x4 PreviousWorld { get; }
	public Matrix4x4 World { get; }
	public Vector4 BoundsCenterRadius { get; }
	public Mesh? Mesh { get; }
	public Material? Material { get; }
	public TerrainChunkInstanceData TerrainInstanceData { get; }
	public TerrainDrawSurface? TerrainSurface { get; }
	public TerrainRayTracingChunkData TerrainRayTracingChunk { get; }

	public int DrawIndex => DrawHandle.Index;
	public int InstanceIndex => InstanceHandle.Index;
	public int MeshIndex => MeshHandle.Index;
	public int MaterialIndex => MaterialHandle.Index;

	public static GpuDrawUpdate CreateAdd(
		GpuDrawKind drawKind,
		GpuDrawHandle drawHandle,
		GpuDrawHandle instanceHandle,
		GpuDrawHandle meshHandle,
		GpuDrawHandle materialHandle,
		in Matrix4x4 previousWorld,
		in Matrix4x4 world,
		Vector4 boundsCenterRadius,
		Mesh mesh,
		Material material,
		TerrainChunkInstanceData terrainInstanceData = default,
		TerrainDrawSurface? terrainSurface = null,
		TerrainRayTracingChunkData terrainRayTracingChunk = default)
	{
		return new GpuDrawUpdate(
			GpuDrawUpdateType.Add,
			drawKind,
			drawHandle,
			instanceHandle,
			meshHandle,
			materialHandle,
			previousWorld,
			world,
			boundsCenterRadius,
			mesh,
			material,
			terrainInstanceData,
			terrainSurface,
			terrainRayTracingChunk);
	}

	public static GpuDrawUpdate CreateRemove(GpuDrawKind drawKind, GpuDrawHandle drawHandle, GpuDrawHandle instanceHandle)
	{
		return new GpuDrawUpdate(
			GpuDrawUpdateType.Remove,
			drawKind,
			drawHandle,
			instanceHandle,
			GpuDrawHandle.Invalid,
			GpuDrawHandle.Invalid,
			Matrix4x4.Identity,
			Matrix4x4.Identity,
			Vector4.Zero,
			null,
			null,
			default,
			null,
			default);
	}

	public static GpuDrawUpdate CreateTransformUpdate(
		GpuDrawKind drawKind,
		GpuDrawHandle drawHandle,
		GpuDrawHandle instanceHandle,
		GpuDrawHandle meshHandle,
		GpuDrawHandle materialHandle,
		in Matrix4x4 previousWorld,
		in Matrix4x4 world,
		Vector4 boundsCenterRadius,
		TerrainChunkInstanceData terrainInstanceData = default,
		TerrainRayTracingChunkData terrainRayTracingChunk = default)
	{
		return new GpuDrawUpdate(
			GpuDrawUpdateType.UpdateTransform,
			drawKind,
			drawHandle,
			instanceHandle,
			meshHandle,
			materialHandle,
			previousWorld,
			world,
			boundsCenterRadius,
			null,
			null,
			terrainInstanceData,
			null,
			terrainRayTracingChunk);
	}

	public static GpuDrawUpdate CreateMaterialUpdate(
		GpuDrawKind drawKind,
		GpuDrawHandle drawHandle,
		GpuDrawHandle instanceHandle,
		GpuDrawHandle meshHandle,
		GpuDrawHandle materialHandle,
		in Matrix4x4 previousWorld,
		in Matrix4x4 world,
		Vector4 boundsCenterRadius,
		Mesh mesh,
		Material material,
		TerrainChunkInstanceData terrainInstanceData = default,
		TerrainDrawSurface? terrainSurface = null,
		TerrainRayTracingChunkData terrainRayTracingChunk = default)
	{
		return new GpuDrawUpdate(
			GpuDrawUpdateType.UpdateMaterial,
			drawKind,
			drawHandle,
			instanceHandle,
			meshHandle,
			materialHandle,
			previousWorld,
			world,
			boundsCenterRadius,
			mesh,
			material,
			terrainInstanceData,
			terrainSurface,
			terrainRayTracingChunk);
	}

	public static GpuDrawUpdate CreateMeshUpdate(
		GpuDrawKind drawKind,
		GpuDrawHandle drawHandle,
		GpuDrawHandle instanceHandle,
		GpuDrawHandle meshHandle,
		GpuDrawHandle materialHandle,
		in Matrix4x4 previousWorld,
		in Matrix4x4 world,
		Vector4 boundsCenterRadius,
		Mesh mesh,
		TerrainChunkInstanceData terrainInstanceData = default,
		TerrainDrawSurface? terrainSurface = null,
		TerrainRayTracingChunkData terrainRayTracingChunk = default)
	{
		return new GpuDrawUpdate(
			GpuDrawUpdateType.UpdateMesh,
			drawKind,
			drawHandle,
			instanceHandle,
			meshHandle,
			materialHandle,
			previousWorld,
			world,
			boundsCenterRadius,
			mesh,
			null,
			terrainInstanceData,
			terrainSurface,
			terrainRayTracingChunk);
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
