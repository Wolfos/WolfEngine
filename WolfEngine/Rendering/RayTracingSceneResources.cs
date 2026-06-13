#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

public interface IRayTracingSceneResources
{
	IGfxTopLevelAccelerationStructure? TopLevelAccelerationStructure { get; }
	IGfxBuffer? InstanceIndexToInstanceHandleBuffer { get; }
	RayTracingSceneStats LastStats { get; }
}

[Flags]
public enum RayTracingSceneRebuildReason
{
	None = 0,
	Bootstrap = 1 << 0,
	Add = 1 << 1,
	Remove = 1 << 2,
	Transform = 1 << 3,
	Mesh = 1 << 4
}

public readonly struct RayTracingSceneStats
{
	public RayTracingSceneStats(
		int bottomLevelAccelerationStructureCount,
		int topLevelInstanceCount,
		int pendingBottomLevelBuildCount,
		int topLevelRebuildCount,
		RayTracingSceneRebuildReason topLevelRebuildReason,
		int skippedTerrainCount,
		int skippedTransparentOrAlphaCount,
		bool sidecarHitShadingAvailable)
	{
		BottomLevelAccelerationStructureCount = bottomLevelAccelerationStructureCount;
		TopLevelInstanceCount = topLevelInstanceCount;
		PendingBottomLevelBuildCount = pendingBottomLevelBuildCount;
		TopLevelRebuildCount = topLevelRebuildCount;
		TopLevelRebuildReason = topLevelRebuildReason;
		SkippedTerrainCount = skippedTerrainCount;
		SkippedTransparentOrAlphaCount = skippedTransparentOrAlphaCount;
		SidecarHitShadingAvailable = sidecarHitShadingAvailable;
	}

	public int BottomLevelAccelerationStructureCount { get; }
	public int TopLevelInstanceCount { get; }
	public int PendingBottomLevelBuildCount { get; }
	public int TopLevelRebuildCount { get; }
	public RayTracingSceneRebuildReason TopLevelRebuildReason { get; }
	public int SkippedTerrainCount { get; }
	public int SkippedTransparentOrAlphaCount { get; }
	public bool SidecarHitShadingAvailable { get; }
}

public sealed class RayTracingSceneResources : IRayTracingSceneResources, IDisposable
{
	private readonly Dictionary<Mesh, MeshAccelerationStructureRecord> _meshRecords = new(new ReferenceComparer<Mesh>());
	private readonly Dictionary<uint, InstanceRecord> _instances = new();
	private readonly List<RayTracingInstanceDescription> _instanceDescriptions = new();
	private readonly uint[] _instanceIndexToInstanceHandle = new uint[GpuDrawResources.MaxInstanceCount];
	private readonly List<IGfxBottomLevelAccelerationStructure> _pendingBlasBuilds = new();
	private readonly List<GpuDrawEntry> _drawEntries = new();
	private IGfxTopLevelAccelerationStructure? _topLevelAccelerationStructure;
	private IGfxBuffer? _instanceIndexToInstanceHandleBuffer;
	private IGfxDevice? _sidecarDevice;
	private RayTracingSceneStats _lastStats;
	private bool _bootstrapPending = true;
	private bool _tlasDirty;

	public IGfxTopLevelAccelerationStructure? TopLevelAccelerationStructure => _topLevelAccelerationStructure;

	public IGfxBuffer? InstanceIndexToInstanceHandleBuffer => _instanceIndexToInstanceHandleBuffer;

	public RayTracingSceneStats LastStats => _lastStats;

	public void RecordUpdate(
		RenderGraphContext context,
		IRenderer renderer,
		IReadOnlyList<GpuDrawUpdate> updates)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(renderer);
		ArgumentNullException.ThrowIfNull(updates);

		var device = renderer.GetGfxDevice();
		if (device.BackendKind != GraphicsBackendKind.Metal)
		{
			return;
		}

		EnsureSidecarResources(device);
		_topLevelAccelerationStructure ??= device.CreateTopLevelAccelerationStructure(
			new TopLevelAccelerationStructureDescriptor(GpuDrawResources.MaxInstanceCount - 1));

		var statsBuilder = new RayTracingSceneStatsBuilder();
		var appliedBootstrap = false;
		if (_bootstrapPending)
		{
			RebuildFromCurrentDrawEntries(context.GpuDrawDatabase, renderer, device, ref statsBuilder);
			statsBuilder.TopLevelRebuildReason |= RayTracingSceneRebuildReason.Bootstrap;
			_bootstrapPending = false;
			appliedBootstrap = true;
		}

		if (appliedBootstrap == false)
		{
			for (var i = 0; i < updates.Count; i++)
			{
				ApplyUpdate(updates[i], renderer, device, ref statsBuilder);
			}
		}

		var pendingBlasBuildCount = _pendingBlasBuilds.Count;
		var commandList = context.CommandList;
		for (var i = 0; i < _pendingBlasBuilds.Count; i++)
		{
			commandList.BuildBottomLevelAccelerationStructure(_pendingBlasBuilds[i]);
		}
		_pendingBlasBuilds.Clear();

