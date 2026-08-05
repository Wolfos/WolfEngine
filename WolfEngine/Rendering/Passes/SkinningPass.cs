#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

/// <summary>Bind-pose position and skin influences shared with the shader.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly struct GpuSkinVertex
{
	internal GpuSkinVertex(Vector4 bindPosition, uint index0, uint index1, uint index2, uint index3, Vector4 weights)
	{
		BindPosition = bindPosition;
		Index0 = index0;
		Index1 = index1;
		Index2 = index2;
		Index3 = index3;
		Weights = weights;
	}

	/// <summary>Float4 keeps the shared struct stride consistent across backends.</summary>
	public readonly Vector4 BindPosition;
	public readonly uint Index0;
	public readonly uint Index1;
	public readonly uint Index2;
	public readonly uint Index3;
	public readonly Vector4 Weights;
}

/// <summary>Per-instance skinning ranges, indexed by mesh-handle slot.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly struct GpuSkinnedInstanceData
{
	internal GpuSkinnedInstanceData(
		uint meshHandle,
		uint skinVertexBase,
		uint boneMatrixOffset,
		uint previousBoneMatrixOffset,
		uint boneCount)
	{
		MeshHandle = meshHandle;
		SkinVertexBase = skinVertexBase;
		BoneMatrixOffset = boneMatrixOffset;
		PreviousBoneMatrixOffset = previousBoneMatrixOffset;
		BoneCount = boneCount;
		Pad0 = 0;
		Pad1 = 0;
		Pad2 = 0;
	}

	/// <summary>Full handle validates entries in recycled slots.</summary>
	public readonly uint MeshHandle;
	public readonly uint SkinVertexBase;
	public readonly uint BoneMatrixOffset;
	public readonly uint PreviousBoneMatrixOffset;
	public readonly uint BoneCount;
	public readonly uint Pad0;
	public readonly uint Pad1;
	public readonly uint Pad2;
}

/// <summary>One skinned instance to deform this frame.</summary>
public readonly struct SkinningPacket
{
	public SkinningPacket(
		Mesh sourceMesh,
		Mesh instanceMesh,
		int boneMatrixOffset,
		int previousBoneMatrixOffset,
		int boneCount)
	{
		SourceMesh = sourceMesh;
		InstanceMesh = instanceMesh;
		BoneMatrixOffset = boneMatrixOffset;
		PreviousBoneMatrixOffset = previousBoneMatrixOffset;
		BoneCount = boneCount;
	}

	/// <summary>The shared bind-pose mesh the deformation reads from.</summary>
	public Mesh SourceMesh { get; }

	/// <summary>The instance-owned mesh the deformation is written into.</summary>
	public Mesh InstanceMesh { get; }

	/// <summary>Current-pose offset in <see cref="FrameSnapshot.BoneMatrices"/>.</summary>
	public int BoneMatrixOffset { get; }

	/// <summary>Start of the matrices for the pose the previous frame rendered.</summary>
	public int PreviousBoneMatrixOffset { get; }

	public int BoneCount { get; }
}

/// <summary>
/// Deforms every visible skinned instance into its own range of the packed vertex buffer.
/// </summary>
/// <remarks>
/// This runs inside the GpuDraw update pass rather than as its own render-graph pass, and the
/// ordering there is load-bearing: mesh GPU ranges are allocated by <c>GpuDrawPass.RecordUpdate</c>,
/// and bottom-level acceleration structures are built by <c>RayTracingSceneResources.RecordUpdate</c>.
/// Skinning has to sit between the two, so that the acceleration structures are built over vertices
/// this frame produced rather than last frame's.
/// </remarks>
public sealed class SkinningPass
{
	private readonly IShaderProvider _shaderProvider;
	private readonly Dictionary<Mesh, SkinAttributeRange> _skinRangesBySourceMesh = new(new ReferenceComparer<Mesh>());
	private readonly List<Mesh> _skinUploadOrder = new();

