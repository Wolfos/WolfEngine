using System;
using WolfEngine.Mathematics;

namespace WolfEngine.Rendering.Passes;

public static class DdgiUtilities
{
	public const int IrradianceTileInteriorSize = 8;
	public const int VisibilityTileInteriorSize = 16;
	public const int TileBorderSize = 1;

	public static bool IsRayTracedDdgiEnabled(RenderConfig config)
	{
		return config.DiffuseGlobalIllumination.Enabled &&
		       config.DiffuseGlobalIllumination.Mode == DiffuseGlobalIlluminationMode.RayTracedDdgi;
	}

	public static DdgiGridShape GetGridShape(DiffuseGlobalIlluminationConfig config)
	{
		var countX = Math.Max(1, config.ProbeCounts.X);
		var countY = Math.Max(1, config.ProbeCounts.Y);
		var countZ = Math.Max(1, config.ProbeCounts.Z);
		var probeCount = checked(countX * countY * countZ);
		var atlasColumns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(probeCount)));
		var atlasRows = Math.Max(1, (probeCount + atlasColumns - 1) / atlasColumns);
		return new DdgiGridShape(countX, countY, countZ, probeCount, atlasColumns, atlasRows);
	}

	public static Int2 GetAtlasSize(DdgiGridShape shape, int tileInteriorSize)
	{
		var tileSize = tileInteriorSize + TileBorderSize * 2;
		return new Int2(shape.AtlasColumns * tileSize, shape.AtlasRows * tileSize);
	}

	public static float GetMaxRayDistance(DiffuseGlobalIlluminationConfig config)
	{
		if (config.MaxRayDistance > 0.0f)
		{
			return config.MaxRayDistance;
		}

		return Math.Max(config.ProbeSpacing, 0.001f) * 3.0f;
	}
}

public readonly record struct DdgiGridShape(
	int CountX,
	int CountY,
	int CountZ,
	int ProbeCount,
	int AtlasColumns,
	int AtlasRows);
