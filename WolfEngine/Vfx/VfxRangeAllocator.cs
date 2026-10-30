namespace WolfEngine.Vfx;

/// <summary>
/// First-fit allocator for contiguous runs of particle slots. Each playing sprite emitter owns one run, so the CPU
/// decides where every particle goes and never needs to read anything back from the GPU.
/// </summary>
internal sealed class VfxRangeAllocator
{
	private readonly List<(int Start, int Length)> _free = new();

	public VfxRangeAllocator(int capacity)
	{
		Capacity = capacity;
		_free.Add((0, capacity));
	}

	public int Capacity { get; }

	public int FreeCount
	{
		get
		{
			var total = 0;
			for (var i = 0; i < _free.Count; i++)
			{
				total += _free[i].Length;
			}

			return total;
		}
	}

	public bool TryAllocate(int length, out int start)
	{
		start = -1;
		if (length <= 0) return false;

		for (var i = 0; i < _free.Count; i++)
		{
			var block = _free[i];
			if (block.Length < length)
			{
				continue;
			}

			start = block.Start;
			if (block.Length == length)
			{
				_free.RemoveAt(i);
			}
			else
			{
				_free[i] = (block.Start + length, block.Length - length);
			}

			return true;
		}

		return false;
	}

	public void Free(int start, int length)
	{
		if (length <= 0) return;

		var index = 0;
		while (index < _free.Count && _free[index].Start < start)
		{
			index++;
		}

		_free.Insert(index, (start, length));

		// Merge with the following block, then the preceding one.
		if (index + 1 < _free.Count && _free[index].Start + _free[index].Length == _free[index + 1].Start)
		{
			_free[index] = (_free[index].Start, _free[index].Length + _free[index + 1].Length);
			_free.RemoveAt(index + 1);
		}

		if (index > 0 && _free[index - 1].Start + _free[index - 1].Length == _free[index].Start)
		{
			_free[index - 1] = (_free[index - 1].Start, _free[index - 1].Length + _free[index].Length);
			_free.RemoveAt(index);
		}
	}

	public void Reset()
	{
		_free.Clear();
		_free.Add((0, Capacity));
	}
}