	private IGfxPipeline? _pipeline;
	private ShaderPropertyWriter? _paramsWriter;
	private ComputeThreadGroupSize? _threadGroupSize;
	private ReadOnlyMemory<byte>? _shaderBytecode;

	private IGfxBuffer? _skinVertexBuffer;
	private uint _skinVertexCapacity;
	private uint _skinVertexCount;

	private IGfxBuffer? _boneMatrixBuffer;
	private int _boneMatrixCapacity;
	private Matrix4x4[] _boneMatrixStaging = [];

	private IGfxBuffer? _skinnedInstanceBuffer;
	private readonly List<uint> _publishedInstanceSlots = new();
	private readonly List<uint> _previouslyPublishedInstanceSlots = new();

	private int _lastDispatchedInstanceCount;

	public SkinningPass(IShaderProvider shaderProvider)
	{
		_shaderProvider = shaderProvider ?? throw new ArgumentNullException(nameof(shaderProvider));
	}

	/// <summary>Skinned instances deformed during the most recent recording. Used by tests and diagnostics.</summary>
	public int LastDispatchedInstanceCount => _lastDispatchedInstanceCount;

	public IGfxBuffer? SkinVertexBuffer => _skinVertexBuffer;

	public IGfxBuffer? BoneMatrixBuffer => _boneMatrixBuffer;

	public IGfxBuffer? SkinnedInstanceBuffer => _skinnedInstanceBuffer;

	public void Record(
		IGfxCommandList commandList,
		IGfxDevice device,
		IRenderer renderer,
		IReadOnlyList<SkinningPacket> packets,
		ReadOnlySpan<Matrix4x4> boneMatrices,
		GpuDrawDatabase drawDatabase)
	{
		ArgumentNullException.ThrowIfNull(commandList);
		ArgumentNullException.ThrowIfNull(device);
		ArgumentNullException.ThrowIfNull(renderer);
		ArgumentNullException.ThrowIfNull(drawDatabase);

		_lastDispatchedInstanceCount = 0;
		if (EnsureResources(device) == false)
		{
			return;
		}

		var packedVertexBuffer = renderer.GetPackedMeshVertexBuffer();
		if (packets is null || packets.Count == 0 || packedVertexBuffer is null)
		{
			RetireStaleSkinnedInstances();
			return;
		}

		if (TryEnsureSkinVertices(device, packets) == false ||
		    TryUploadBoneMatrices(device, packets, boneMatrices, out var boneMatrixOffsets) == false)
		{
			RetireStaleSkinnedInstances();
			return;
		}

		var pipeline = EnsurePipeline(device);
		var writer = _paramsWriter
			?? throw new InvalidOperationException("Skinning parameters were not reflected.");
		var threadGroupSize = _threadGroupSize
			?? throw new InvalidOperationException("Skinning thread group size was not reflected.");

		commandList.BindPipeline(pipeline);
		commandList.SetComputeBuffer(0, packedVertexBuffer);
		commandList.SetComputeReadOnlyBuffer(2, _skinVertexBuffer!);
		commandList.SetComputeReadOnlyBuffer(3, _boneMatrixBuffer!);

		_publishedInstanceSlots.Clear();

		for (var i = 0; i < packets.Count; i++)
		{
			var packet = packets[i];
			var instance = packet.InstanceMesh;
			if (instance.VertexBuffer is null || _skinRangesBySourceMesh.TryGetValue(packet.SourceMesh, out var skinRange) == false)
			{
				continue;
			}

			var vertexCount = (uint)packet.SourceMesh.Vertices.Length;
			if (vertexCount == 0 || packet.BoneCount == 0)
			{
				continue;
			}

			var boneMatrixOffset = boneMatrixOffsets[i];
			writer.Clear();
			writer.SetUInt("vertexCount", vertexCount);
			writer.SetUInt("sourceVertexIndex", (uint)packet.SourceMesh.PackedBaseVertex);
			writer.SetUInt("destVertexIndex", (uint)instance.PackedBaseVertex);
			writer.SetUInt("skinVertexIndex", skinRange.FirstVertex);
			writer.SetUInt("boneMatrixOffset", boneMatrixOffset);
			writer.SetUInt("boneCount", (uint)packet.BoneCount);
			commandList.SetComputeConstants(writer.RegisterIndex, writer.AsBytes());

			var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(vertexCount, 1, 1);
			commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
			_lastDispatchedInstanceCount++;

			PublishSkinnedInstance(
				drawDatabase,
				instance,
				skinRange.FirstVertex,
				boneMatrixOffset,
				boneMatrixOffset + (uint)packet.BoneCount,
				(uint)packet.BoneCount);
		}

		RetireStaleSkinnedInstances();

		if (_lastDispatchedInstanceCount > 0)
		{
			// The deformed vertices are consumed as geometry by the draw passes and by acceleration
			// structure builds, so the writes have to land before either reads them.
			commandList.Barrier(new ResourceBarrierDescription(
				packedVertexBuffer,
				ResourceState.UnorderedAccess,
				ResourceState.ShaderResource));
		}
	}

