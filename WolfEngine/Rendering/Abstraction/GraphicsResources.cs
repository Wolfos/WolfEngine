#nullable enable

using System;
using WolfEngine.Rendering;

namespace WolfEngine.Rendering.Abstraction;

/// <summary>
/// Shared base type for all GPU resources surfaced through the abstraction layer.
/// </summary>
public interface IGfxResource
{
	string? Name { get; }
}

public interface IGfxTexture : IGfxResource
{
	TextureDescriptor Descriptor { get; }
}

public interface IGfxBuffer : IGfxResource
{
	BufferDescriptor Descriptor { get; }
}

public interface IGfxPipeline : IGfxResource
{
	PipelineKey Key { get; }
}

/// <summary>
/// Represents the bindless descriptor table shared across passes.
/// </summary>
public interface IGfxDescriptorTable
{
	DescriptorHandle AllocateShaderResourceView(IGfxResource resource);

	DescriptorHandle AllocateUnorderedAccessView(IGfxResource resource);

	DescriptorHandle AllocateConstantBufferView(IGfxBuffer buffer);

	DescriptorHandle AllocateSampler(in SamplerDescriptor sampler);
}

/// <summary>
/// Identifier returned when allocating into the descriptor table.
/// </summary>
public readonly struct DescriptorHandle : IEquatable<DescriptorHandle>
{
	public DescriptorHandle(int index)
	{
		if (index < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(index), "Descriptor index must be non-negative.");
		}

		Index = index;
	}

	public int Index { get; }

	public bool Equals(DescriptorHandle other) => Index == other.Index;

	public override bool Equals(object? obj) => obj is DescriptorHandle other && Equals(other);

	public override int GetHashCode() => Index;
}

/// <summary>
/// Describes a GPU buffer allocation request.
/// </summary>
public readonly struct BufferDescriptor
{
	public BufferDescriptor(ulong sizeInBytes, BufferUsage usage, BufferFlags flags = BufferFlags.None)
	{
		if (sizeInBytes == 0)
		{
			throw new ArgumentOutOfRangeException(nameof(sizeInBytes), "Buffer size must be greater than zero.");
		}

		SizeInBytes = sizeInBytes;
		Usage = usage;
		Flags = flags;
	}

	public ulong SizeInBytes { get; }

	public BufferUsage Usage { get; }

	public BufferFlags Flags { get; }
}

[Flags]
public enum BufferUsage
{
	Vertex = 1 << 0,
	Index = 1 << 1,
	Constant = 1 << 2,
	Structured = 1 << 3,
	Indirect = 1 << 4,
	Staging = 1 << 5
}

[Flags]
public enum BufferFlags
{
	None = 0,
	AllowUnorderedAccess = 1 << 0,
	AllowShaderResource = 1 << 1
}

/// <summary>
/// Aggregates shader bytecode per stage when requesting a pipeline.
/// </summary>
public readonly struct ShaderBytecodeSet
{
	public ShaderBytecodeSet(ReadOnlyMemory<byte>? vertex = null, ReadOnlyMemory<byte>? pixel = null, ReadOnlyMemory<byte>? compute = null)
	{
		Vertex = vertex;
		Pixel = pixel;
		Compute = compute;
	}

	public ReadOnlyMemory<byte>? Vertex { get; }

	public ReadOnlyMemory<byte>? Pixel { get; }

	public ReadOnlyMemory<byte>? Compute { get; }
}
