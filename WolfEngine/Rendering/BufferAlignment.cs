#nullable enable

using System;

namespace WolfEngine.Rendering;

/// <summary>Alignment helpers for byte offsets into GPU buffers.</summary>
internal static class BufferAlignment
{
	/// <summary>
	/// Rounds an offset up to the next multiple of <paramref name="alignment"/>.
	/// Unlike bit-mask alignment, this also supports non-power-of-two values such as WolfEngine's
	/// 48-byte packed vertex stride.
	/// </summary>
	internal static ulong AlignUp(ulong value, ulong alignment)
	{
		if (alignment == 0)
		{
			return value;
		}

		return checked(((value + alignment - 1) / alignment) * alignment);
	}
}