	/// <summary>Uploads static skin data for newly seen source meshes.</summary>
	private bool TryEnsureSkinVertices(IGfxDevice device, IReadOnlyList<SkinningPacket> packets)
	{
		var newVertices = 0u;
		for (var i = 0; i < packets.Count; i++)
		{
			var source = packets[i].SourceMesh;
			if (source.IsSkinned && _skinRangesBySourceMesh.ContainsKey(source) == false)
			{
				newVertices += (uint)source.Vertices.Length;
			}
		}

		if (newVertices == 0)
		{
			return _skinVertexBuffer is not null;
		}

		var requiredVertices = _skinVertexCount + newVertices;
		if (_skinVertexBuffer is null || requiredVertices > _skinVertexCapacity)
		{
			// A replacement buffer requires re-uploading registered meshes.
			var capacity = Math.Max(requiredVertices, Math.Max(_skinVertexCapacity * 2, 65536u));
			if (TryCreateSkinVertexBuffer(device, capacity) == false)
			{
				return false;
			}

			var previousOrder = new List<Mesh>(_skinUploadOrder);
			_skinRangesBySourceMesh.Clear();
			_skinUploadOrder.Clear();
			for (var i = 0; i < previousOrder.Count; i++)
			{
				AppendSkinVertices(previousOrder[i]);
			}
		}

		for (var i = 0; i < packets.Count; i++)
		{
			var source = packets[i].SourceMesh;
			if (source.IsSkinned && _skinRangesBySourceMesh.ContainsKey(source) == false)
			{
				AppendSkinVertices(source);
			}
		}

		return true;
	}

	private void AppendSkinVertices(Mesh source)
	{
		if (_skinVertexBuffer is not IWritableGpuBuffer writable || source.IsSkinned == false)
		{
			return;
		}

		var vertexCount = source.Vertices.Length;
		var skinVertices = new GpuSkinVertex[vertexCount];
		var positions = source.Vertices;
		var boneIndices = source.BoneIndices;
		var boneWeights = source.BoneWeights;
		for (var vertex = 0; vertex < vertexCount; vertex++)
		{
			var offset = vertex * Mesh.InfluencesPerVertex;
			skinVertices[vertex] = new GpuSkinVertex(
				new Vector4(positions[vertex].X, positions[vertex].Y, positions[vertex].Z, 1.0f),
				boneIndices[offset + 0],
				boneIndices[offset + 1],
				boneIndices[offset + 2],
				boneIndices[offset + 3],
				new Vector4(
					boneWeights[offset + 0],
					boneWeights[offset + 1],
					boneWeights[offset + 2],
					boneWeights[offset + 3]));
		}

		writable.Write<GpuSkinVertex>(skinVertices, _skinVertexCount);
		_skinRangesBySourceMesh[source] = new SkinAttributeRange(_skinVertexCount, (uint)vertexCount);
		_skinUploadOrder.Add(source);
		_skinVertexCount += (uint)vertexCount;
	}

