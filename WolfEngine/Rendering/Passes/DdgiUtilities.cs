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

	public static int GetProbeUpdateFrames(DiffuseGlobalIlluminationConfig config)
	{
		return Math.Max(config.ProbeUpdateFrames, 1);
	}

	public static int GetProbeUpdateFrameIndex(uint frameIndex, int probeUpdateFrames)
	{
		var clampedFrames = Math.Max(probeUpdateFrames, 1);
		return (int)(frameIndex % (uint)clampedFrames);
	}

	public static bool IsProbeActive(int probeIndex, int probeUpdateFrames, int probeUpdateFrameIndex, bool forceFullUpdate)
	{
		var clampedFrames = Math.Max(probeUpdateFrames, 1);
		if (forceFullUpdate || clampedFrames == 1)
		{
			return true;
		}

		var clampedFrameIndex = Math.Clamp(probeUpdateFrameIndex, 0, clampedFrames - 1);
		return probeIndex >= 0 && probeIndex % clampedFrames == clampedFrameIndex;
	}

	public static int GetActiveProbeCount(int totalProbeCount, int probeUpdateFrames, int probeUpdateFrameIndex, bool forceFullUpdate)
	{
		if (totalProbeCount <= 0)
		{
			return 0;
		}

		var clampedFrames = Math.Max(probeUpdateFrames, 1);
		if (forceFullUpdate || clampedFrames == 1)
		{
			return totalProbeCount;
		}

		var clampedFrameIndex = Math.Clamp(probeUpdateFrameIndex, 0, clampedFrames - 1);
		var fullCycles = totalProbeCount / clampedFrames;
		var remainder = totalProbeCount % clampedFrames;
		return fullCycles + (clampedFrameIndex < remainder ? 1 : 0);
	}
}

public readonly record struct DdgiGridShape(
	int CountX,
	int CountY,
	int CountZ,
	int ProbeCount,
	int AtlasColumns,
	int AtlasRows);
