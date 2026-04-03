#nullable enable

using System;
using System.Collections.Generic;

namespace WolfEngine.Rendering;

public readonly struct GpuDrawHandle : IEquatable<GpuDrawHandle>
{
	private const uint IndexMask = 0xFFFFu;
	private const int GenerationShift = 16;

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
			throw new ArgumentOutOfRangeException(nameof(index), index, "GpuDraw handle index must be <= 65535.");
		}

		if (generation == 0)
		{
			generation = 1;
		}

		return new GpuDrawHandle(((uint)generation << GenerationShift) | (uint)index);
	}

	public bool Equals(GpuDrawHandle other) => Value == other.Value;

	public override bool Equals(object? obj) => obj is GpuDrawHandle other && Equals(other);

	public override int GetHashCode() => (int)Value;

	public override string ToString() => $"0x{Value:X8}";
}

internal sealed class GpuDrawHandlePool
{
	private readonly ushort[] _generations;
	private readonly Stack<ushort> _free = new();
	private ushort _nextIndex = 1;

	public GpuDrawHandlePool(int capacity)
	{
		if (capacity <= 0 || capacity > ushort.MaxValue)
		{
			throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "GpuDraw handle pool capacity must be in 1..65535.");
		}

		_generations = new ushort[capacity + 1];
		_generations[0] = 1;
	}

	public int Capacity => _generations.Length - 1;

	public GpuDrawHandle FallbackHandle => GpuDrawHandle.Create(0, _generations[0]);

	public GpuDrawHandle Acquire()
	{
		ushort index;
		if (_free.Count > 0)
		{
			index = _free.Pop();
		}
		else
		{
			if (_nextIndex == 0 || _nextIndex > Capacity)
			{
				throw new InvalidOperationException($"GpuDraw handle pool exhausted (capacity={Capacity}).");
			}

			index = _nextIndex++;
		}

		var generation = _generations[index];
		if (generation == 0)
		{
			generation = 1;
			_generations[index] = generation;
		}

		return GpuDrawHandle.Create(index, generation);
	}

	public bool IsCurrent(in GpuDrawHandle handle)
	{
		var index = handle.Index;
		return (uint)index < (uint)_generations.Length && _generations[index] == handle.Generation;
	}

	public void Release(in GpuDrawHandle handle)
	{
		if (handle.IsValid == false)
		{
			return;
		}

		var index = handle.Index;
		if (index <= 0 || index >= _generations.Length)
		{
			return;
		}

		if (_generations[index] != handle.Generation)
		{
			return;
		}

		var nextGeneration = unchecked((ushort)(handle.Generation + 1));
		if (nextGeneration == 0)
		{
			nextGeneration = 1;
		}

		_generations[index] = nextGeneration;
		_free.Push((ushort)index);
	}

	public void Reset()
	{
		_free.Clear();
		_nextIndex = 1;
		for (var i = 0; i < _generations.Length; i++)
		{
			var nextGeneration = unchecked((ushort)(_generations[i] + 1));
			if (nextGeneration == 0)
			{
				nextGeneration = 1;
			}

			_generations[i] = nextGeneration;
		}
	}

	public void WriteGenerations(Span<uint> destination)
	{
		if (destination.Length < _generations.Length)
		{
			throw new ArgumentException("Destination span is too small.", nameof(destination));
		}

		for (var i = 0; i < _generations.Length; i++)
		{
			destination[i] = _generations[i];
		}
	}

	public void WriteGenerations(List<uint> destination)
	{
		destination.Clear();
		for (var i = 0; i < _generations.Length; i++)
		{
			destination.Add(_generations[i]);
		}
	}
}