	/// <summary>Allocates the skinning buffers before graphics bindings are built.</summary>
	public bool EnsureResources(IGfxDevice device)
	{
		ArgumentNullException.ThrowIfNull(device);
		if (_skinnedInstanceBuffer is null)
		{
			var buffer = device.CreateBuffer(new BufferDescriptor(
				(ulong)GpuDrawResources.MaxMeshCount * (ulong)Marshal.SizeOf<GpuSkinnedInstanceData>(),
				BufferUsage.Structured,
				BufferFlags.AllowShaderResource,
				"SkinnedInstanceBuffer"));
			if (buffer is not IWritableGpuBuffer writableInstances)
			{
				return false;
			}

			writableInstances.Write<GpuSkinnedInstanceData>(new GpuSkinnedInstanceData[GpuDrawResources.MaxMeshCount]);
			_skinnedInstanceBuffer = buffer;
		}

		if (_skinVertexBuffer is null && TryCreateSkinVertexBuffer(device, 1u) == false)
		{
			return false;
		}

		if (_boneMatrixBuffer is null && TryCreateBoneMatrixBuffer(device, 1) == false)
		{
			return false;
		}

		return true;
	}

	private bool TryCreateSkinVertexBuffer(IGfxDevice device, uint capacity)
	{
		var buffer = device.CreateBuffer(new BufferDescriptor(
			(ulong)capacity * (ulong)Marshal.SizeOf<GpuSkinVertex>(),
			BufferUsage.Structured,
			BufferFlags.AllowShaderResource,
			"SkinVertexBuffer"));
		if (buffer is not IWritableGpuBuffer)
		{
			return false;
		}

		var previousBuffer = _skinVertexBuffer;
		if (previousBuffer is not null)
		{
			device.Retire(() => (previousBuffer as IDisposable)?.Dispose(), "SkinVertexBuffer");
		}

		_skinVertexBuffer = buffer;
		_skinVertexCapacity = capacity;
		_skinVertexCount = 0;
		return true;
	}

	private bool TryCreateBoneMatrixBuffer(IGfxDevice device, int capacity)
	{
		var buffer = device.CreateBuffer(new BufferDescriptor(
			(ulong)capacity * (ulong)Marshal.SizeOf<Matrix4x4>(),
			BufferUsage.Structured,
			BufferFlags.AllowShaderResource,
			"SkinningBoneMatrixBuffer"));
		if (buffer is not IWritableGpuBuffer)
		{
			return false;
		}

		var previousBuffer = _boneMatrixBuffer;
		if (previousBuffer is not null)
		{
			device.Retire(() => (previousBuffer as IDisposable)?.Dispose(), "SkinningBoneMatrixBuffer");
		}

		_boneMatrixBuffer = buffer;
		_boneMatrixCapacity = capacity;
		return true;
	}

	/// <summary>Publishes skinning ranges at the mesh handle's index.</summary>
	private void PublishSkinnedInstance(
		GpuDrawDatabase drawDatabase,
		Mesh instance,
		uint skinVertexBase,
		uint boneMatrixOffset,
		uint previousBoneMatrixOffset,
		uint boneCount)
	{
		if (_skinnedInstanceBuffer is not IWritableGpuBuffer writable ||
		    drawDatabase.TryGetMeshHandle(instance, out var meshHandle) == false ||
		    meshHandle.IsValid == false ||
		    meshHandle.Index >= GpuDrawResources.MaxMeshCount)
		{
			return;
		}

		var slot = (uint)meshHandle.Index;
		Span<GpuSkinnedInstanceData> entry =
		[
			new GpuSkinnedInstanceData(
				meshHandle.Value,
				skinVertexBase,
				boneMatrixOffset,
				previousBoneMatrixOffset,
				boneCount)
		];
		writable.Write<GpuSkinnedInstanceData>(entry, slot);
		_publishedInstanceSlots.Add(slot);
	}

