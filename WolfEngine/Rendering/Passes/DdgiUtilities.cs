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
	public const int RelocationRayCount = 16;
	public const int RelocationIterationCount = 1;
	public const float DefaultRecursiveBounceEnergy = 0.5f;
	private const float ShBasisL0 = 0.28209479177f;
	private const float ShBasisL1 = 0.48860251190f;
	private const float ShDirectionalLimit = 0.95f;
	private const float VisibilityVarianceFloor = 0.000001f;
	private const float MaxIrradianceMeanBlend = 0.02f;
	private const float MaxProbeRelocationDistanceFactor = 0.45f;
	private const float RelocationMinimumTolerance = 0.001f;

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

	public static Vector3 GetRuntimeOrigin(
		Vector3 latticeAnchor,
		DdgiGridShape shape,
		float probeSpacing,
		Vector3 cameraPosition)
	{
		probeSpacing = Math.Max(probeSpacing, 0.001f);
		var initialGridCenter = latticeAnchor + new Vector3(
			(shape.CountX - 1) * 0.5f * probeSpacing,
			(shape.CountY - 1) * 0.5f * probeSpacing,
			(shape.CountZ - 1) * 0.5f * probeSpacing);
		var latticeOffset = (cameraPosition - initialGridCenter) / probeSpacing;
		return latticeAnchor + new Vector3(
			MathF.Round(latticeOffset.X, MidpointRounding.AwayFromZero),
			0.0f,
			MathF.Round(latticeOffset.Z, MidpointRounding.AwayFromZero)) * probeSpacing;
	}

	public static Int3 GetScrollDelta(Vector3 previousOrigin, Vector3 currentOrigin, float probeSpacing)
	{
		probeSpacing = Math.Max(probeSpacing, 0.001f);
		var delta = (currentOrigin - previousOrigin) / probeSpacing;
		return new Int3(
			(int)MathF.Round(delta.X, MidpointRounding.AwayFromZero),
			(int)MathF.Round(delta.Y, MidpointRounding.AwayFromZero),
			(int)MathF.Round(delta.Z, MidpointRounding.AwayFromZero));
	}

	public static Int3 AdvanceStorageOffset(Int3 previousOffset, Int3 scrollDelta, DdgiGridShape shape)
	{
		return new Int3(
			PositiveModulo(previousOffset.X + scrollDelta.X, shape.CountX),
			PositiveModulo(previousOffset.Y + scrollDelta.Y, shape.CountY),
			PositiveModulo(previousOffset.Z + scrollDelta.Z, shape.CountZ));
	}

	public static Int3 GetLogicalProbeCoord(int probeIndex, DdgiGridShape shape)
	{
		var x = probeIndex % shape.CountX;
		var yz = probeIndex / shape.CountX;
		return new Int3(x, yz % shape.CountY, yz / shape.CountY);
	}

	public static int GetPhysicalProbeIndex(int logicalProbeIndex, Int3 storageOffset, DdgiGridShape shape)
	{
		var logical = GetLogicalProbeCoord(logicalProbeIndex, shape);
		var physical = new Int3(
			PositiveModulo(logical.X + storageOffset.X, shape.CountX),
			PositiveModulo(logical.Y + storageOffset.Y, shape.CountY),
			PositiveModulo(logical.Z + storageOffset.Z, shape.CountZ));
		return physical.X + physical.Y * shape.CountX + physical.Z * shape.CountX * shape.CountY;
	}

	public static bool IsProbeNewlyExposed(
		int logicalProbeIndex,
		Int3 scrollDelta,
		DdgiGridShape shape,
		bool historyValid = true)
	{
		if (historyValid == false)
		{
			return true;
		}

		var logical = GetLogicalProbeCoord(logicalProbeIndex, shape);
		var previous = new Int3(
			logical.X + scrollDelta.X,
			logical.Y + scrollDelta.Y,
			logical.Z + scrollDelta.Z);
		return previous.X < 0 || previous.X >= shape.CountX ||
		       previous.Y < 0 || previous.Y >= shape.CountY ||
		       previous.Z < 0 || previous.Z >= shape.CountZ;
	}

	public static int GetNewlyExposedProbeCount(Int3 scrollDelta, DdgiGridShape shape, bool historyValid = true)
	{
		var count = 0;
		for (var probeIndex = 0; probeIndex < shape.ProbeCount; probeIndex++)
		{
			if (IsProbeNewlyExposed(probeIndex, scrollDelta, shape, historyValid))
			{
				count++;
			}
		}

		return count;
	}

	private static int PositiveModulo(int value, int modulus)
	{
		var positiveModulus = Math.Max(modulus, 1);
		var remainder = value % positiveModulus;
		return remainder < 0 ? remainder + positiveModulus : remainder;
	}

	public static int GetRaySampleCount(int requestedRayCount, int tileInteriorSize)
	{
		var tileCapacity = checked(tileInteriorSize * tileInteriorSize);
		return Math.Clamp(requestedRayCount, 1, tileCapacity);
	}

	internal static int GetProbeTraceInvocationCount(int requestedRayCount)
	{
		var visibilityRayCount = GetRaySampleCount(requestedRayCount, VisibilityTileInteriorSize);
		var irradianceRayCount = GetRaySampleCount(requestedRayCount, IrradianceTileInteriorSize);
		return visibilityRayCount == irradianceRayCount
			? visibilityRayCount
			: checked(visibilityRayCount + irradianceRayCount);
	}

	internal static bool IsRelocationTraceEnabled(RenderConfig config)
	{
		return IsRayTracedDdgiEnabled(config) &&
		       config.DiffuseGlobalIllumination.ProbeRelocationEnabled;
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

	public static Vector3 EvaluateRadiance(in DdgiL1Sh sh, Vector3 direction)
	{
		direction = direction == Vector3.Zero ? Vector3.UnitZ : Vector3.Normalize(direction);
		var dc = Vector3.Max(sh.L0 * ShBasisL0, Vector3.Zero);
		var directionalX = sh.Lx * ShBasisL1;
		var directionalY = sh.Ly * ShBasisL1;
		var directionalZ = sh.Lz * ShBasisL1;
		var directionalAmplitude = Vector3.SquareRoot(
			directionalX * directionalX +
			directionalY * directionalY +
			directionalZ * directionalZ);
		var channelScale = new Vector3(
			GetShDirectionalChannelScale(dc.X, directionalAmplitude.X),
			GetShDirectionalChannelScale(dc.Y, directionalAmplitude.Y),
			GetShDirectionalChannelScale(dc.Z, directionalAmplitude.Z));
		var lobeScale = MathF.Min(1.0f, MathF.Min(channelScale.X, MathF.Min(channelScale.Y, channelScale.Z)));
		var radiance = dc + lobeScale *
			(directionalY * direction.Y + directionalZ * direction.Z + directionalX * direction.X);
		return Vector3.Max(radiance, Vector3.Zero);
	}

	public static float GetRoughSpecularBlend(float gridInfluence, float roughness, bool hasValidProbeSample)
	{
		if (hasValidProbeSample == false)
		{
			return 0.0f;
		}

		var t = Math.Clamp((roughness - 0.25f) / (0.6f - 0.25f), 0.0f, 1.0f);
		var roughnessBlend = t * t * (3.0f - 2.0f * t);
		return Math.Clamp(gridInfluence, 0.0f, 1.0f) * roughnessBlend;
	}

	private static float GetShDirectionalChannelScale(float dc, float directionalAmplitude)
	{
		return directionalAmplitude > 1e-5f
			? dc * ShDirectionalLimit / directionalAmplitude
			: 1.0f;
	}

	public static float GetVisibilityDirectionalWeight(float directionDot)
	{
		return GetVisibilityDirectionalWeight(directionDot, MaxRaySamplesPerProbe);
	}

	public static float GetVisibilityDirectionalWeight(float directionDot, int rayCount)
	{
		var exponent = Math.Clamp(rayCount / 6.0f - 1.0f, 8.0f, 32.0f);
		return MathF.Pow(Math.Clamp(directionDot, 0.0f, 1.0f), exponent);
	}

	public static float GetOctahedralSolidAngleWeight(Vector2 octUv)
	{
		var unnormalizedDirection = new Vector3(
			octUv.X,
			octUv.Y,
			1.0f - MathF.Abs(octUv.X) - MathF.Abs(octUv.Y));
		var length = unnormalizedDirection.Length();
		return 1.0f / MathF.Max(unnormalizedDirection.LengthSquared() * length, 1e-5f);
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
		float maxRelocationDistance,
		Vector3 previousOffset = default,
		float backfaceThreshold = 0.25f)
	{
		return SolveProbeRelocation(
			hits,
			keepDistance,
			maxRelocationDistance,
			maxRelocationDistance + Math.Max(keepDistance, 0.0f),
			previousOffset: previousOffset,
			backfaceThreshold: backfaceThreshold).Offset;
	}

	public static DdgiRelocationResult SolveProbeRelocation(
		ReadOnlySpan<DdgiRelocationHit> hits,
		float keepDistance,
		float maxRelocationDistance,
		float maxRayDistance,
		Vector3 previousOffset = default,
		float backfaceThreshold = 0.25f)
	{
		keepDistance = Math.Max(keepDistance, 0.0f);
		maxRayDistance = Math.Max(maxRayDistance, 0.001f);
		previousOffset = ClampProbeRelocationOffset(previousOffset, maxRelocationDistance);
		var relocationTolerance = Math.Max(
			RelocationMinimumTolerance,
			Math.Max(Math.Max(maxRelocationDistance, 0.0f), keepDistance) / 1024.0f);

		var backfaceCount = 0;
		var closestBackfaceDistance = maxRayDistance;
		var closestBackfaceDirection = Vector3.Zero;
		var closestFrontfaceDistance = maxRayDistance;
		var closestFrontfaceDirection = Vector3.Zero;
		var farthestFrontfaceDistance = 0.0f;
		var farthestFrontfaceDirection = Vector3.Zero;
		for (var index = 0; index < hits.Length; index++)
		{
			var hit = hits[index];
			var direction = NormalizeRelocationDirection(hit.Direction);
			var distance = hit.Valid
				? Math.Clamp(hit.Distance, 0.0f, maxRayDistance)
				: maxRayDistance;
			if (hit.Valid && hit.Backface)
			{
				backfaceCount++;
				if (distance < closestBackfaceDistance)
				{
					closestBackfaceDistance = distance;
					closestBackfaceDirection = direction;
				}
			}
			else
			{
				if (distance < closestFrontfaceDistance)
				{
					closestFrontfaceDistance = distance;
					closestFrontfaceDirection = direction;
				}
				if (distance > farthestFrontfaceDistance)
				{
					farthestFrontfaceDistance = distance;
					farthestFrontfaceDirection = direction;
				}
			}
		}

		var target = previousOffset;
		var decision = DdgiProbeRelocationDecision.None;
		var hasCandidate = false;
		var inside = backfaceCount / (float)Math.Max(hits.Length, 1) >
			Math.Clamp(backfaceThreshold, 0.0f, 1.0f);
		if (inside)
		{
			target += closestBackfaceDirection *
				(closestBackfaceDistance + keepDistance * 0.5f);
			decision = DdgiProbeRelocationDecision.BackfaceEscape;
			hasCandidate = true;
		}
		else if (closestFrontfaceDistance + relocationTolerance < keepDistance)
		{
			if (Vector3.Dot(closestFrontfaceDirection, farthestFrontfaceDirection) <= 0.0f)
			{
				target += farthestFrontfaceDirection * Math.Min(farthestFrontfaceDistance, 1.0f);
				decision = DdgiProbeRelocationDecision.FrontfaceSeparation;
				hasCandidate = true;
			}
		}
		else if (closestFrontfaceDistance > keepDistance &&
		         previousOffset.LengthSquared() > relocationTolerance * relocationTolerance)
		{
			var moveBackMargin = Math.Min(
				closestFrontfaceDistance - keepDistance,
				previousOffset.Length());
			target += Vector3.Normalize(-previousOffset) * moveBackMargin;
			decision = DdgiProbeRelocationDecision.ReturnToLattice;
			hasCandidate = true;
		}

		var acceptanceRadius = Math.Max(maxRelocationDistance, 0.0f);
		if (!hasCandidate || target.LengthSquared() >= acceptanceRadius * acceptanceRadius)
		{
			target = previousOffset;
		}
		return new DdgiRelocationResult(
			target,
			DdgiProbeState.Stable,
			decision,
			backfaceCount);
	}

	public static Vector3 GetRelocationRayDirection(int rayIndex)
	{
		const float goldenAngle = 2.39996322973f;
		var sampleIndex = Math.Clamp(rayIndex, 0, RelocationRayCount - 1) + 0.5f;
		var y = 1.0f - 2.0f * sampleIndex / RelocationRayCount;
		var radius = MathF.Sqrt(MathF.Max(0.0f, 1.0f - y * y));
		var azimuth = goldenAngle * sampleIndex;
		return new Vector3(MathF.Cos(azimuth) * radius, y, MathF.Sin(azimuth) * radius);
	}

	public static bool IsProbeRelocationUpdateActive(
		bool enabled,
		bool hasHistory,
		bool scheduled)
	{
		if (enabled == false)
		{
			return false;
		}

		return hasHistory == false ||
		       scheduled;
	}

	public static bool CanProbeContribute(DdgiProbeState state, bool enabled)
	{
		return enabled && state == DdgiProbeState.Stable;
	}

	private static Vector3 NormalizeRelocationDirection(Vector3 direction)
	{
		return direction == Vector3.Zero ? Vector3.UnitZ : Vector3.Normalize(direction);
	}

	public static bool IsProbeInsideGeometry(
		ReadOnlySpan<DdgiRelocationHit> hits,
		float backfaceThreshold)
	{
		var backfaceHitCount = 0;
		foreach (var hit in hits)
		{
			if (hit.Valid == false)
			{
				continue;
			}

			if (hit.Backface)
			{
				backfaceHitCount++;
			}
		}

		return backfaceHitCount > 0 &&
		       backfaceHitCount / (float)Math.Max(hits.Length, 1) >
		       Math.Clamp(backfaceThreshold, 0.0f, 1.0f);
	}

	public static bool IsBackfaceHit(Vector3 surfaceNormal, Vector3 rayDirection)
	{
		if (surfaceNormal == Vector3.Zero || rayDirection == Vector3.Zero)
		{
			return false;
		}

		return Vector3.Dot(Vector3.Normalize(surfaceNormal), Vector3.Normalize(rayDirection)) > 0.0f;
	}

	public static Vector3 UpdateProbeRelocation(
		Vector3 previousOffset,
		Vector3 targetOffset,
		float maxRelocationDistance,
		bool active,
		bool hasUsableHistory = true)
	{
		previousOffset = ClampProbeRelocationOffset(previousOffset, maxRelocationDistance);
		if (active == false)
		{
			return previousOffset;
		}

		targetOffset = ClampProbeRelocationOffset(targetOffset, maxRelocationDistance);
		return targetOffset;
	}

	public static Vector3 ClampProbeRelocationOffset(Vector3 offset, float maxRelocationDistance)
	{
		var maxOffset = Math.Max(maxRelocationDistance, 0.0f);
		var length = offset.Length();
		return length > maxOffset && length > 0.0f
			? offset * (maxOffset / length)
			: offset;
	}

	public static float GetProbeMaxRelocationDistance(DiffuseGlobalIlluminationConfig config)
	{
		var spacing = Math.Max(config.ProbeSpacing, 0.001f);
		var factor = Math.Clamp(config.ProbeMaxRelocationDistanceFactor, 0.0f, MaxProbeRelocationDistanceFactor);
		return spacing * factor;
	}

	public static float GetRecursiveBounceEnergy(DiffuseGlobalIlluminationConfig config)
	{
		return Math.Clamp(config.RecursiveBounceEnergy, 0.0f, 1.0f);
	}

	public static Vector3 ShadeDiffuseHit(
		Vector3 albedo,
		Vector3 directLightRadiance,
		float normalDotLight,
		float visibility,
		Vector3 previousDdgi,
		Vector3 emissive,
		bool historyValid,
		float recursiveBounceEnergy = DefaultRecursiveBounceEnergy)
	{
		albedo = Vector3.Max(albedo, Vector3.Zero);
		var direct = directLightRadiance *
		             Math.Clamp(normalDotLight, 0.0f, 1.0f) *
		             Math.Clamp(visibility, 0.0f, 1.0f) /
		             MathF.PI;
		var recursive = historyValid
			? Vector3.Max(previousDdgi, Vector3.Zero) * Math.Clamp(recursiveBounceEnergy, 0.0f, 1.0f)
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

	public static float GetProbeInfluenceHalfExtent(float probeSpacing, float viewBias)
	{
		return Math.Max(probeSpacing, 0.001f) + Math.Max(viewBias, 0.0f);
	}

	public static bool SphereIntersectsProbeInfluence(
		Vector3 sphereCenter,
		float sphereRadius,
		Vector3 probePosition,
		float influenceHalfExtent)
	{
		var halfExtent = Math.Max(influenceHalfExtent, 0.0f);
		var delta = Vector3.Abs(sphereCenter - probePosition) - new Vector3(halfExtent);
		var outside = Vector3.Max(delta, Vector3.Zero);
		var radius = Math.Max(sphereRadius, 0.0f);
		return outside.LengthSquared() <= radius * radius;
	}

	public static bool IsProbeUpdateActive(
		int probeIndex,
		int probeUpdateFrames,
		int probeUpdateFrameIndex,
		bool forceFullUpdate,
		bool enabled,
		bool previouslyEnabled,
		bool hasHistory)
	{
		return enabled &&
		       (forceFullUpdate ||
		        hasHistory == false ||
		        previouslyEnabled == false ||
		        IsProbeActive(probeIndex, probeUpdateFrames, probeUpdateFrameIndex, forceFullUpdate: false));
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

	public static int GetActiveProbeCount(
		DdgiGridShape shape,
		int probeUpdateFrames,
		int probeUpdateFrameIndex,
		bool forceFullUpdate,
		Int3 scrollDelta,
		bool historyValid)
	{
		var count = 0;
		for (var probeIndex = 0; probeIndex < shape.ProbeCount; probeIndex++)
		{
			if (IsProbeActive(probeIndex, probeUpdateFrames, probeUpdateFrameIndex, forceFullUpdate) ||
			    IsProbeNewlyExposed(probeIndex, scrollDelta, shape, historyValid))
			{
				count++;
			}
		}

		return count;
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
	bool Valid = true,
	bool Backface = false);

public enum DdgiProbeState : uint
{
	Disabled,
	Stable
}

public readonly record struct DdgiRelocationResult(
	Vector3 Offset,
	DdgiProbeState State,
	DdgiProbeRelocationDecision Decision,
	int BackfaceHitCount);
