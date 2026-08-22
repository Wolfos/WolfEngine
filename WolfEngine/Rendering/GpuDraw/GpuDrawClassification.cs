using WolfEngine.Rendering.Passes;

namespace WolfEngine.Rendering;

public static class GpuDrawClassification
{
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

	public static bool TryResolveExecutionLane(GpuDrawKind drawKind, Material material,
		out GpuDrawExecutionLaneDefinition laneDefinition)
	{
		ArgumentNullException.ThrowIfNull(material);

		if (TryResolveBucketId(drawKind, material, out var bucketId) == false)
		{
			laneDefinition = default;
			return false;
		}

		return GpuDrawExecutionLanes.TryGetDefinition(drawKind, bucketId, out laneDefinition);
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
