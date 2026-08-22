#nullable enable

using System.Collections.Generic;

namespace WolfEngine.Rendering;

/// <summary>
/// Recycles per-instance vertex ranges inside the packed vertex buffer.
/// </summary>
/// <remarks>
/// Static level geometry is uploaded once and effectively never freed, so the packed buffer's bump
/// pointer is fine for it. Skinned characters are different: every spawned instance takes its own
/// range and gives it back on despawn, so without reuse a spawn-heavy scene would exhaust the
/// buffer over time.
///
/// Freed ranges are keyed by exact size rather than coalesced. An instance range's size is fixed by
/// its source mesh, so respawning the same character always finds an exact match, which is the case
/// that actually occurs. Ranges of a size that is never requested again stay parked rather than
/// being merged back — acceptable for a bounded set of character meshes, and the place to revisit
/// if instance sizes ever become open-ended.
/// </remarks>
internal sealed class SkinnedInstanceVertexAllocator
{
	private readonly Dictionary<ulong, Stack<ulong>> _freeOffsetsBySize = new();

	internal int FreeRangeCount { get; private set; }

	internal bool TryReuse(ulong sizeBytes, out ulong offsetBytes)
	{
		offsetBytes = 0;
		if (sizeBytes == 0 || _freeOffsetsBySize.TryGetValue(sizeBytes, out var offsets) == false || offsets.Count == 0)
		{
			return false;
		}

		offsetBytes = offsets.Pop();
		FreeRangeCount--;
		return true;
	}

	internal void Release(ulong offsetBytes, ulong sizeBytes)
	{
		if (sizeBytes == 0)
		{
			return;
		}

		if (_freeOffsetsBySize.TryGetValue(sizeBytes, out var offsets) == false)
		{
			offsets = new Stack<ulong>();
			_freeOffsetsBySize[sizeBytes] = offsets;
		}

		offsets.Push(offsetBytes);
		FreeRangeCount++;
	}

	/// <summary>
	/// Drops every recycled range. Call when the packed buffer itself is torn down, since the
	/// offsets no longer refer to anything.
	/// </summary>
	internal void Clear()
	{
		_freeOffsetsBySize.Clear();
		FreeRangeCount = 0;
	}
}
