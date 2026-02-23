#nullable enable

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
	DescriptorHandle ShaderResourceView { get; }
	DescriptorHandle DepthShaderResourceView { get; }
	DescriptorHandle UnorderedAccessView { get; }
}

public interface IGfxBuffer : IGfxResource
{
	BufferDescriptor Descriptor { get; }
}

public interface IWritableGpuBuffer : IGfxBuffer
{
	void Write<T>(ReadOnlySpan<T> source, ulong elementOffset = 0) where T : unmanaged;
}

public interface IGfxPipeline : IGfxResource
{
	PipelineKey Key { get; }
}

public interface IGfxIndirectCommandBuffer : IGfxResource
{
	IndirectCommandBufferDescriptor Descriptor { get; }
}

/// <summary>
/// Represents the bindless descriptor table shared across passes.
/// </summary>
public interface IGfxDescriptorTable
{
	DescriptorHandle AllocateShaderResourceView(IGfxResource resource);

	DescriptorHandle AllocateDepthShaderResourceView(IGfxTexture texture);

	DescriptorHandle AllocateUnorderedAccessView(IGfxResource resource);

	DescriptorHandle AllocateConstantBufferView(IGfxBuffer buffer);

	DescriptorHandle AllocateSampler(in SamplerDescriptor sampler);
}

/// <summary>
/// Identifier returned when allocating into the descriptor table.
/// </summary>
public enum DescriptorKind : uint
{
	ShaderResourceView = 0,
	UnorderedAccessView = 1,
	ConstantBufferView = 2,
	Sampler = 3
}

/// <summary>
/// Packed descriptor handle: high 2 bits = kind, low 30 bits = index.
/// </summary>
public readonly struct DescriptorHandle : IEquatable<DescriptorHandle>
{
	private const uint KindShift = 30;
	private const uint KindMask = 0b11u;
	private const uint IndexMask = (1u << (int)KindShift) - 1u;
	private const uint InvalidValue = 0xFFFFFFFF;

	public DescriptorHandle(DescriptorKind kind, int index)
	{
		if (index < 0 || (uint)index > IndexMask)
		{
			throw new ArgumentOutOfRangeException(nameof(index), "Descriptor index must be within 0..(2^30-1).");
		}

		var kindBits = ((uint)kind & KindMask) << (int)KindShift;
		Value = kindBits | (uint)index;
	}

	private DescriptorHandle(uint value)
	{
		Value = value;
	}

	public static DescriptorHandle Invalid => new(InvalidValue);

	public bool IsValid => Value != InvalidValue;

	public DescriptorKind Kind => IsValid
		? (DescriptorKind)((Value >> (int)KindShift) & KindMask)
		: throw new InvalidOperationException("Descriptor handle is invalid.");

	public int Index => IsValid
		? (int)(Value & IndexMask)
		: throw new InvalidOperationException("Descriptor handle is invalid.");

	public uint Value { get; }

	public bool Equals(DescriptorHandle other) => Value == other.Value;

	public override bool Equals(object? obj) => obj is DescriptorHandle other && Equals(other);

	public override int GetHashCode() => (int)Value;
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
