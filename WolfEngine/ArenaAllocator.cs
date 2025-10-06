using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WolfEngine;

internal unsafe class ArenaAllocator
{
    private const int DefaultBlockSize = 4096;
    private readonly object _lock = new();
    private readonly List<Block> _blocks = new();
    private int _currentBlockIndex = -1;

    private ArenaAllocator()
    {
    }

    ~ArenaAllocator()
    {
        DisposeUnmanaged();
    }

    public static ArenaAllocator RenderCommands { get; } = new();

    public nint Store<T>(T payload) where T : struct
    {
        var size = Unsafe.SizeOf<T>();
        var alignment = Math.Min(size, IntPtr.Size);
        if (alignment == 0)
        {
            alignment = IntPtr.Size;
        }
        var pointer = Allocate(size, alignment);
        Unsafe.Write((void*)pointer, payload);
        return pointer;
    }

    public T Read<T>(nint pointer) where T : struct
    {
        return Unsafe.Read<T>((void*)pointer);
    }

    public void Reset()
    {
        lock (_lock)
        {
            for (var i = 0; i < _blocks.Count; i++)
            {
                var block = _blocks[i];
                block.Offset = 0;
                _blocks[i] = block;
            }

            _currentBlockIndex = _blocks.Count == 0 ? -1 : 0;
        }
    }

    private nint Allocate(int size, int alignment)
    {
        lock (_lock)
        {
            if (_currentBlockIndex == -1)
            {
                AddBlock(Math.Max(DefaultBlockSize, size));
            }

            if (!TryAllocate(size, alignment, out var pointer))
            {
                AddBlock(Math.Max(DefaultBlockSize, size));
                if (!TryAllocate(size, alignment, out pointer))
                {
                    throw new InvalidOperationException("Failed to allocate render command payload.");
                }
            }

            return pointer;
        }
    }

    private bool TryAllocate(int size, int alignment, out nint pointer)
    {
        pointer = 0;
        if (_currentBlockIndex == -1)
        {
            return false;
        }

        var block = _blocks[_currentBlockIndex];
        var alignedOffset = Align(block.Offset, alignment);
        if (alignedOffset + size > block.Size)
        {
            return false;
        }

        pointer = block.Pointer + alignedOffset;
        block.Offset = alignedOffset + size;
        _blocks[_currentBlockIndex] = block;
        return true;
    }

    private void AddBlock(int size)
    {
        var memory = NativeMemory.Alloc((nuint)size);
        _blocks.Add(new Block((nint)memory, size, 0));
        _currentBlockIndex = _blocks.Count - 1;
    }

    private void DisposeUnmanaged()
    {
        foreach (var block in _blocks)
        {
            NativeMemory.Free((void*)block.Pointer);
        }
        _blocks.Clear();
        _currentBlockIndex = -1;
    }

    private static int Align(int value, int alignment)
    {
        var mask = alignment - 1;
        return (value + mask) & ~mask;
    }

    private struct Block
    {
        public Block(nint pointer, int size, int offset)
        {
            Pointer = pointer;
            Size = size;
            Offset = offset;
        }

        public nint Pointer;
        public int Size;
        public int Offset;
    }
}