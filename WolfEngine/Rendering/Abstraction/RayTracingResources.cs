#nullable enable

using System;
using System.Numerics;

namespace WolfEngine.Rendering.Abstraction;

public interface IGfxBottomLevelAccelerationStructure : IGfxResource
{
	BottomLevelAccelerationStructureDescriptor Descriptor { get; }
}

public interface IGfxTopLevelAccelerationStructure : IGfxResource
{
	TopLevelAccelerationStructureDescriptor Descriptor { get; }
}

public readonly struct BottomLevelAccelerationStructureDescriptor
{
	public BottomLevelAccelerationStructureDescriptor(
		IGfxBuffer vertexBuffer,
		ulong vertexBufferOffsetBytes,
		uint vertexStrideBytes,
		uint vertexCount,
		IGfxBuffer indexBuffer,
		ulong indexBufferOffsetBytes,
		uint indexCount)
	{
		VertexBuffer = vertexBuffer ?? throw new ArgumentNullException(nameof(vertexBuffer));
		VertexBufferOffsetBytes = vertexBufferOffsetBytes;
		VertexStrideBytes = vertexStrideBytes;
		VertexCount = vertexCount;
		IndexBuffer = indexBuffer ?? throw new ArgumentNullException(nameof(indexBuffer));
		IndexBufferOffsetBytes = indexBufferOffsetBytes;
		IndexCount = indexCount;
	}

	public IGfxBuffer VertexBuffer { get; }
	public ulong VertexBufferOffsetBytes { get; }
	public uint VertexStrideBytes { get; }
	public uint VertexCount { get; }
	public IGfxBuffer IndexBuffer { get; }
	public ulong IndexBufferOffsetBytes { get; }
	public uint IndexCount { get; }
	public uint TriangleCount => IndexCount / 3;
}

public readonly struct TopLevelAccelerationStructureDescriptor
{
	public TopLevelAccelerationStructureDescriptor(uint maxInstanceCount)
	{
		if (maxInstanceCount == 0)
		{
			throw new ArgumentOutOfRangeException(nameof(maxInstanceCount), "TLAS instance capacity must be greater than zero.");
		}

		MaxInstanceCount = maxInstanceCount;
	}

	public uint MaxInstanceCount { get; }
}

public readonly struct RayTracingInstanceDescription
{
	public RayTracingInstanceDescription(
		uint instanceIndex,
		IGfxBottomLevelAccelerationStructure accelerationStructure,
		Matrix4x4 transform,
		uint mask = 0xFF,
		bool active = true)
	{
		InstanceIndex = instanceIndex;
		AccelerationStructure = accelerationStructure ?? throw new ArgumentNullException(nameof(accelerationStructure));
		Transform = transform;
		Mask = mask;
		Active = active;
	}

	public uint InstanceIndex { get; }
	public IGfxBottomLevelAccelerationStructure AccelerationStructure { get; }
	public Matrix4x4 Transform { get; }
	public uint Mask { get; }
	public bool Active { get; }
}
