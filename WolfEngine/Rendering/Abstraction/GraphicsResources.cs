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

public interface IReadableGpuBuffer : IGfxBuffer
{
	void Read(Span<byte> destination, ulong sourceOffset = 0);
}

public interface IGfxPipeline : IGfxResource
{
	PipelineKey Key { get; }
}

/// <summary>
/// How a backend compacts an indirect command buffer down to the draws culling left visible, which
/// is what lets execution stop scaling with the size of the scene rather than with what survived.
/// </summary>
public enum IndirectCompactionKind
{
	/// <summary>The backend cannot compact, so the caller executes the full command range instead.</summary>
	None,

	/// <summary>
	/// Commands are plain records in a buffer, so a shared compute kernel copies the surviving records
	/// from <see cref="IGfxIndirectCommandBuffer.TemplateRecordBuffer"/> into
	/// <see cref="IGfxIndirectCommandBuffer.CompactedRecordBuffer"/>.
	/// </summary>
	CommandRecords,

	/// <summary>
	/// Commands are opaque objects that no shader can copy as memory, so the backend records the
	/// compaction itself through <see cref="IGfxCommandList.RecordNativeIndirectCompaction"/>.
	/// </summary>
	NativeCommands
}

/// <summary>
/// Layout of the entries compaction writes and the following execute reads: <c>{ location, length }</c>,
/// which is an execution range Metal consumes whole. Compaction always emits from index zero, so
/// location stays zero and backends that want only a count read the length field.
/// </summary>
public static class IndirectCompactionExecutionRange
{
	public const int StrideInBytes = 2 * sizeof(uint);

	public const int LengthOffsetInBytes = sizeof(uint);
}

public interface IGfxIndirectCommandBuffer : IGfxResource
{
	IndirectCommandBufferDescriptor Descriptor { get; }

	/// <summary>
	/// How this buffer's commands can be compacted, or <see cref="IndirectCompactionKind.None"/> when
	/// the backend cannot, in which case it is executed through its full command range instead.
	/// </summary>
	IndirectCompactionKind CompactionKind => IndirectCompactionKind.None;

	/// <summary>
	/// The CPU-encoded command records, readable by a compute shader as the compaction source.
	/// Null unless <see cref="CompactionKind"/> is <see cref="IndirectCompactionKind.CommandRecords"/>.
	/// </summary>
	IGfxBuffer? TemplateRecordBuffer => null;

	/// <summary>
	/// The dense command records written by compaction and consumed by the following ExecuteIndirect.
	/// Null unless <see cref="CompactionKind"/> is <see cref="IndirectCompactionKind.CommandRecords"/>.
	/// </summary>
	IGfxBuffer? CompactedRecordBuffer => null;

	/// <summary>
	/// Size of a single command record. Compaction copies records opaquely, so it only needs the
	/// stride and the offset below rather than the backend's record layout.
	/// </summary>
	uint RecordStrideInBytes => 0;

	/// <summary>Byte offset of the draw index count within a record, used to skip records that draw nothing.</summary>
	uint RecordIndexCountOffsetInBytes => 0;
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
	BindlessFallbackHandles GetOrCreateFallbackHandles();
	void Free(DescriptorHandle handle);
}

/// <summary>
/// Fallback descriptors used when a bindless handle is invalid or unavailable.
/// </summary>
public readonly struct BindlessFallbackHandles
{
	public BindlessFallbackHandles(
		DescriptorHandle shaderResourceView,
		DescriptorHandle unorderedAccessView,
		DescriptorHandle constantBufferView,
		DescriptorHandle sampler)
	{
		ShaderResourceView = shaderResourceView;
		UnorderedAccessView = unorderedAccessView;
		ConstantBufferView = constantBufferView;
		Sampler = sampler;
	}

	public DescriptorHandle ShaderResourceView { get; }

	public DescriptorHandle UnorderedAccessView { get; }

	public DescriptorHandle ConstantBufferView { get; }

	public DescriptorHandle Sampler { get; }
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
	public BufferDescriptor(ulong sizeInBytes, BufferUsage usage, BufferFlags flags = BufferFlags.None, string? name = null)
	{
		if (sizeInBytes == 0)
		{
			throw new ArgumentOutOfRangeException(nameof(sizeInBytes), "Buffer size must be greater than zero.");
		}

		SizeInBytes = sizeInBytes;
		Usage = usage;
		Flags = flags;
		Name = name;
	}

	public ulong SizeInBytes { get; }

	public BufferUsage Usage { get; }

	public BufferFlags Flags { get; }

	/// <summary>
	/// Diagnostic name. Worth setting on anything whose GPU virtual address gets baked into an indirect
	/// command record: a dangling reference is otherwise reported as '&lt;unnamed&gt;' and identifies nothing.
	/// </summary>
	public string? Name { get; }
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
	public ShaderBytecodeSet(
		ReadOnlyMemory<byte>? vertex = null,
		ReadOnlyMemory<byte>? pixel = null,
		ReadOnlyMemory<byte>? compute = null,
		ComputeThreadGroupSize? computeThreadGroupSize = null)
	{
		Vertex = vertex;
		Pixel = pixel;
		Compute = compute;
		ComputeThreadGroupSize = computeThreadGroupSize;
	}

	public ReadOnlyMemory<byte>? Vertex { get; }

	public ReadOnlyMemory<byte>? Pixel { get; }

	public ReadOnlyMemory<byte>? Compute { get; }

	public ComputeThreadGroupSize? ComputeThreadGroupSize { get; }
}
