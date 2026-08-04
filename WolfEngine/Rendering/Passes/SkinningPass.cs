#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Passes;

/// <summary>Per-vertex skin influences, as the skinning shader reads them.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly struct GpuSkinAttribute
{
	internal GpuSkinAttribute(uint index0, uint index1, uint index2, uint index3, Vector4 weights)
	{
		Index0 = index0;
		Index1 = index1;
		Index2 = index2;
		Index3 = index3;
		Weights = weights;
	}

	public readonly uint Index0;
	public readonly uint Index1;
	public readonly uint Index2;
	public readonly uint Index3;
	public readonly Vector4 Weights;
}

/// <summary>One skinned instance to deform this frame.</summary>
public readonly struct SkinningPacket
{
	public SkinningPacket(Mesh sourceMesh, Mesh instanceMesh, Matrix4x4[] boneMatrices, int boneCount)
	{
		SourceMesh = sourceMesh;
		InstanceMesh = instanceMesh;
		BoneMatrices = boneMatrices;
		BoneCount = boneCount;
	}

	/// <summary>The shared bind-pose mesh the deformation reads from.</summary>
	public Mesh SourceMesh { get; }

	/// <summary>The instance-owned mesh the deformation is written into.</summary>
	public Mesh InstanceMesh { get; }

	/// <summary>
	/// Skinning matrices for this instance, copied at snapshot time. The render thread must not
	/// reach back into the animator for these.
	/// </summary>
	public Matrix4x4[] BoneMatrices { get; }

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

	private IGfxBuffer? _skinAttributeBuffer;
	private uint _skinAttributeVertexCapacity;
	private uint _skinAttributeVertexCount;

	private IGfxBuffer? _boneMatrixBuffer;
	private int _boneMatrixCapacity;
	private Matrix4x4[] _boneMatrixStaging = [];

	private int _lastDispatchedInstanceCount;

	public SkinningPass(IShaderProvider shaderProvider)
	{
		_shaderProvider = shaderProvider ?? throw new ArgumentNullException(nameof(shaderProvider));
	}

	/// <summary>Skinned instances deformed during the most recent recording. Used by tests and diagnostics.</summary>
	public int LastDispatchedInstanceCount => _lastDispatchedInstanceCount;

	public void Record(
		IGfxCommandList commandList,
		IGfxDevice device,
		IRenderer renderer,
		IReadOnlyList<SkinningPacket> packets)
	{
		ArgumentNullException.ThrowIfNull(commandList);
		ArgumentNullException.ThrowIfNull(device);
		ArgumentNullException.ThrowIfNull(renderer);

		_lastDispatchedInstanceCount = 0;
		if (packets is null || packets.Count == 0)
		{
			return;
		}

		var packedVertexBuffer = renderer.GetPackedMeshVertexBuffer();
		if (packedVertexBuffer is null)
		{
			return;
		}

		if (TryEnsureSkinAttributes(device, packets) == false)
		{
			return;
		}

		if (TryUploadBoneMatrices(device, packets, out var boneMatrixOffsets) == false)
		{
			return;
		}

		var pipeline = EnsurePipeline(device);
		var writer = _paramsWriter
			?? throw new InvalidOperationException("Skinning parameters were not reflected.");
		var threadGroupSize = _threadGroupSize
			?? throw new InvalidOperationException("Skinning thread group size was not reflected.");

		commandList.BindPipeline(pipeline);
		commandList.SetComputeBuffer(0, packedVertexBuffer);
		commandList.SetComputeBuffer(1, _skinAttributeBuffer!);
		commandList.SetComputeBuffer(2, _boneMatrixBuffer!);

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

			writer.Clear();
			writer.SetUInt("vertexCount", vertexCount);
			writer.SetUInt("sourceVertexIndex", (uint)packet.SourceMesh.PackedBaseVertex);
			writer.SetUInt("destVertexIndex", (uint)instance.PackedBaseVertex);
			writer.SetUInt("skinVertexIndex", skinRange.FirstVertex);
			writer.SetUInt("boneMatrixOffset", boneMatrixOffsets[i]);
			writer.SetUInt("boneCount", (uint)packet.BoneCount);
			commandList.SetComputeConstants(writer.RegisterIndex, writer.AsBytes());

			var (dispatchX, dispatchY, dispatchZ) = threadGroupSize.GetDispatchGroupCount(vertexCount, 1, 1);
			commandList.Dispatch(dispatchX, dispatchY, dispatchZ);
			_lastDispatchedInstanceCount++;
		}

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

	/// <summary>
	/// Uploads skin influences for any source mesh not seen before. Influences never change, so this
	/// costs nothing after a character's first frame.
	/// </summary>
	private bool TryEnsureSkinAttributes(IGfxDevice device, IReadOnlyList<SkinningPacket> packets)
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
			return _skinAttributeBuffer is not null;
		}

		var requiredVertices = _skinAttributeVertexCount + newVertices;
		if (_skinAttributeBuffer is null || requiredVertices > _skinAttributeVertexCapacity)
		{
			// Growing means a new buffer, so everything already registered is re-uploaded alongside
			// the new meshes. Character meshes are few and this only runs when one first appears.
			var capacity = Math.Max(requiredVertices, Math.Max(_skinAttributeVertexCapacity * 2, 65536u));
			var buffer = device.CreateBuffer(new BufferDescriptor(
				(ulong)capacity * (ulong)Marshal.SizeOf<GpuSkinAttribute>(),
				BufferUsage.Structured,
				BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource,
				"SkinAttributeBuffer"));
			if (buffer is not IWritableGpuBuffer)
			{
				return false;
			}

			var previousBuffer = _skinAttributeBuffer;
			if (previousBuffer is not null)
			{
				device.Retire(() => (previousBuffer as IDisposable)?.Dispose(), "SkinAttributeBuffer");
			}

			_skinAttributeBuffer = buffer;
			_skinAttributeVertexCapacity = capacity;
			_skinAttributeVertexCount = 0;
			var previousOrder = new List<Mesh>(_skinUploadOrder);
			_skinRangesBySourceMesh.Clear();
			_skinUploadOrder.Clear();
			for (var i = 0; i < previousOrder.Count; i++)
			{
				AppendSkinAttributes(previousOrder[i]);
			}
		}

		for (var i = 0; i < packets.Count; i++)
		{
			var source = packets[i].SourceMesh;
			if (source.IsSkinned && _skinRangesBySourceMesh.ContainsKey(source) == false)
			{
				AppendSkinAttributes(source);
			}
		}

		return true;
	}

	private void AppendSkinAttributes(Mesh source)
	{
		if (_skinAttributeBuffer is not IWritableGpuBuffer writable || source.IsSkinned == false)
		{
			return;
		}

		var vertexCount = source.Vertices.Length;
		var attributes = new GpuSkinAttribute[vertexCount];
		var boneIndices = source.BoneIndices;
		var boneWeights = source.BoneWeights;
		for (var vertex = 0; vertex < vertexCount; vertex++)
		{
			var offset = vertex * Mesh.InfluencesPerVertex;
			attributes[vertex] = new GpuSkinAttribute(
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

		writable.Write<GpuSkinAttribute>(attributes, _skinAttributeVertexCount);
		_skinRangesBySourceMesh[source] = new SkinAttributeRange(_skinAttributeVertexCount, (uint)vertexCount);
		_skinUploadOrder.Add(source);
		_skinAttributeVertexCount += (uint)vertexCount;
	}

	private bool TryUploadBoneMatrices(
		IGfxDevice device,
		IReadOnlyList<SkinningPacket> packets,
		out uint[] boneMatrixOffsets)
	{
		boneMatrixOffsets = new uint[packets.Count];
		var totalBones = 0;
		for (var i = 0; i < packets.Count; i++)
		{
			boneMatrixOffsets[i] = (uint)totalBones;
			totalBones += packets[i].BoneCount;
		}

		if (totalBones == 0)
		{
			return false;
		}

		if (_boneMatrixBuffer is null || totalBones > _boneMatrixCapacity)
		{
			var capacity = Math.Max(totalBones, Math.Max(_boneMatrixCapacity * 2, 1024));
			var buffer = device.CreateBuffer(new BufferDescriptor(
				(ulong)capacity * (ulong)Marshal.SizeOf<Matrix4x4>(),
				BufferUsage.Structured,
				BufferFlags.AllowUnorderedAccess | BufferFlags.AllowShaderResource,
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
		}

		if (_boneMatrixStaging.Length < totalBones)
		{
			_boneMatrixStaging = new Matrix4x4[totalBones];
		}

		for (var i = 0; i < packets.Count; i++)
		{
			var packet = packets[i];
			Array.Copy(packet.BoneMatrices, 0, _boneMatrixStaging, boneMatrixOffsets[i], packet.BoneCount);
		}

		((IWritableGpuBuffer)_boneMatrixBuffer!).Write<Matrix4x4>(_boneMatrixStaging.AsSpan(0, totalBones));
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
