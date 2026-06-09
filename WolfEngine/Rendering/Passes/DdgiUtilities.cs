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
	public const int MaxRaySamplesPerProbe = VisibilityTileInteriorSize * VisibilityTileInteriorSize;
	public const int IrradianceEstimatorDirectionCount = IrradianceTileInteriorSize * IrradianceTileInteriorSize;
	public const int IrradianceEstimatorStride = 16;
	private const float ShBasisL0 = 0.28209479177f;
	private const float ShBasisL1 = 0.48860251190f;
	private const float ShDirectionalLimit = 0.95f;
	private const float VisibilityVarianceFloor = 0.0004f;
	private const float MaxIrradianceMeanBlend = 0.02f;
	private const float ProbeRelocationBlend = 0.01f;
	private const float RecursiveBounceEnergy = 0.95f;

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

	public static ulong GetIrradianceEstimatorBufferSize(DdgiGridShape shape)
	{
		return checked((ulong)shape.ProbeCount * IrradianceEstimatorDirectionCount * IrradianceEstimatorStride);
	}

	public static int GetRaySampleCount(int requestedRayCount, int tileInteriorSize)
	{
		var tileCapacity = checked(tileInteriorSize * tileInteriorSize);
		return Math.Clamp(requestedRayCount, 1, tileCapacity);
	}

	public static uint PackRgbe(Vector3 rgb)
	{
		const float maxValue = 65408.0f;
		const float minValue = 1.0f / 65536.0f;
		rgb = Vector3.Clamp(rgb, Vector3.Zero, new Vector3(maxValue));
		var maxChannel = MathF.Max(minValue, MathF.Max(rgb.X, MathF.Max(rgb.Y, rgb.Z)));
		var exponent = Math.Clamp((int)MathF.Floor(MathF.Log2(maxChannel)) + 1, -15, 16);
		var scale = MathF.Pow(2.0f, 9 - exponent);
		var packedR = (uint)Math.Clamp((int)MathF.Round(rgb.X * scale), 0, 511);
		var packedG = (uint)Math.Clamp((int)MathF.Round(rgb.Y * scale), 0, 511);
		var packedB = (uint)Math.Clamp((int)MathF.Round(rgb.Z * scale), 0, 511);
		var packedExponent = (uint)(exponent + 15);
		return (packedExponent << 27) | (packedB << 18) | (packedG << 9) | packedR;
	}

	public static Vector3 UnpackRgbe(uint packed)
	{
		var scale = MathF.Pow(2.0f, (int)(packed >> 27) - 24);
		return new Vector3(
			packed & 0x1ffu,
			(packed >> 9) & 0x1ffu,
			(packed >> 18) & 0x1ffu) * scale;
	}

	public static uint PackHalf2(Vector2 value)
	{
		return BitConverter.HalfToUInt16Bits((Half)value.X) |
		       ((uint)BitConverter.HalfToUInt16Bits((Half)value.Y) << 16);
	}

	public static Vector2 UnpackHalf2(uint packed)
	{
		return new Vector2(
			(float)BitConverter.UInt16BitsToHalf((ushort)packed),
			(float)BitConverter.UInt16BitsToHalf((ushort)(packed >> 16)));
	}

	public static DdgiVarianceData UpdateVarianceEstimator(
		Vector3 sampleValue,
		DdgiVarianceData data,
		float shortWindowBlend)
	{
		shortWindowBlend = Math.Clamp(shortWindowBlend, 1.0f / 256.0f, 1.0f);
		var deviation = Vector3.SquareRoot(Vector3.Max(new Vector3(1e-5f), data.Variance));
		var highThreshold = new Vector3(0.1f) + data.ShortMean + deviation * 8.0f;
		sampleValue = Vector3.Min(sampleValue, highThreshold);

		var delta = sampleValue - data.ShortMean;
		data.ShortMean = Vector3.Lerp(data.ShortMean, sampleValue, shortWindowBlend);
		var delta2 = sampleValue - data.ShortMean;
		data.Variance = Vector3.Lerp(
			data.Variance,
			Vector3.Max(Vector3.Zero, delta * delta2),
			shortWindowBlend * 0.5f);
		deviation = Vector3.SquareRoot(Vector3.Max(new Vector3(1e-5f), data.Variance));

		var relativeDifference = Luminance(Vector3.Abs(data.Mean - data.ShortMean) / deviation);
		data.Inconsistency += (relativeDifference - data.Inconsistency) * 0.08f;
		var varianceBlendReduction = Math.Clamp(
			Luminance(0.5f * data.ShortMean / deviation),
			1.0f / 32.0f,
			1.0f);
		var catchUpInput = relativeDifference * MathF.Max(0.02f, data.Inconsistency - 0.2f);
		var smoothCatchUp = Math.Clamp(catchUpInput, 0.0f, 1.0f);
		smoothCatchUp = smoothCatchUp * smoothCatchUp * (3.0f - 2.0f * smoothCatchUp);
		var catchUpBlend = Math.Clamp(smoothCatchUp, 1.0f / 256.0f, 1.0f) * data.VarianceBlendReduction;
		data.VarianceBlendReduction += (varianceBlendReduction - data.VarianceBlendReduction) * 0.1f;
		var meanBlend = Math.Min(Math.Clamp(catchUpBlend, 0.0f, 1.0f), MaxIrradianceMeanBlend);
		data.Mean = Vector3.Lerp(data.Mean, sampleValue, meanBlend);
		return data;
	}

	private static float Luminance(Vector3 value)
	{
		return Vector3.Dot(value, new Vector3(0.299f, 0.587f, 0.114f));
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
		var dc = Vector3.Max(sh.L0 * ShBasisL0, Vector3.Zero);
		var directionalScale = 2.0f / 3.0f * ShBasisL1;
		var directionalX = sh.Lx * directionalScale;
		var directionalY = sh.Ly * directionalScale;
		var directionalZ = sh.Lz * directionalScale;
		var directionalAmplitude = Vector3.SquareRoot(
			directionalX * directionalX +
			directionalY * directionalY +
			directionalZ * directionalZ);
		var channelScale = new Vector3(
			GetShDirectionalChannelScale(dc.X, directionalAmplitude.X),
			GetShDirectionalChannelScale(dc.Y, directionalAmplitude.Y),
			GetShDirectionalChannelScale(dc.Z, directionalAmplitude.Z));
		var lobeScale = MathF.Min(1.0f, MathF.Min(channelScale.X, MathF.Min(channelScale.Y, channelScale.Z)));
		var irradiance = dc + lobeScale *
			(directionalY * normal.Y + directionalZ * normal.Z + directionalX * normal.X);
		return Vector3.Max(irradiance, Vector3.Zero);
	}

	private static float GetShDirectionalChannelScale(float dc, float directionalAmplitude)
	{
		return directionalAmplitude > 1e-5f
			? dc * ShDirectionalLimit / directionalAmplitude
			: 1.0f;
	}

	public static float GetVisibilityDirectionalWeight(float directionDot)
	{
		return MathF.Pow(Math.Clamp(directionDot, 0.0f, 1.0f), 64.0f);
	}

	public static float EvaluateVisibility(float meanDistance, float meanDistanceSquared, float distance)
	{
		if (distance <= meanDistance)
		{
			return 1.0f;
		}

		var variance = MathF.Max(
			meanDistanceSquared - meanDistance * meanDistance,
			VisibilityVarianceFloor);
		var distanceDelta = distance - meanDistance;
		var chebyshevVisibility = variance / (variance + distanceDelta * distanceDelta);
		return chebyshevVisibility * chebyshevVisibility * chebyshevVisibility;
	}

	public static Vector3 ComputeProbeRelocationTarget(
		ReadOnlySpan<DdgiRelocationHit> hits,
		float keepDistance,
		float maxRelocationDistance)
	{
		keepDistance = Math.Max(keepDistance, 0.0f);
		var targetOffset = Vector3.Zero;
		foreach (var hit in hits)
		{
			if (hit.Valid == false || hit.Distance >= keepDistance)
			{
				continue;
			}

			var direction = hit.Direction == Vector3.Zero
				? Vector3.UnitZ
				: Vector3.Normalize(hit.Direction);
			targetOffset -= direction * (keepDistance - Math.Max(hit.Distance, 0.0f));
		}

		return ClampProbeRelocationOffset(targetOffset, maxRelocationDistance);
	}

	public static Vector3 UpdateProbeRelocation(
		Vector3 previousOffset,
		Vector3 targetOffset,
		float maxRelocationDistance,
		bool active)
	{
		previousOffset = ClampProbeRelocationOffset(previousOffset, maxRelocationDistance);
		if (active == false)
		{
			return previousOffset;
		}

		targetOffset = ClampProbeRelocationOffset(targetOffset, maxRelocationDistance);
		return ClampProbeRelocationOffset(
			Vector3.Lerp(previousOffset, targetOffset, ProbeRelocationBlend),
			maxRelocationDistance);
	}

	private static Vector3 ClampProbeRelocationOffset(Vector3 offset, float maxRelocationDistance)
	{
		var maxOffset = Math.Max(maxRelocationDistance, 0.0f);
		return Vector3.Clamp(offset, new Vector3(-maxOffset), new Vector3(maxOffset));
	}

	public static Vector3 ShadeDiffuseHit(
		Vector3 albedo,
		Vector3 directLightRadiance,
		float normalDotLight,
		float visibility,
		Vector3 previousDdgi,
		Vector3 emissive,
		bool historyValid)
	{
		albedo = Vector3.Max(albedo, Vector3.Zero);
		var direct = directLightRadiance *
		             Math.Clamp(normalDotLight, 0.0f, 1.0f) *
		             Math.Clamp(visibility, 0.0f, 1.0f) /
		             MathF.PI;
		var recursive = historyValid
			? Vector3.Max(previousDdgi, Vector3.Zero) * RecursiveBounceEnergy
			: Vector3.Zero;
		return albedo * (direct + recursive) + emissive;
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

public record struct DdgiVarianceData(
	Vector3 Mean,
	Vector3 ShortMean,
	float VarianceBlendReduction,
	Vector3 Variance,
	float Inconsistency);

public readonly record struct DdgiRelocationHit(
	Vector3 Direction,
	float Distance,
	bool Valid = true);
