using WolfEngine.Mathematics;

namespace WolfEngine.Rendering.Passes;

/// <summary>
/// Dispatch shape and constants for a single-pass downsample.
/// </summary>
/// <remarks>Reproduces <c>ffxSpdSetup</c> from ffx-fsr3/upstream/ffx_spd.h.</remarks>
public readonly struct Fsr3SpdSetup
{
	/// <summary>Thread groups to dispatch, one per 64x64 source tile.</summary>
	public required Int2 DispatchThreadGroupCount { get; init; }

	/// <summary>First tile to downsample, for a sub-rectangle. Zero when covering the whole image.</summary>
	public required Int2 WorkGroupOffset { get; init; }

	/// <summary>
	/// Total thread groups for this slice. The shader counts up to this to elect the last
	/// group still running, so it must match the dispatch exactly.
	/// </summary>
	public required uint NumWorkGroups { get; init; }

	/// <summary>Mip levels to produce, capped at 12 as upstream does.</summary>
	public required uint MipCount { get; init; }

	/// <summary>
	/// Builds the setup for downsampling the whole of <paramref name="size"/>.
	/// </summary>
	public static Fsr3SpdSetup Create(Int2 size)
	{
		var width = Math.Max(size.X, 1);
		var height = Math.Max(size.Y, 1);

		// Tiles are 64x64; a partial tile at the edge still needs a group.
		var endIndexX = (uint)((width - 1) / 64);
		var endIndexY = (uint)((height - 1) / 64);
		var groupCountX = (int)(endIndexX + 1);
		var groupCountY = (int)(endIndexY + 1);

		var resolution = Math.Max(width, height);
		var mipCount = (uint)Math.Min(Math.Floor(Math.Log2(resolution)), 12.0);

		return new Fsr3SpdSetup
		{
			DispatchThreadGroupCount = new Int2(groupCountX, groupCountY),
			WorkGroupOffset = new Int2(0, 0),
			NumWorkGroups = (uint)(groupCountX * groupCountY),
			MipCount = mipCount
		};
	}
}
