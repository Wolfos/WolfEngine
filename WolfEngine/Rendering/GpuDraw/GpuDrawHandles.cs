namespace WolfEngine.Rendering;

/// <summary>
/// A slot in one of the shared GPU draw tables: a 20-bit index and a 12-bit generation that changes whenever the
/// slot is released. Mirrored by <c>WOLF_HANDLE_*</c> in draw_data.slang; change them together.
/// </summary>
public readonly struct GpuDrawHandle : IEquatable<GpuDrawHandle>
{
	public const int IndexBits = 20;
	public const uint IndexMask = (1u << IndexBits) - 1u;
	public const int GenerationShift = IndexBits;
	public const uint GenerationMask = (1u << (32 - IndexBits)) - 1u;

	/// <summary>Highest index a handle can name. Index zero is reserved for each table's fallback entry.</summary>
	public const int MaxIndex = (int)IndexMask;

	public GpuDrawHandle(uint value)
	{
		Value = value;
	}

	public static GpuDrawHandle Invalid => new(0);

	public uint Value { get; }

	public int Index => (int)(Value & IndexMask);

	public ushort Generation => (ushort)(Value >> GenerationShift);

	public bool IsValid => Value != 0;

	public static GpuDrawHandle Create(int index, ushort generation)
	{
		if ((uint)index > IndexMask)
		{
			throw new ArgumentOutOfRangeException(nameof(index), index, $"GpuDraw handle index must be <= {MaxIndex}.");
		}

		if (generation > GenerationMask)
		{
			throw new ArgumentOutOfRangeException(
				nameof(generation),
				generation,
				$"GpuDraw handle generation must be <= {GenerationMask}.");
		}

		if (generation == 0)
		{
			generation = 1;
		}

		return new GpuDrawHandle(((uint)generation << GenerationShift) | (uint)index);
	}

	/// <summary>The generation after <paramref name="generation"/>, wrapping within the generation bits and skipping zero.</summary>
	public static ushort NextGeneration(ushort generation)
	{
		var next = (ushort)((generation + 1u) & GenerationMask);
		if (next == 0)
		{
			next = 1;
		}

		return next;
	}

	public bool Equals(GpuDrawHandle other) => Value == other.Value;

	public override bool Equals(object? obj) => obj is GpuDrawHandle other && Equals(other);

	public override int GetHashCode() => (int)Value;

	public override string ToString() => $"0x{Value:X8}";
}

/// <summary>
/// Hands out the slots of one table. Slots are allocated from the bottom and reused before new ones are taken, so
/// <see cref="SlotCount"/> stays at the peak number in use and sizes the GPU table.
/// </summary>
internal sealed class GpuDrawHandlePool
{
	// One generation per slot handed out so far, including the reserved slot zero.
	private readonly List<ushort> _generations = new();
	private readonly Stack<int> _free = new();

	/// <param name="capacity">Most slots the pool hands out, not counting the reserved slot zero.</param>
	public GpuDrawHandlePool(int capacity)
	{
		if (capacity <= 0 || capacity > GpuDrawHandle.MaxIndex)
		{
			throw new ArgumentOutOfRangeException(
				nameof(capacity),
				capacity,
				$"GpuDraw handle pool capacity must be in 1..{GpuDrawHandle.MaxIndex}.");
		}

		Capacity = capacity;
		_generations.Add(1);
	}

	public int Capacity { get; }

	/// <summary>Slots handed out so far including slot zero, which is the length a GPU table needs.</summary>
	public int SlotCount => _generations.Count;

	public GpuDrawHandle FallbackHandle => GpuDrawHandle.Create(0, _generations[0]);

	public GpuDrawHandle Acquire()
	{
		if (TryAcquire(out var handle) == false)
		{
			throw new InvalidOperationException($"GpuDraw handle pool exhausted (capacity={Capacity}).");
		}

		return handle;
	}

	/// <returns>False, with <paramref name="handle"/> invalid, when every slot is in use.</returns>
	public bool TryAcquire(out GpuDrawHandle handle)
	{
		int index;
		if (_free.Count > 0)
		{
			index = _free.Pop();
		}
		else
		{
			if (_generations.Count > Capacity)
			{
				handle = GpuDrawHandle.Invalid;
				return false;
			}

			index = _generations.Count;
			_generations.Add(1);
		}

		handle = GpuDrawHandle.Create(index, _generations[index]);
		return true;
	}

	public bool IsCurrent(in GpuDrawHandle handle)
	{
		var index = handle.Index;
		return (uint)index < (uint)_generations.Count && _generations[index] == handle.Generation;
	}

	public void Release(in GpuDrawHandle handle)
	{
		if (handle.IsValid == false)
		{
			return;
		}

		var index = handle.Index;
		if (index <= 0 || index >= _generations.Count)
		{
			return;
		}

		if (_generations[index] != handle.Generation)
		{
			return;
		}

		_generations[index] = GpuDrawHandle.NextGeneration(handle.Generation);
		_free.Push(index);
	}

	public void WriteGenerations(List<uint> destination)
	{
		destination.Clear();
		for (var i = 0; i < _generations.Count; i++)
		{
			destination.Add(_generations[i]);
		}
	}
}
