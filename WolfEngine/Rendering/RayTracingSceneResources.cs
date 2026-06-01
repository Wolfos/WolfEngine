#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering;

public interface IRayTracingSceneResources
{
	IGfxTopLevelAccelerationStructure? TopLevelAccelerationStructure { get; }
}

public sealed class RayTracingSceneResources : IRayTracingSceneResources, IDisposable
{
	private readonly Dictionary<Mesh, MeshAccelerationStructureRecord> _meshRecords = new(new ReferenceComparer<Mesh>());
	private readonly Dictionary<uint, InstanceRecord> _instances = new();
	private readonly List<RayTracingInstanceDescription> _instanceDescriptions = new();
	private readonly List<IGfxBottomLevelAccelerationStructure> _pendingBlasBuilds = new();
	private readonly List<GpuDrawEntry> _drawEntries = new();
	private IGfxTopLevelAccelerationStructure? _topLevelAccelerationStructure;
	private Vector3 _lastCameraOrigin;
	private bool _bootstrapPending = true;
	private bool _hasLastCameraOrigin;
	private bool _tlasDirty;

	public IGfxTopLevelAccelerationStructure? TopLevelAccelerationStructure => _topLevelAccelerationStructure;

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

		_topLevelAccelerationStructure ??= device.CreateTopLevelAccelerationStructure(
			new TopLevelAccelerationStructureDescriptor(GpuDrawResources.MaxInstanceCount - 1));

		if (_bootstrapPending)
		{
			RebuildFromCurrentDrawEntries(context.GpuDrawDatabase, renderer, device);
			_bootstrapPending = false;
		}

		var cameraOrigin = context.SceneData?.CameraOrigin ?? Vector3.Zero;
		if (_hasLastCameraOrigin == false || _lastCameraOrigin.Equals(cameraOrigin) == false)
		{
			_lastCameraOrigin = cameraOrigin;
			_hasLastCameraOrigin = true;
			_tlasDirty = true;
		}

		for (var i = 0; i < updates.Count; i++)
		{
			ApplyUpdate(updates[i], renderer, device);
		}

		var commandList = context.CommandList;
		for (var i = 0; i < _pendingBlasBuilds.Count; i++)
		{
			commandList.BuildBottomLevelAccelerationStructure(_pendingBlasBuilds[i]);
		}
		_pendingBlasBuilds.Clear();

		if (_tlasDirty)
		{
			BuildInstanceDescriptions(cameraOrigin);
			commandList.BuildTopLevelAccelerationStructure(_topLevelAccelerationStructure, CollectionsMarshalAsSpan(_instanceDescriptions));
			_tlasDirty = false;
		}
	}

	private void RebuildFromCurrentDrawEntries(GpuDrawDatabase drawDatabase, IRenderer renderer, IGfxDevice device)
	{
		foreach (var record in _instances.Values)
		{
			ReleaseMesh(record.Mesh);
		}
		_instances.Clear();

		drawDatabase.CollectDrawEntries(_drawEntries);
		for (var i = 0; i < _drawEntries.Count; i++)
		{
			var entry = _drawEntries[i];
			if (IsRayTraceable(entry.DrawKind, entry.Material) == false)
			{
				continue;
			}

			renderer.EnsureMeshResources(entry.Mesh);
			var blas = AcquireMesh(entry.Mesh, device);
			_instances[entry.InstanceHandle.Value] = new InstanceRecord(
				entry.Mesh,
				blas,
				entry.World,
				entry.InstanceHandle.Value);
		}

		_tlasDirty = true;
	}

	private void ApplyUpdate(in GpuDrawUpdate update, IRenderer renderer, IGfxDevice device)
	{
		if (update.Type == GpuDrawUpdateType.Remove)
		{
			RemoveInstance(update.InstanceHandle.Value);
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
			}

			return;
		}

		if (update.Mesh is null || update.Material is null || IsRayTraceable(update.DrawKind, update.Material) == false)
		{
			RemoveInstance(update.InstanceHandle.Value);
			return;
		}

		renderer.EnsureMeshResources(update.Mesh);
		var newBlas = AcquireMesh(update.Mesh, device);
		if (_instances.TryGetValue(update.InstanceHandle.Value, out var oldRecord))
		{
			ReleaseMesh(oldRecord.Mesh);
		}

		_instances[update.InstanceHandle.Value] = new InstanceRecord(
			update.Mesh,
			newBlas,
			update.World,
			update.InstanceHandle.Value);
		_tlasDirty = true;
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

	private void RemoveInstance(uint instanceHandle)
	{
		if (_instances.Remove(instanceHandle, out var record) == false)
		{
			return;
		}

		ReleaseMesh(record.Mesh);
		_tlasDirty = true;
	}

	private void BuildInstanceDescriptions(Vector3 cameraOrigin)
	{
		_instanceDescriptions.Clear();
		foreach (var record in _instances.Values)
		{
			var relativeWorld = record.World;
			relativeWorld.M41 -= cameraOrigin.X;
			relativeWorld.M42 -= cameraOrigin.Y;
			relativeWorld.M43 -= cameraOrigin.Z;
			_instanceDescriptions.Add(new RayTracingInstanceDescription(
				(uint)_instanceDescriptions.Count,
				record.AccelerationStructure,
				relativeWorld));
		}
	}

	private static bool IsRayTraceable(GpuDrawKind drawKind, Material material)
	{
		return (drawKind is GpuDrawKind.Mesh or GpuDrawKind.DebugPrimitive) &&
		       material.AlphaMode == AlphaMode.Opaque;
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
	}

	private readonly record struct InstanceRecord(
		Mesh Mesh,
		IGfxBottomLevelAccelerationStructure AccelerationStructure,
		Matrix4x4 World,
		uint InstanceHandle);

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
