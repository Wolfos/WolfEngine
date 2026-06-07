using System;
using System.Numerics;
using WolfEngine.Mathematics;

namespace WolfEngine.Rendering.Passes;

public static class DdgiUtilities
{
	public const int IrradianceTileInteriorSize = 8;
	public const int VisibilityTileInteriorSize = 16;
	public const int TileBorderSize = 1;
	public const int ShCoefficientCount = 4;
	private const float ShBasisL0 = 0.28209479177f;
	private const float ShBasisL1 = 0.48860251190f;

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

	public static Int2 GetShCoefficientTextureSize(DdgiGridShape shape)
	{
		return new Int2(shape.AtlasColumns, shape.AtlasRows);
	}

	public static DdgiL1Sh ProjectRadiance(Vector3 direction, Vector3 radiance, float solidAngle)
	{
		direction = direction == Vector3.Zero ? Vector3.UnitZ : Vector3.Normalize(direction);
		return new DdgiL1Sh(
			radiance * (ShBasisL0 * solidAngle),
			radiance * (ShBasisL1 * direction.Y * solidAngle),
			radiance * (ShBasisL1 * direction.Z * solidAngle),
			radiance * (ShBasisL1 * direction.X * solidAngle));
	}

	public static Vector3 EvaluateDiffuse(in DdgiL1Sh sh, Vector3 normal)
	{
		normal = normal == Vector3.Zero ? Vector3.UnitZ : Vector3.Normalize(normal);
		var irradiance = sh.L0 * ShBasisL0;
		irradiance += (sh.Ly * normal.Y + sh.Lz * normal.Z + sh.Lx * normal.X) *
		              (2.0f / 3.0f * ShBasisL1);
		return Vector3.Max(irradiance, Vector3.Zero);
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

public readonly record struct DdgiL1Sh(
	Vector3 L0,
	Vector3 Ly,
	Vector3 Lz,
	Vector3 Lx)
{
	public static DdgiL1Sh operator +(DdgiL1Sh left, DdgiL1Sh right)
	{
		return new DdgiL1Sh(
			left.L0 + right.L0,
			left.Ly + right.Ly,
			left.Lz + right.Lz,
			left.Lx + right.Lx);
	}
}
