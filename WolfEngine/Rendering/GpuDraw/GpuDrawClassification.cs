#nullable enable

using System;
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

	public static bool SupportsMeshGeometry(GpuDrawKind drawKind) => drawKind == GpuDrawKind.Mesh;
	public static bool SupportsMeshMaterialInterpretation(GpuDrawKind drawKind) => drawKind == GpuDrawKind.Mesh;
}