		var tlasRebuildCount = 0;
		if (_tlasDirty)
		{
			BuildInstanceDescriptions();
			commandList.BuildTopLevelAccelerationStructure(_topLevelAccelerationStructure, CollectionsMarshalAsSpan(_instanceDescriptions));
			_tlasDirty = false;
			tlasRebuildCount = 1;
		}

		_lastStats = new RayTracingSceneStats(
			_meshRecords.Count,
			_instances.Count,
			pendingBlasBuildCount,
			tlasRebuildCount,
			tlasRebuildCount > 0 ? statsBuilder.TopLevelRebuildReason : RayTracingSceneRebuildReason.None,
			statsBuilder.SkippedTerrainCount,
			statsBuilder.SkippedTransparentOrAlphaCount,
			_instanceIndexToInstanceHandleBuffer is not null);
	}

	private void EnsureSidecarResources(IGfxDevice device)
	{
		if (ReferenceEquals(_sidecarDevice, device) && _instanceIndexToInstanceHandleBuffer is not null)
		{
			return;
		}

		if (_instanceIndexToInstanceHandleBuffer is IDisposable disposableBuffer)
		{
			disposableBuffer.Dispose();
		}

		_instanceIndexToInstanceHandleBuffer = device.CreateBuffer(new BufferDescriptor(
			(ulong)(GpuDrawResources.MaxInstanceCount * sizeof(uint)),
			BufferUsage.Structured,
			BufferFlags.AllowShaderResource));
		_sidecarDevice = device;
	}

	private void RebuildFromCurrentDrawEntries(
		GpuDrawDatabase drawDatabase,
		IRenderer renderer,
		IGfxDevice device,
		ref RayTracingSceneStatsBuilder statsBuilder)
	{
		foreach (var record in _instances.Values)
		{
			ReleaseMesh(record.Mesh);
		}
		_instances.Clear();

		drawDatabase.CollectDrawEntries(_drawEntries);
		foreach (var entry in _drawEntries)
		{
			if (IsRayTraceable(entry.DrawKind, entry.Material, ref statsBuilder) == false)
			{
				continue;
			}

			renderer.EnsureMeshResources(entry.Mesh);
			var blas = AcquireMesh(entry.Mesh, device);
			_instances[entry.InstanceHandle.Value] = new InstanceRecord(
				entry.Mesh,
				blas,
				entry.World,
				entry.InstanceHandle.Value,
				entry.Material);
		}

		_tlasDirty = true;
	}

	private void ApplyUpdate(
		in GpuDrawUpdate update,
		IRenderer renderer,
		IGfxDevice device,
		ref RayTracingSceneStatsBuilder statsBuilder)
	{
		if (update.Type == GpuDrawUpdateType.Remove)
		{
			if (RemoveInstance(update.InstanceHandle.Value))
			{
				statsBuilder.TopLevelRebuildReason |= RayTracingSceneRebuildReason.Remove;
			}
			return;
		}

		if (update.Type == GpuDrawUpdateType.UpdateMaterial)
		{
			return;
		}

		if (update.Type == GpuDrawUpdateType.UpdateTransform)
		{
			if (_instances.TryGetValue(update.InstanceHandle.Value, out var record))
			{
				_instances[update.InstanceHandle.Value] = record with { World = update.World };
				_tlasDirty = true;
				statsBuilder.TopLevelRebuildReason |= RayTracingSceneRebuildReason.Transform;
			}

			return;
		}

		var hasExistingRecord = _instances.TryGetValue(update.InstanceHandle.Value, out var oldRecord);
		var material = update.Material ?? (hasExistingRecord ? oldRecord.Material : null);
		if (update.Mesh is null || material is null || IsRayTraceable(update.DrawKind, material, ref statsBuilder) == false)
		{
			if (RemoveInstance(update.InstanceHandle.Value))
			{
				statsBuilder.TopLevelRebuildReason |= update.Type == GpuDrawUpdateType.UpdateMesh
					? RayTracingSceneRebuildReason.Mesh
					: RayTracingSceneRebuildReason.Remove;
			}
			return;
		}

		renderer.EnsureMeshResources(update.Mesh);
		var newBlas = AcquireMesh(update.Mesh, device);
		if (hasExistingRecord)
		{
			ReleaseMesh(oldRecord.Mesh);
		}

		_instances[update.InstanceHandle.Value] = new InstanceRecord(
			update.Mesh,
			newBlas,
			update.World,
			update.InstanceHandle.Value,
			material);
		_tlasDirty = true;
		statsBuilder.TopLevelRebuildReason |= update.Type == GpuDrawUpdateType.UpdateMesh
			? RayTracingSceneRebuildReason.Mesh
			: RayTracingSceneRebuildReason.Add;
	}

	private IGfxBottomLevelAccelerationStructure AcquireMesh(Mesh mesh, IGfxDevice device)
	{
		if (_meshRecords.TryGetValue(mesh, out var record))
		{
			record.RefCount++;
			_meshRecords[mesh] = record;
			return record.AccelerationStructure;
		}

		if (mesh.VertexBuffer is null || mesh.IndexBuffer is null)
		{
			throw new InvalidOperationException("Mesh GPU buffers must exist before creating ray tracing geometry.");
		}

		var descriptor = new BottomLevelAccelerationStructureDescriptor(
			mesh.VertexBuffer,
			mesh.PackedVertexOffsetBytes,
			mesh.StrideInBytes,
			(uint)mesh.Vertices.Length,
			mesh.IndexBuffer,
			mesh.PackedIndexOffsetBytes,
			mesh.IndexCount);
		var accelerationStructure = device.CreateBottomLevelAccelerationStructure(descriptor);
		_meshRecords[mesh] = new MeshAccelerationStructureRecord(accelerationStructure, 1);
		_pendingBlasBuilds.Add(accelerationStructure);
		return accelerationStructure;
	}

	private void ReleaseMesh(Mesh mesh)
	{
		if (_meshRecords.TryGetValue(mesh, out var record) == false)
		{
			return;
		}

		record.RefCount--;
		if (record.RefCount > 0)
		{
			_meshRecords[mesh] = record;
			return;
		}

		_meshRecords.Remove(mesh);
		if (record.AccelerationStructure is IDisposable disposable)
		{
			disposable.Dispose();
		}
	}

	private bool RemoveInstance(uint instanceHandle)
	{
		if (_instances.Remove(instanceHandle, out var record) == false)
		{
			return false;
		}

		ReleaseMesh(record.Mesh);
		_tlasDirty = true;
		return true;
	}

	private void BuildInstanceDescriptions()
	{
		_instanceDescriptions.Clear();
		Array.Clear(_instanceIndexToInstanceHandle);
		foreach (var record in _instances.Values)
		{
			var instanceIndex = (uint)_instanceDescriptions.Count;
			_instanceDescriptions.Add(new RayTracingInstanceDescription(
				instanceIndex,
				record.AccelerationStructure,
				record.World));
			if (instanceIndex < _instanceIndexToInstanceHandle.Length)
			{
				_instanceIndexToInstanceHandle[instanceIndex] = record.InstanceHandle;
			}
		}

		if (_instanceIndexToInstanceHandleBuffer is IWritableGpuBuffer writableBuffer)
		{
			writableBuffer.Write<uint>(_instanceIndexToInstanceHandle);
		}
	}

	private static bool IsRayTraceable(GpuDrawKind drawKind, Material material, ref RayTracingSceneStatsBuilder statsBuilder)
	{
		if (drawKind == GpuDrawKind.Terrain)
		{
			statsBuilder.SkippedTerrainCount++;
			return false;
		}

		if (drawKind is not GpuDrawKind.Mesh)
		{
			return false;
		}

		if (material.AlphaMode != AlphaMode.Opaque)
		{
			statsBuilder.SkippedTransparentOrAlphaCount++;
			return false;
		}

		return true;
	}

	private static ReadOnlySpan<RayTracingInstanceDescription> CollectionsMarshalAsSpan(
		List<RayTracingInstanceDescription> descriptions)
	{
		return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(descriptions);
	}

	public void Dispose()
	{
		foreach (var record in _meshRecords.Values)
		{
			if (record.AccelerationStructure is IDisposable disposable)
			{
				disposable.Dispose();
			}
		}
		_meshRecords.Clear();

		if (_topLevelAccelerationStructure is IDisposable tlasDisposable)
		{
			tlasDisposable.Dispose();
		}
		_topLevelAccelerationStructure = null;

		if (_instanceIndexToInstanceHandleBuffer is IDisposable sidecarDisposable)
		{
			sidecarDisposable.Dispose();
		}
		_instanceIndexToInstanceHandleBuffer = null;
		_sidecarDevice = null;
	}

	private struct RayTracingSceneStatsBuilder
	{
		public RayTracingSceneRebuildReason TopLevelRebuildReason;
		public int SkippedTerrainCount;
		public int SkippedTransparentOrAlphaCount;
	}

	private readonly record struct InstanceRecord(
		Mesh Mesh,
		IGfxBottomLevelAccelerationStructure AccelerationStructure,
		Matrix4x4 World,
		uint InstanceHandle,
		Material Material);

	private struct MeshAccelerationStructureRecord
	{
		public MeshAccelerationStructureRecord(IGfxBottomLevelAccelerationStructure accelerationStructure, int refCount)
		{
			AccelerationStructure = accelerationStructure;
			RefCount = refCount;
		}

		public IGfxBottomLevelAccelerationStructure AccelerationStructure { get; }
		public int RefCount { get; set; }
	}

	private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
	{
		public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

		public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
	}
}
