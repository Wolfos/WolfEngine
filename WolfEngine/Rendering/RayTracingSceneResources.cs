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
	IGfxBuffer? InstanceIndexToTerrainRayTracingResolutionBuffer { get; }
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
	private readonly Dictionary<uint, TerrainInstanceRecord> _terrainInstances = new();
	private readonly Dictionary<int, TerrainIndexBufferRecord> _terrainIndexBuffers = new();
	private readonly List<RayTracingInstanceDescription> _instanceDescriptions = new();
	private readonly uint[] _instanceIndexToInstanceHandle = new uint[GpuDrawResources.MaxInstanceCount];
	private readonly uint[] _instanceIndexToTerrainRayTracingResolution = new uint[GpuDrawResources.MaxInstanceCount];
	private readonly List<IGfxBottomLevelAccelerationStructure> _pendingBlasBuilds = new();
	private readonly List<TerrainVertexUpdateRecord> _pendingTerrainVertexUpdates = new();
	private readonly List<uint> _pendingTerrainRetryHandles = new();
	private readonly List<GpuDrawEntry> _drawEntries = new();
	private readonly ShaderCompiler _shaderCompiler = new();
	private IGfxTopLevelAccelerationStructure? _topLevelAccelerationStructure;
	private IGfxBuffer? _instanceIndexToInstanceHandleBuffer;
	private IGfxBuffer? _instanceIndexToTerrainRayTracingResolutionBuffer;
	private IGfxDevice? _sidecarDevice;
	private IGfxPipeline? _terrainVertexUpdatePipeline;
	private ShaderPropertyWriter? _terrainVertexUpdateParamsWriter;
	private ComputeThreadGroupSize? _terrainVertexUpdateThreadGroupSize;
	private ReadOnlyMemory<byte> _terrainVertexUpdateShaderBytecode;
	private RayTracingSceneStats _lastStats;
	private bool _bootstrapPending = true;
	private bool _tlasDirty;

	public IGfxTopLevelAccelerationStructure? TopLevelAccelerationStructure => _topLevelAccelerationStructure;

	public IGfxBuffer? InstanceIndexToInstanceHandleBuffer => _instanceIndexToInstanceHandleBuffer;

	public IGfxBuffer? InstanceIndexToTerrainRayTracingResolutionBuffer => _instanceIndexToTerrainRayTracingResolutionBuffer;

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

		RetryPendingTerrainVertexUpdates(ref statsBuilder);
		var pendingBlasBuildCount = _pendingBlasBuilds.Count;
		var commandList = context.CommandList;
		DispatchPendingTerrainVertexUpdates(commandList, device);
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
			_meshRecords.Count + CountValidTerrainInstances(),
			_instances.Count + CountValidTerrainInstances(),
			pendingBlasBuildCount,
			tlasRebuildCount,
			tlasRebuildCount > 0 ? statsBuilder.TopLevelRebuildReason : RayTracingSceneRebuildReason.None,
			statsBuilder.SkippedTerrainCount,
			statsBuilder.SkippedTransparentOrAlphaCount,
			_instanceIndexToInstanceHandleBuffer is not null);
	}

	private void EnsureSidecarResources(IGfxDevice device)
	{
		if (ReferenceEquals(_sidecarDevice, device) &&
		    _instanceIndexToInstanceHandleBuffer is not null &&
		    _instanceIndexToTerrainRayTracingResolutionBuffer is not null)
		{
			return;
		}

		if (_instanceIndexToInstanceHandleBuffer is IDisposable disposableBuffer)
		{
			disposableBuffer.Dispose();
		}
		if (_instanceIndexToTerrainRayTracingResolutionBuffer is IDisposable disposableTerrainBuffer)
		{
			disposableTerrainBuffer.Dispose();
		}

		_instanceIndexToInstanceHandleBuffer = device.CreateBuffer(new BufferDescriptor(
			(ulong)(GpuDrawResources.MaxInstanceCount * sizeof(uint)),
			BufferUsage.Structured,
			BufferFlags.AllowShaderResource));
		_instanceIndexToTerrainRayTracingResolutionBuffer = device.CreateBuffer(new BufferDescriptor(
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
		ReleaseAllTerrainInstances();

		drawDatabase.CollectDrawEntries(_drawEntries);
		foreach (var entry in _drawEntries)
		{
			if (entry.DrawKind == GpuDrawKind.Terrain)
			{
				if (TryAcquireTerrain(entry, device, out var terrainRecord))
				{
					_terrainInstances[entry.InstanceHandle.Value] = terrainRecord;
				}

				continue;
			}

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
			if (update.DrawKind == GpuDrawKind.Terrain)
			{
				if (RemoveTerrainInstance(update.InstanceHandle.Value))
				{
					statsBuilder.TopLevelRebuildReason |= RayTracingSceneRebuildReason.Remove;
				}

				return;
			}

			if (RemoveInstance(update.InstanceHandle.Value))
			{
				statsBuilder.TopLevelRebuildReason |= RayTracingSceneRebuildReason.Remove;
			}
			return;
		}

		if (update.Type == GpuDrawUpdateType.UpdateMaterial)
		{
			if (update.DrawKind == GpuDrawKind.Terrain)
			{
				ApplyTerrainUpdate(update, device, ref statsBuilder);
			}

			return;
		}

		if (update.Type == GpuDrawUpdateType.UpdateTransform)
		{
			if (update.DrawKind == GpuDrawKind.Terrain)
			{
				if (_terrainInstances.TryGetValue(update.InstanceHandle.Value, out var terrainRecord))
				{
					_terrainInstances[update.InstanceHandle.Value] = terrainRecord with { World = update.World };
					_tlasDirty = true;
					statsBuilder.TopLevelRebuildReason |= RayTracingSceneRebuildReason.Transform;
				}

				return;
			}

			if (_instances.TryGetValue(update.InstanceHandle.Value, out var record))
			{
				_instances[update.InstanceHandle.Value] = record with { World = update.World };
				_tlasDirty = true;
				statsBuilder.TopLevelRebuildReason |= RayTracingSceneRebuildReason.Transform;
			}

			return;
		}

		if (update.DrawKind == GpuDrawKind.Terrain)
		{
			ApplyTerrainUpdate(update, device, ref statsBuilder);
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

	private void ApplyTerrainUpdate(
		in GpuDrawUpdate update,
		IGfxDevice device,
		ref RayTracingSceneStatsBuilder statsBuilder)
	{
		var hasExistingRecord = _terrainInstances.TryGetValue(update.InstanceHandle.Value, out var oldRecord);
		if (update.TerrainSurface is null)
		{
			if (RemoveTerrainInstance(update.InstanceHandle.Value))
			{
				statsBuilder.TopLevelRebuildReason |= RayTracingSceneRebuildReason.Remove;
			}

			return;
		}

		if (hasExistingRecord == false ||
		    oldRecord.RayTracingChunk.ResolutionInQuads != update.TerrainRayTracingChunk.ResolutionInQuads)
		{
			if (hasExistingRecord)
			{
				DisposeTerrainRecord(oldRecord);
			}

			if (TryAcquireTerrain(update, device, out var newRecord))
			{
				_terrainInstances[update.InstanceHandle.Value] = newRecord;
				if (newRecord.HasValidGeometry || hasExistingRecord)
				{
					_tlasDirty = true;
					statsBuilder.TopLevelRebuildReason |= hasExistingRecord
						? RayTracingSceneRebuildReason.Mesh
						: RayTracingSceneRebuildReason.Add;
				}
			}

			return;
		}

		var updatedRecord = oldRecord with
		{
			World = update.World,
			RayTracingChunk = update.TerrainRayTracingChunk,
			TerrainSurface = update.TerrainSurface.Value
		};

		if (oldRecord.RayTracingChunk.GeometryRevision != update.TerrainRayTracingChunk.GeometryRevision ||
		    !ReferenceEquals(oldRecord.TerrainSurface.Heightmap, update.TerrainSurface.Value.Heightmap))
		{
			updatedRecord = QueueTerrainVertexUpdate(updatedRecord)
				? updatedRecord with { HasValidGeometry = true, VertexUpdatePending = false }
				: updatedRecord with { VertexUpdatePending = true };
			if (updatedRecord.VertexUpdatePending == false)
			{
				_pendingBlasBuilds.Add(updatedRecord.AccelerationStructure);
				if (oldRecord.HasValidGeometry == false)
				{
					_tlasDirty = true;
					statsBuilder.TopLevelRebuildReason |= RayTracingSceneRebuildReason.Add;
				}
			}
		}

		_terrainInstances[update.InstanceHandle.Value] = updatedRecord;
	}

	private bool TryAcquireTerrain(in GpuDrawEntry entry, IGfxDevice device, out TerrainInstanceRecord record)
	{
		record = default;
		if (entry.TerrainSurface is null)
		{
			return false;
		}

		return TryAcquireTerrain(
			entry.InstanceHandle.Value,
			entry.World,
			entry.TerrainSurface.Value,
			entry.TerrainRayTracingChunk,
			device,
			out record);
	}

	private bool TryAcquireTerrain(in GpuDrawUpdate update, IGfxDevice device, out TerrainInstanceRecord record)
	{
		record = default;
		if (update.TerrainSurface is null)
		{
			return false;
		}

		return TryAcquireTerrain(
			update.InstanceHandle.Value,
			update.World,
			update.TerrainSurface.Value,
			update.TerrainRayTracingChunk,
			device,
			out record);
	}

	private bool TryAcquireTerrain(
		uint instanceHandle,
		Matrix4x4 world,
		in TerrainDrawSurface surface,
		in TerrainRayTracingChunkData rayTracingChunk,
		IGfxDevice device,
		out TerrainInstanceRecord record)
	{
		record = default;
		var resolution = rayTracingChunk.ResolutionInQuads;
		if (resolution < 1)
		{
			return false;
		}

		var vertexCount = (uint)((resolution + 1) * (resolution + 1));
		var vertexBuffer = device.CreateBuffer(new BufferDescriptor(
			(ulong)vertexCount * 16UL,
			BufferUsage.Vertex | BufferUsage.Structured,
			BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource));
		var indexRecord = GetOrCreateTerrainIndexBuffer(device, resolution);
		var descriptor = new BottomLevelAccelerationStructureDescriptor(
			vertexBuffer,
			0,
			16,
			vertexCount,
			indexRecord.IndexBuffer,
			0,
			indexRecord.IndexCount);
		var accelerationStructure = device.CreateBottomLevelAccelerationStructure(descriptor);
		record = new TerrainInstanceRecord(
			instanceHandle,
			world,
			rayTracingChunk,
			surface,
			vertexBuffer,
			indexRecord.IndexBuffer,
			accelerationStructure,
			false,
			true);
		if (QueueTerrainVertexUpdate(record))
		{
			record = record with { HasValidGeometry = true, VertexUpdatePending = false };
			_pendingBlasBuilds.Add(accelerationStructure);
		}

		return true;
	}

	private TerrainIndexBufferRecord GetOrCreateTerrainIndexBuffer(IGfxDevice device, int resolution)
	{
		if (_terrainIndexBuffers.TryGetValue(resolution, out var existing))
		{
			return existing;
		}

		var indices = BuildTerrainIndices(resolution);
		var indexBuffer = device.CreateBuffer(new BufferDescriptor(
			(ulong)indices.Length * sizeof(uint),
			BufferUsage.Index,
			BufferFlags.AllowShaderResource));
		if (indexBuffer is IWritableGpuBuffer writableBuffer)
		{
			writableBuffer.Write<uint>(indices);
		}

		var record = new TerrainIndexBufferRecord(indexBuffer, (uint)indices.Length);
		_terrainIndexBuffers[resolution] = record;
		return record;
	}

	private static uint[] BuildTerrainIndices(int resolution)
	{
		var indices = new uint[resolution * resolution * 6];
		var write = 0;
		var vertsPerAxis = resolution + 1;
		for (var y = 0; y < resolution; y++)
		{
			for (var x = 0; x < resolution; x++)
			{
				var i0 = (uint)(y * vertsPerAxis + x);
				var i1 = i0 + 1;
				var i2 = i0 + (uint)vertsPerAxis;
				var i3 = i2 + 1;
				indices[write++] = i0;
				indices[write++] = i2;
				indices[write++] = i1;
				indices[write++] = i1;
				indices[write++] = i2;
				indices[write++] = i3;
			}
		}

		return indices;
	}

	private bool QueueTerrainVertexUpdate(in TerrainInstanceRecord record)
	{
		if (record.TerrainSurface.Heightmap is not { } heightmap ||
		    heightmap.Resources?.ShaderResourceView.IsValid != true)
		{
			return false;
		}

		_pendingTerrainVertexUpdates.Add(new TerrainVertexUpdateRecord(
			record.VertexBuffer,
			heightmap.Resources.ShaderResourceView.Value,
			record.RayTracingChunk,
			record.TerrainSurface.HeightScale,
			heightmap.Width,
			heightmap.Height));
		return true;
	}

	private void RetryPendingTerrainVertexUpdates(
		ref RayTracingSceneStatsBuilder statsBuilder)
	{
		_pendingTerrainRetryHandles.Clear();
		foreach (var (instanceHandle, record) in _terrainInstances)
		{
			if (record.VertexUpdatePending)
			{
				_pendingTerrainRetryHandles.Add(instanceHandle);
			}
		}

		for (var i = 0; i < _pendingTerrainRetryHandles.Count; i++)
		{
			var instanceHandle = _pendingTerrainRetryHandles[i];
			if (_terrainInstances.TryGetValue(instanceHandle, out var record) == false)
			{
				continue;
			}

			if (QueueTerrainVertexUpdate(record) == false)
			{
				continue;
			}

			_terrainInstances[instanceHandle] = record with
			{
				HasValidGeometry = true,
				VertexUpdatePending = false
			};
			_pendingBlasBuilds.Add(record.AccelerationStructure);
			if (record.HasValidGeometry == false)
			{
				_tlasDirty = true;
				statsBuilder.TopLevelRebuildReason |= RayTracingSceneRebuildReason.Add;
			}
		}

		_pendingTerrainRetryHandles.Clear();
	}

	private void DispatchPendingTerrainVertexUpdates(IGfxCommandList commandList, IGfxDevice device)
	{
		if (_pendingTerrainVertexUpdates.Count == 0)
		{
			return;
		}

		var pipeline = EnsureTerrainVertexUpdatePipeline(device);
		var writer = _terrainVertexUpdateParamsWriter
			?? throw new InvalidOperationException("Terrain RT vertex update parameters were not reflected.");
		var threadGroupSize = _terrainVertexUpdateThreadGroupSize
			?? throw new InvalidOperationException("Terrain RT vertex update thread group size was not reflected.");

		commandList.BindPipeline(pipeline);
		for (var i = 0; i < _pendingTerrainVertexUpdates.Count; i++)
		{
			var update = _pendingTerrainVertexUpdates[i];
			writer.Clear();
			writer.SetUInt("heightmapHandle", update.HeightmapHandle);
			writer.SetUInt("resolutionInQuads", (uint)update.RayTracingChunk.ResolutionInQuads);
			writer.SetUInt("heightmapWidth", (uint)Math.Max(update.HeightmapWidth, 1));
			writer.SetUInt("heightmapHeight", (uint)Math.Max(update.HeightmapHeight, 1));
			writer.SetFloat("heightScale", update.HeightScale);
			writer.SetVector4("chunkOriginSize", update.RayTracingChunk.ChunkOriginSize);
			writer.SetVector4("heightmapUvScaleOffset", update.RayTracingChunk.HeightmapUvScaleOffset);
			commandList.SetComputeConstants(writer.RegisterIndex, writer.AsBytes());
			commandList.SetComputeBuffer(0, update.VertexBuffer);
			var vertexCount = (uint)((update.RayTracingChunk.ResolutionInQuads + 1) * (update.RayTracingChunk.ResolutionInQuads + 1));
			var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(vertexCount, 1, 1);
			commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
			commandList.Barrier(new ResourceBarrierDescription(update.VertexBuffer, ResourceState.UnorderedAccess, ResourceState.ShaderResource));
		}

		_pendingTerrainVertexUpdates.Clear();
	}

	private IGfxPipeline EnsureTerrainVertexUpdatePipeline(IGfxDevice device)
	{
		if (_terrainVertexUpdatePipeline is not null)
		{
			return _terrainVertexUpdatePipeline;
		}

		var compiled = _shaderCompiler.GetComputeShaderWithReflection(
			"terrain_rt_vertex_update.compute.slang",
			"CSMain",
			device.BackendKind);
		_terrainVertexUpdateShaderBytecode = compiled.Bytecode;
		_terrainVertexUpdateThreadGroupSize = compiled.ThreadGroupSize;
		_terrainVertexUpdateParamsWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("TerrainRtVertexUpdateParams"));
		var pipelineKey = new PipelineKey(PassKind.Compute, null, null, "CSMain", default, default, default, shaderVariant: "terrain_rt_vertex_update.compute.slang");
		_terrainVertexUpdatePipeline = device.GetOrCreatePipeline(
			pipelineKey,
			new ShaderBytecodeSet(compute: _terrainVertexUpdateShaderBytecode, computeThreadGroupSize: _terrainVertexUpdateThreadGroupSize));
		return _terrainVertexUpdatePipeline;
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

	private bool RemoveTerrainInstance(uint instanceHandle)
	{
		if (_terrainInstances.Remove(instanceHandle, out var record) == false)
		{
			return false;
		}

		DisposeTerrainRecord(record);
		_tlasDirty = true;
		return true;
	}

	private void ReleaseAllTerrainInstances()
	{
		foreach (var record in _terrainInstances.Values)
		{
			DisposeTerrainRecord(record);
		}

		_terrainInstances.Clear();
	}

	private static void DisposeTerrainRecord(in TerrainInstanceRecord record)
	{
		if (record.AccelerationStructure is IDisposable blasDisposable)
		{
			blasDisposable.Dispose();
		}

		if (record.VertexBuffer is IDisposable vertexBufferDisposable)
		{
			vertexBufferDisposable.Dispose();
		}
	}

	private void BuildInstanceDescriptions()
	{
		_instanceDescriptions.Clear();
		Array.Clear(_instanceIndexToInstanceHandle);
		Array.Clear(_instanceIndexToTerrainRayTracingResolution);
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

		foreach (var record in _terrainInstances.Values)
		{
			if (record.HasValidGeometry == false)
			{
				continue;
			}

			var instanceIndex = (uint)_instanceDescriptions.Count;
			_instanceDescriptions.Add(new RayTracingInstanceDescription(
				instanceIndex,
				record.AccelerationStructure,
				record.World));
			if (instanceIndex < _instanceIndexToInstanceHandle.Length)
			{
				_instanceIndexToInstanceHandle[instanceIndex] = record.InstanceHandle;
				_instanceIndexToTerrainRayTracingResolution[instanceIndex] =
					(uint)Math.Max(record.RayTracingChunk.ResolutionInQuads, 0);
			}
		}

		if (_instanceIndexToInstanceHandleBuffer is IWritableGpuBuffer writableBuffer)
		{
			writableBuffer.Write<uint>(_instanceIndexToInstanceHandle);
		}
		if (_instanceIndexToTerrainRayTracingResolutionBuffer is IWritableGpuBuffer writableTerrainBuffer)
		{
			writableTerrainBuffer.Write<uint>(_instanceIndexToTerrainRayTracingResolution);
		}
	}

	private int CountValidTerrainInstances()
	{
		var count = 0;
		foreach (var record in _terrainInstances.Values)
		{
			if (record.HasValidGeometry)
			{
				count++;
			}
		}

		return count;
	}

	private static bool IsRayTraceable(GpuDrawKind drawKind, Material material, ref RayTracingSceneStatsBuilder statsBuilder)
	{
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
		ReleaseAllTerrainInstances();
		foreach (var record in _terrainIndexBuffers.Values)
		{
			if (record.IndexBuffer is IDisposable disposableIndexBuffer)
			{
				disposableIndexBuffer.Dispose();
			}
		}
		_terrainIndexBuffers.Clear();

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
		if (_instanceIndexToTerrainRayTracingResolutionBuffer is IDisposable terrainSidecarDisposable)
		{
			terrainSidecarDisposable.Dispose();
		}
		_instanceIndexToTerrainRayTracingResolutionBuffer = null;
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

	private readonly record struct TerrainInstanceRecord(
		uint InstanceHandle,
		Matrix4x4 World,
		TerrainRayTracingChunkData RayTracingChunk,
		TerrainDrawSurface TerrainSurface,
		IGfxBuffer VertexBuffer,
		IGfxBuffer IndexBuffer,
		IGfxBottomLevelAccelerationStructure AccelerationStructure,
		bool HasValidGeometry,
		bool VertexUpdatePending);

	private readonly record struct TerrainIndexBufferRecord(IGfxBuffer IndexBuffer, uint IndexCount);

	private readonly record struct TerrainVertexUpdateRecord(
		IGfxBuffer VertexBuffer,
		uint HeightmapHandle,
		TerrainRayTracingChunkData RayTracingChunk,
		float HeightScale,
		int HeightmapWidth,
		int HeightmapHeight);

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
