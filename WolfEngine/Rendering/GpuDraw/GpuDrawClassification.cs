using System.Numerics;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering;

public static class GpuDrawClassification
{
	/// <summary>Reflection parity of the full world transform, including inherited scales.</summary>
	public static bool ReversesWinding(in Matrix4x4 world) => world.GetDeterminant() < 0.0f;

	public static bool TryResolveBucketId(GpuDrawKind drawKind, Material material, out GpuDrawBucketId bucketId)
	{
		ArgumentNullException.ThrowIfNull(material);

		switch (drawKind)
		{
			case GpuDrawKind.Mesh:
				bucketId = GBufferDrawBuckets.ResolveBucketId(material.AlphaMode);
				return true;
			case GpuDrawKind.DebugPrimitive:
				bucketId = material.AlphaMode == AlphaMode.AlphaBlend
					? GpuDrawBucketId.AlphaBlend
					: GpuDrawBucketId.Opaque;
				return true;
			case GpuDrawKind.Terrain:
				bucketId = GpuDrawBucketId.Opaque;
				return true;
			default:
				bucketId = GpuDrawBucketId.Opaque;
				return false;
		}
	}

	public static GpuDrawBucketId ResolveBucketId(GpuDrawKind drawKind, Material material)
	{
		if (TryResolveBucketId(drawKind, material, out var bucketId))
		{
			return bucketId;
		}

		throw new NotSupportedException($"Shared draw kind '{drawKind}' does not define bucket participation yet.");
	}

	/// <summary>
	/// Only mesh lanes have double-sided variants: terrain is a heightfield and debug primitives are
	/// closed hulls, so neither has a back face worth rasterising.
	/// </summary>
	public static GpuDrawSidedness ResolveSidedness(GpuDrawKind drawKind, Material material)
	{
		ArgumentNullException.ThrowIfNull(material);

		return drawKind == GpuDrawKind.Mesh && material.DoubleSided
			? GpuDrawSidedness.DoubleSided
			: GpuDrawSidedness.SingleSided;
	}

	public static bool TryResolveExecutionLane(GpuDrawKind drawKind, Material material,
		out GpuDrawExecutionLaneDefinition laneDefinition)
	{
		ArgumentNullException.ThrowIfNull(material);

		if (TryResolveBucketId(drawKind, material, out var bucketId) == false)
		{
			laneDefinition = default;
			return false;
		}

		return GpuDrawExecutionLanes.TryGetDefinition(
			drawKind,
			bucketId,
			ResolveSidedness(drawKind, material),
			out laneDefinition);
	}

	public static GpuDrawExecutionLaneDefinition ResolveExecutionLane(GpuDrawKind drawKind, Material material)
	{
		if (TryResolveExecutionLane(drawKind, material, out var laneDefinition))
		{
			return laneDefinition;
		}

		throw new NotSupportedException(
			$"Shared draw kind '{drawKind}' does not define an execution lane for material '{material.ShaderPath}'.");
	}

	public static bool SupportsMeshBackedGeometry(GpuDrawKind drawKind) =>
		drawKind is GpuDrawKind.Mesh or GpuDrawKind.DebugPrimitive or GpuDrawKind.Terrain;

	public static bool SupportsTexturedPbrMaterialInterpretation(GpuDrawKind drawKind) => drawKind == GpuDrawKind.Mesh;

	public static bool SupportsUnlitTintMaterialInterpretation(GpuDrawKind drawKind) =>
		drawKind == GpuDrawKind.DebugPrimitive;

	public static bool SupportsTerrainMaterialInterpretation(GpuDrawKind drawKind) =>
		drawKind == GpuDrawKind.Terrain;
}