	/// <summary>Clears stale entries because mesh handles are recycled.</summary>
	private void RetireStaleSkinnedInstances()
	{
		if (_skinnedInstanceBuffer is IWritableGpuBuffer writable)
		{
			for (var i = 0; i < _previouslyPublishedInstanceSlots.Count; i++)
			{
				var slot = _previouslyPublishedInstanceSlots[i];
				if (_publishedInstanceSlots.Contains(slot))
				{
					continue;
				}

				Span<GpuSkinnedInstanceData> cleared = [default];
				writable.Write<GpuSkinnedInstanceData>(cleared, slot);
			}
		}

		_previouslyPublishedInstanceSlots.Clear();
		_previouslyPublishedInstanceSlots.AddRange(_publishedInstanceSlots);
		_publishedInstanceSlots.Clear();
	}

	/// <summary>Packs current and previous matrices for GPU upload.</summary>
	private bool TryUploadBoneMatrices(
		IGfxDevice device,
		IReadOnlyList<SkinningPacket> packets,
		ReadOnlySpan<Matrix4x4> boneMatrices,
		out uint[] boneMatrixOffsets)
	{
		boneMatrixOffsets = new uint[packets.Count];
		var totalMatrices = 0;
		for (var i = 0; i < packets.Count; i++)
		{
			boneMatrixOffsets[i] = (uint)totalMatrices;
			totalMatrices += packets[i].BoneCount * 2;
		}

		if (totalMatrices == 0)
		{
			return false;
		}

		if (_boneMatrixBuffer is null || totalMatrices > _boneMatrixCapacity)
		{
			var capacity = Math.Max(totalMatrices, Math.Max(_boneMatrixCapacity * 2, 1024));
			if (TryCreateBoneMatrixBuffer(device, capacity) == false)
			{
				return false;
			}
		}

		if (_boneMatrixStaging.Length < totalMatrices)
		{
			_boneMatrixStaging = new Matrix4x4[totalMatrices];
		}

		var staging = _boneMatrixStaging.AsSpan();
		for (var i = 0; i < packets.Count; i++)
		{
			var packet = packets[i];
			var boneCount = packet.BoneCount;
			if (packet.BoneMatrixOffset + boneCount > boneMatrices.Length ||
			    packet.PreviousBoneMatrixOffset + boneCount > boneMatrices.Length)
			{
				return false;
			}

			var destination = (int)boneMatrixOffsets[i];
			boneMatrices.Slice(packet.BoneMatrixOffset, boneCount).CopyTo(staging[destination..]);
			boneMatrices.Slice(packet.PreviousBoneMatrixOffset, boneCount).CopyTo(staging[(destination + boneCount)..]);
		}

		((IWritableGpuBuffer)_boneMatrixBuffer!).Write<Matrix4x4>(staging[..totalMatrices]);
		return true;
	}

	private IGfxPipeline EnsurePipeline(IGfxDevice device)
	{
		if (_pipeline is not null)
		{
			return _pipeline;
		}

		var compiled = _shaderProvider.GetComputeShaderWithReflection(
			EngineShaderPrograms.Skinning,
			"SkinningCS",
			device.BackendKind);
		_shaderBytecode = compiled.Bytecode;
		_threadGroupSize = compiled.ThreadGroupSize;
		_paramsWriter = new ShaderPropertyWriter(compiled.ReflectionLayout.GetConstantBuffer("SkinningParams"));

		var pipelineKey = new PipelineKey(
			PassKind.Compute,
			null,
			null,
			"SkinningCS",
			default,
			default,
			default,
			shaderVariant: "skinning.compute.slang");
		_pipeline = device.GetOrCreatePipeline(
			pipelineKey,
			new ShaderBytecodeSet(compute: _shaderBytecode, computeThreadGroupSize: _threadGroupSize));
		return _pipeline;
	}

	/// <summary>Drops cached shader state so a shader reload rebuilds the pipeline.</summary>
	public void InvalidateShaders()
	{
		_pipeline = null;
		_paramsWriter = null;
		_threadGroupSize = null;
		_shaderBytecode = null;
	}

	private readonly record struct SkinAttributeRange(uint FirstVertex, uint VertexCount);

	private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
	{
		public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

		public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
	}
}
