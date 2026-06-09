using System.Numerics;
using System.Text.Json;
using WolfEngine;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class RayTracingSceneResourcesTests
{
	[Test]
	public void RtaoShaderCompilesForMetal()
	{
		if (OperatingSystem.IsMacOS() == false)
		{
			Assert.Ignore("Metal shader validation only runs on macOS.");
		}

		var shaderCompiler = new ShaderCompiler();
		var compiled = shaderCompiler.GetComputeShaderWithReflection(
			"ao_rtao.compute.slang",
			"CSMain",
			GraphicsBackendKind.Metal);

		Assert.That(compiled.Bytecode.IsEmpty, Is.False);
		Assert.That(compiled.ThreadGroupSize.X, Is.EqualTo(8));
		Assert.That(compiled.ThreadGroupSize.Y, Is.EqualTo(8));
	}

	[Test]
	public void DdgiShadersCompileForMetal()
	{
		if (OperatingSystem.IsMacOS() == false)
		{
			Assert.Ignore("Metal shader validation only runs on macOS.");
		}

		var shaderCompiler = new ShaderCompiler();
		foreach (var shader in new[]
		         {
				         "ddgi_trace.compute.slang",
				         "ddgi_relocate.compute.slang",
				         "ddgi_irradiance_integrate.compute.slang",
				         "ddgi_integrate.compute.slang",
			         "ddgi_border_update.compute.slang"
		         })
		{
			var compiled = shaderCompiler.GetComputeShaderWithReflection(
				shader,
				"CSMain",
				GraphicsBackendKind.Metal);

			Assert.That(compiled.Bytecode.IsEmpty, Is.False, shader);
			Assert.That(compiled.ThreadGroupSize.X, Is.EqualTo(8), shader);
			Assert.That(compiled.ThreadGroupSize.Y, Is.EqualTo(8), shader);
		}
	}

	[Test]
	public void DeferredLightingShaderCompilesForMetal()
	{
		if (OperatingSystem.IsMacOS() == false)
		{
			Assert.Ignore("Metal shader validation only runs on macOS.");
		}

		var shaderCompiler = new ShaderCompiler();
		var compiled = shaderCompiler.GetComputeShaderWithReflection(
			"deferred_lighting.compute.slang",
			"CSMain",
			GraphicsBackendKind.Metal);

		Assert.That(compiled.Bytecode.IsEmpty, Is.False);
		Assert.That(compiled.ThreadGroupSize.X, Is.EqualTo(8));
		Assert.That(compiled.ThreadGroupSize.Y, Is.EqualTo(8));
	}

	[Test]
	public void DdgiDefaultsAndAtlasSizingMatchMilestoneDefaults()
	{
		var config = new RenderConfig();
		var ddgi = config.DiffuseGlobalIllumination;

		Assert.That(ddgi.Enabled, Is.False);
		Assert.That(ddgi.Mode, Is.EqualTo(DiffuseGlobalIlluminationMode.RayTracedDdgi));
		Assert.That(ddgi.ProbeCounts.X, Is.EqualTo(16));
		Assert.That(ddgi.ProbeCounts.Y, Is.EqualTo(8));
		Assert.That(ddgi.ProbeCounts.Z, Is.EqualTo(16));
		Assert.That(ddgi.ProbeSpacing, Is.EqualTo(2.0f));
		Assert.That(ddgi.RaysPerProbe, Is.EqualTo(64));
		Assert.That(ddgi.ProbeUpdateFrames, Is.EqualTo(8));
		Assert.That(ddgi.MaxRayDistance, Is.EqualTo(6.0f));
		Assert.That(ddgi.NormalBias, Is.EqualTo(0.05f));
		Assert.That(ddgi.ViewBias, Is.EqualTo(0.2f));
		Assert.That(ddgi.HorizontalBlendDistance, Is.EqualTo(6.0f));
		Assert.That(ddgi.VerticalBlendDistance, Is.EqualTo(6.0f));
		Assert.That(ddgi.IrradianceTemporalBlendSpeed, Is.EqualTo(0.08f));
		Assert.That(ddgi.Hysteresis, Is.EqualTo(0.95f));
		Assert.That(ddgi.ProbeRelocationEnabled, Is.True);
		Assert.That(ddgi.ProbeMinFrontfaceDistance, Is.EqualTo(0.2f));
		Assert.That(ddgi.ProbeBackfaceThreshold, Is.EqualTo(0.25f));
		Assert.That(ddgi.ProbeMaxRelocationDistanceFactor, Is.EqualTo(0.45f));
		Assert.That(ddgi.DebugProbeSpheres, Is.False);
		Assert.That(ddgi.DebugProbeSphereRadius, Is.EqualTo(0.15f));

		var shape = DdgiUtilities.GetGridShape(ddgi);
		Assert.That(shape.ProbeCount, Is.EqualTo(2048));
		Assert.That(DdgiUtilities.GetAtlasSize(shape, DdgiUtilities.IrradianceTileInteriorSize), Is.EqualTo(new Int2(460, 450)));
		Assert.That(DdgiUtilities.GetAtlasSize(shape, DdgiUtilities.VisibilityTileInteriorSize), Is.EqualTo(new Int2(828, 810)));
	}

	[Test]
	public void RenderConfig_DdgiSettingsRoundTripThroughAssetJson()
	{
		var config = new RenderConfig
		{
			DiffuseGlobalIllumination = new DiffuseGlobalIlluminationConfig
			{
				Enabled = true,
				Mode = DiffuseGlobalIlluminationMode.RayTracedDdgi,
				Origin = new Vector3(1.0f, 2.0f, 3.0f),
				ProbeCounts = new DdgiProbeCounts { X = 4, Y = 5, Z = 6 },
				ProbeSpacing = 3.5f,
				RaysPerProbe = 32,
				ProbeUpdateFrames = 4,
				MaxRayDistance = 12.0f,
					NormalBias = 0.1f,
					ViewBias = 0.4f,
					HorizontalBlendDistance = 8.0f,
					VerticalBlendDistance = 4.0f,
					IrradianceTemporalBlendSpeed = 0.12f,
					Hysteresis = 0.8f,
					ProbeRelocationEnabled = false,
					ProbeMinFrontfaceDistance = 0.3f,
					ProbeBackfaceThreshold = 0.4f,
					ProbeMaxRelocationDistanceFactor = 0.35f,
				DebugProbeSpheres = true,
				DebugProbeSphereRadius = 0.3f
			}
		};

		var json = JsonSerializer.Serialize(config, AssetJson.SerializerOptions);
		var roundTripped = JsonSerializer.Deserialize<RenderConfig>(json, AssetJson.SerializerOptions)!;
		var ddgi = roundTripped.DiffuseGlobalIllumination;

		Assert.That(ddgi.Enabled, Is.True);
		Assert.That(ddgi.Mode, Is.EqualTo(DiffuseGlobalIlluminationMode.RayTracedDdgi));
		Assert.That(ddgi.Origin, Is.EqualTo(new Vector3(1.0f, 2.0f, 3.0f)));
		Assert.That(ddgi.ProbeCounts.X, Is.EqualTo(4));
		Assert.That(ddgi.ProbeCounts.Y, Is.EqualTo(5));
		Assert.That(ddgi.ProbeCounts.Z, Is.EqualTo(6));
		Assert.That(ddgi.ProbeSpacing, Is.EqualTo(3.5f));
		Assert.That(ddgi.RaysPerProbe, Is.EqualTo(32));
		Assert.That(ddgi.ProbeUpdateFrames, Is.EqualTo(4));
		Assert.That(ddgi.MaxRayDistance, Is.EqualTo(12.0f));
		Assert.That(ddgi.NormalBias, Is.EqualTo(0.1f));
		Assert.That(ddgi.ViewBias, Is.EqualTo(0.4f));
		Assert.That(ddgi.HorizontalBlendDistance, Is.EqualTo(8.0f));
		Assert.That(ddgi.VerticalBlendDistance, Is.EqualTo(4.0f));
		Assert.That(ddgi.IrradianceTemporalBlendSpeed, Is.EqualTo(0.12f));
		Assert.That(ddgi.Hysteresis, Is.EqualTo(0.8f));
		Assert.That(ddgi.ProbeRelocationEnabled, Is.False);
		Assert.That(ddgi.ProbeMinFrontfaceDistance, Is.EqualTo(0.3f));
		Assert.That(ddgi.ProbeBackfaceThreshold, Is.EqualTo(0.4f));
		Assert.That(ddgi.ProbeMaxRelocationDistanceFactor, Is.EqualTo(0.35f));
		Assert.That(ddgi.DebugProbeSpheres, Is.True);
		Assert.That(ddgi.DebugProbeSphereRadius, Is.EqualTo(0.3f));
	}

	[Test]
	public void DdgiProbeUpdateFramesClampToAtLeastOne()
	{
		var config = new DiffuseGlobalIlluminationConfig
		{
			ProbeUpdateFrames = 0
		};

		Assert.That(DdgiUtilities.GetProbeUpdateFrames(config), Is.EqualTo(1));
		Assert.That(DdgiUtilities.GetProbeUpdateFrameIndex(5, 0), Is.EqualTo(0));
		Assert.That(DdgiUtilities.IsProbeActive(3, 0, 0, forceFullUpdate: false), Is.True);
		Assert.That(DdgiUtilities.GetActiveProbeCount(17, 0, 0, forceFullUpdate: false), Is.EqualTo(17));
	}

	[TestCase(0, DdgiUtilities.IrradianceTileInteriorSize, 1)]
	[TestCase(32, DdgiUtilities.IrradianceTileInteriorSize, 32)]
	[TestCase(1024, DdgiUtilities.IrradianceTileInteriorSize, 64)]
	[TestCase(1024, DdgiUtilities.VisibilityTileInteriorSize, 256)]
	public void DdgiRaySampleCountDoesNotExceedTemporaryTileCapacity(
		int requestedRayCount,
		int tileInteriorSize,
		int expectedRayCount)
	{
		Assert.That(
			DdgiUtilities.GetRaySampleCount(requestedRayCount, tileInteriorSize),
			Is.EqualTo(expectedRayCount));
	}

	[Test]
	public void DdgiProbeBatchingUpdatesEveryProbeOncePerCycle()
	{
		const int probeCount = 10;
		const int updateFrames = 4;
		var updateCounts = new int[probeCount];

		for (uint frameIndex = 0; frameIndex < updateFrames; frameIndex++)
		{
			var frameSlot = DdgiUtilities.GetProbeUpdateFrameIndex(frameIndex, updateFrames);
			var activeCount = 0;
			for (var probeIndex = 0; probeIndex < probeCount; probeIndex++)
			{
				if (DdgiUtilities.IsProbeActive(probeIndex, updateFrames, frameSlot, forceFullUpdate: false) == false)
				{
					continue;
				}

				updateCounts[probeIndex]++;
				activeCount++;
			}

			Assert.That(activeCount, Is.EqualTo(DdgiUtilities.GetActiveProbeCount(probeCount, updateFrames, frameSlot, forceFullUpdate: false)));
		}

		Assert.That(updateCounts, Is.All.EqualTo(1));
	}

	[Test]
	public void DdgiProbeBatchingForceFullUpdateMarksAllProbesActive()
	{
		const int probeCount = 10;
		const int updateFrames = 4;

		Assert.That(DdgiUtilities.GetActiveProbeCount(probeCount, updateFrames, 2, forceFullUpdate: true), Is.EqualTo(probeCount));
		for (var probeIndex = 0; probeIndex < probeCount; probeIndex++)
		{
			Assert.That(DdgiUtilities.IsProbeActive(probeIndex, updateFrames, 2, forceFullUpdate: true), Is.True);
		}
	}

	[Test]
	public void DdgiShCoefficientTexturesUseOneTexelPerProbe()
	{
		var shape = DdgiUtilities.GetGridShape(new DiffuseGlobalIlluminationConfig
		{
			ProbeCounts = new DdgiProbeCounts { X = 4, Y = 3, Z = 2 }
		});

		var size = DdgiUtilities.GetShCoefficientTextureSize(shape);

		Assert.That(DdgiUtilities.ShCoefficientCount, Is.EqualTo(4));
		Assert.That(size.X, Is.EqualTo(shape.AtlasColumns));
		Assert.That(size.Y, Is.EqualTo(shape.AtlasRows));
	}

	[Test]
	public void DdgiIrradianceEstimatorUsesPackedDirectionStatePerProbe()
	{
		var shape = DdgiUtilities.GetGridShape(new DiffuseGlobalIlluminationConfig
		{
			ProbeCounts = new DdgiProbeCounts { X = 8, Y = 8, Z = 8 }
		});

		Assert.That(DdgiUtilities.IrradianceEstimatorDirectionCount, Is.EqualTo(64));
		Assert.That(DdgiUtilities.IrradianceEstimatorStride, Is.EqualTo(16));
		Assert.That(
			DdgiUtilities.GetIrradianceEstimatorBufferSize(shape),
			Is.EqualTo((ulong)shape.ProbeCount * 64UL * 16UL));
	}

	[Test]
	public void RenderGraphRegistryMaterializesImportedBuffersAsGenericResources()
	{
		var registry = new RenderGraphResourceRegistry();
		var buffer = new TestBuffer(BufferUsage.Structured);
		var handle = registry.ImportBuffer(buffer, initialState: ResourceState.UnorderedAccess);

		Assert.That(registry.GetResource(handle), Is.SameAs(buffer));
		Assert.That(registry.GetResourceState(handle), Is.EqualTo(ResourceState.UnorderedAccess));
	}

	[TestCase(0.0f, 0.0f, 0.0f)]
	[TestCase(1.0f, 0.25f, 8.0f)]
	[TestCase(120.0f, 4.0f, 0.5f)]
	public void DdgiEstimatorPackingRoundTripsHdrValues(float r, float g, float b)
	{
		var value = new Vector3(r, g, b);
		var unpacked = DdgiUtilities.UnpackRgbe(DdgiUtilities.PackRgbe(value));
		var tolerance = MathF.Max(0.002f, MathF.Max(r, MathF.Max(g, b)) / 256.0f);

		Assert.That(unpacked.X, Is.EqualTo(r).Within(tolerance));
		Assert.That(unpacked.Y, Is.EqualTo(g).Within(tolerance));
		Assert.That(unpacked.Z, Is.EqualTo(b).Within(tolerance));

		var halfValues = new Vector2(0.03125f + r * 0.001f, 1.0f + g * 0.001f);
		var unpackedHalf = DdgiUtilities.UnpackHalf2(DdgiUtilities.PackHalf2(halfValues));
		Assert.That(unpackedHalf.X, Is.EqualTo(halfValues.X).Within(0.002f));
		Assert.That(unpackedHalf.Y, Is.EqualTo(halfValues.Y).Within(0.002f));
	}

	[Test]
	public void DdgiEstimatorSuppressesSingleFireflyAndConvergesAfterLightingChange()
	{
		var stable = new Vector3(1.0f);
		var state = new DdgiVarianceData(stable, stable, 0.0f, Vector3.Zero, 1.0f);
		state = DdgiUtilities.UpdateVarianceEstimator(stable, state, 0.08f);
		var beforeFirefly = state.Mean;
		state = DdgiUtilities.UpdateVarianceEstimator(new Vector3(1000.0f), state, 0.08f);
		Assert.That(state.Mean.X - beforeFirefly.X, Is.LessThan(0.1f));

		for (var i = 0; i < 96; i++)
		{
			state = DdgiUtilities.UpdateVarianceEstimator(new Vector3(4.0f), state, 0.08f);
		}

		Assert.That(state.Mean.X, Is.GreaterThan(2.5f));
		Assert.That(state.Mean.X, Is.LessThanOrEqualTo(4.0f));
	}

	[Test]
	public void DdgiEstimatorCapsAdaptiveCatchUpAtTwoPercentPerUpdate()
	{
		var state = new DdgiVarianceData(
			Vector3.Zero,
			new Vector3(10.0f),
			1.0f,
			new Vector3(100.0f),
			10.0f);

		var updated = DdgiUtilities.UpdateVarianceEstimator(new Vector3(10.0f), state, 0.08f);

		Assert.That(updated.Mean.X, Is.EqualTo(0.2f).Within(1e-5f));
		Assert.That(updated.Mean.Y, Is.EqualTo(0.2f).Within(1e-5f));
		Assert.That(updated.Mean.Z, Is.EqualTo(0.2f).Within(1e-5f));
	}

	[Test]
	public void DdgiEstimatorAlternatingNoiseCannotCauseLargeMeanJumps()
	{
		var state = new DdgiVarianceData(
			new Vector3(5.0f),
			new Vector3(5.0f),
			1.0f,
			new Vector3(25.0f),
			10.0f);

		for (var updateIndex = 0; updateIndex < 32; updateIndex++)
		{
			var sample = new Vector3(updateIndex % 2 == 0 ? 0.0f : 10.0f);
			var previousMean = state.Mean;
			state = DdgiUtilities.UpdateVarianceEstimator(sample, state, 0.08f);

			var maximumAllowedMovement = Vector3.Abs(sample - previousMean) * 0.02f;
			var actualMovement = Vector3.Abs(state.Mean - previousMean);
			Assert.That(actualMovement.X, Is.LessThanOrEqualTo(maximumAllowedMovement.X + 1e-5f));
			Assert.That(actualMovement.Y, Is.LessThanOrEqualTo(maximumAllowedMovement.Y + 1e-5f));
			Assert.That(actualMovement.Z, Is.LessThanOrEqualTo(maximumAllowedMovement.Z + 1e-5f));
		}
	}

	[Test]
	public void DdgiL1ShReconstructsConstantRadianceForEveryNormal()
	{
		const int sampleCount = 4096;
		var radiance = new Vector3(1.5f, 0.75f, 0.25f);
		var sh = default(DdgiL1Sh);
		var solidAngle = 4.0f * MathF.PI / sampleCount;
		for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
		{
			sh += DdgiUtilities.ProjectRadiance(SphericalFibonacci(sampleIndex, sampleCount), radiance, solidAngle);
		}

		foreach (var normal in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, Vector3.Normalize(Vector3.One) })
		{
			var evaluated = DdgiUtilities.EvaluateDiffuse(sh, normal);
			Assert.That(evaluated.X, Is.EqualTo(radiance.X).Within(0.002f));
			Assert.That(evaluated.Y, Is.EqualTo(radiance.Y).Within(0.002f));
			Assert.That(evaluated.Z, Is.EqualTo(radiance.Z).Within(0.002f));
		}
	}

	[Test]
	public void DdgiL1ShDirectionalCoefficientsFollowAxisAndEvaluateSmoothly()
	{
		var radiance = Vector3.One;
		var xSh = DdgiUtilities.ProjectRadiance(Vector3.UnitX, radiance, 1.0f);
		var ySh = DdgiUtilities.ProjectRadiance(Vector3.UnitY, radiance, 1.0f);
		var zSh = DdgiUtilities.ProjectRadiance(Vector3.UnitZ, radiance, 1.0f);

		Assert.That(xSh.Lx.X, Is.GreaterThan(0.0f));
		Assert.That(xSh.Ly, Is.EqualTo(Vector3.Zero));
		Assert.That(xSh.Lz, Is.EqualTo(Vector3.Zero));
		Assert.That(ySh.Ly.X, Is.GreaterThan(0.0f));
		Assert.That(ySh.Lx, Is.EqualTo(Vector3.Zero));
		Assert.That(ySh.Lz, Is.EqualTo(Vector3.Zero));
		Assert.That(zSh.Lz.X, Is.GreaterThan(0.0f));
		Assert.That(zSh.Lx, Is.EqualTo(Vector3.Zero));
		Assert.That(zSh.Ly, Is.EqualTo(Vector3.Zero));

		var facing = DdgiUtilities.EvaluateDiffuse(xSh, Vector3.UnitX).X;
		var perpendicular = DdgiUtilities.EvaluateDiffuse(xSh, Vector3.UnitY).X;
		var halfway = DdgiUtilities.EvaluateDiffuse(xSh, Vector3.Normalize(Vector3.UnitX + Vector3.UnitY)).X;
		var opposite = DdgiUtilities.EvaluateDiffuse(xSh, -Vector3.UnitX);
		Assert.That(halfway, Is.GreaterThan(perpendicular));
		Assert.That(halfway, Is.LessThan(facing));
		Assert.That(opposite.X, Is.GreaterThan(0.0f));
		Assert.That(opposite.Y, Is.GreaterThan(0.0f));
		Assert.That(opposite.Z, Is.GreaterThan(0.0f));
		Assert.That(opposite.X, Is.EqualTo(opposite.Y).Within(1e-6f));
		Assert.That(opposite.Y, Is.EqualTo(opposite.Z).Within(1e-6f));
	}

	[Test]
	public void DdgiVisibilityMomentsRemainDirectional()
	{
		Assert.That(DdgiUtilities.GetVisibilityDirectionalWeight(1.0f), Is.EqualTo(1.0f));
		Assert.That(DdgiUtilities.GetVisibilityDirectionalWeight(0.9f), Is.LessThan(0.002f));
		Assert.That(DdgiUtilities.GetVisibilityDirectionalWeight(0.5f), Is.LessThan(1e-12f));
	}

	[Test]
	public void DdgiVisibilityStronglyRejectsSamplesBehindOccluders()
	{
		const float meanDistance = 0.25f;
		const float meanDistanceSquared = 0.065f;

		Assert.That(
			DdgiUtilities.EvaluateVisibility(meanDistance, meanDistanceSquared, 0.2f),
			Is.EqualTo(1.0f));
		Assert.That(
			DdgiUtilities.EvaluateVisibility(meanDistance, meanDistanceSquared, 0.5f),
			Is.LessThan(0.001f));
	}

	[Test]
	public void DdgiDiffuseHitUsesLambertianDirectNormalization()
	{
		var shaded = DdgiUtilities.ShadeDiffuseHit(
			Vector3.One,
			new Vector3(MathF.PI),
			normalDotLight: 1.0f,
			visibility: 1.0f,
			previousDdgi: Vector3.Zero,
			emissive: Vector3.Zero,
			historyValid: false);

		AssertVector3(shaded, Vector3.One);
	}

	[Test]
	public void DdgiDiffuseHitPreservesWhiteSurfaceColorRatios()
	{
		var shaded = DdgiUtilities.ShadeDiffuseHit(
			Vector3.One,
			new Vector3(1.0f, 2.0f, 4.0f),
			normalDotLight: 1.0f,
			visibility: 1.0f,
			previousDdgi: Vector3.Zero,
			emissive: Vector3.Zero,
			historyValid: false);

		Assert.That(shaded.Y / shaded.X, Is.EqualTo(2.0f).Within(1e-6f));
		Assert.That(shaded.Z / shaded.X, Is.EqualTo(4.0f).Within(1e-6f));
	}

	[Test]
	public void DdgiDiffuseHitAlbedoTintsDirectAndRecursiveRadiance()
	{
		var shaded = DdgiUtilities.ShadeDiffuseHit(
			new Vector3(1.0f, 0.25f, 0.0f),
			new Vector3(MathF.PI),
			normalDotLight: 1.0f,
			visibility: 1.0f,
			previousDdgi: Vector3.One,
			emissive: Vector3.Zero,
			historyValid: true);

		Assert.That(shaded.X, Is.EqualTo(1.95f).Within(1e-6f));
		Assert.That(shaded.Y, Is.EqualTo(0.4875f).Within(1e-6f));
		Assert.That(shaded.Z, Is.EqualTo(0.0f).Within(1e-6f));
	}

	[Test]
	public void DdgiDiffuseHitAddsEmissiveIndependentlyOfAlbedo()
	{
		var emissive = new Vector3(3.0f, 1.0f, 0.5f);
		var shaded = DdgiUtilities.ShadeDiffuseHit(
			Vector3.Zero,
			new Vector3(100.0f),
			normalDotLight: 1.0f,
			visibility: 1.0f,
			previousDdgi: new Vector3(100.0f),
			emissive,
			historyValid: true);

		AssertVector3(shaded, emissive);
	}

	[Test]
	public void DdgiDiffuseHitIgnoresRecursiveRadianceWithoutHistory()
	{
		var shaded = DdgiUtilities.ShadeDiffuseHit(
			Vector3.One,
			Vector3.Zero,
			normalDotLight: 0.0f,
			visibility: 0.0f,
			previousDdgi: new Vector3(10.0f),
			emissive: Vector3.Zero,
			historyValid: false);

		AssertVector3(shaded, Vector3.Zero);
	}

	[Test]
	public void DdgiDiffuseHitCapsRecursiveBounceEnergyAtNinetyFivePercent()
	{
		var shaded = DdgiUtilities.ShadeDiffuseHit(
			Vector3.One,
			Vector3.Zero,
			normalDotLight: 0.0f,
			visibility: 0.0f,
			previousDdgi: Vector3.One,
			emissive: Vector3.Zero,
			historyValid: true);

		AssertVector3(shaded, new Vector3(0.95f));
	}

	[Test]
	public void DdgiRelocationNoNearbyHitsReturnsTowardRestPosition()
	{
		var target = DdgiUtilities.ComputeProbeRelocationTarget(
			[new DdgiRelocationHit(Vector3.UnitX, 0.5f)],
			keepDistance: 0.2f,
			maxRelocationDistance: 0.9f);
		var updated = DdgiUtilities.UpdateProbeRelocation(
			new Vector3(0.5f, -0.25f, 0.1f),
			target,
			maxRelocationDistance: 0.9f,
			active: true);

		Assert.That(target, Is.EqualTo(Vector3.Zero));
		AssertVector3(updated, new Vector3(0.495f, -0.2475f, 0.099f));
	}

	[Test]
	public void DdgiRelocationCombinesAllNearbyHitsIntoFreshTarget()
	{
		var target = DdgiUtilities.ComputeProbeRelocationTarget(
			[
				new DdgiRelocationHit(Vector3.UnitX, 0.1f),
				new DdgiRelocationHit(Vector3.UnitY, 0.05f),
				new DdgiRelocationHit(Vector3.UnitZ, 0.3f),
				new DdgiRelocationHit(-Vector3.UnitX, 0.0f, Valid: false)
			],
			keepDistance: 0.2f,
			maxRelocationDistance: 0.9f);

		AssertVector3(target, new Vector3(-0.1f, -0.15f, 0.0f));
	}

	[Test]
	public void DdgiRelocationRepeatedBatchConvergesWithoutCumulativeDrift()
	{
		var target = DdgiUtilities.ComputeProbeRelocationTarget(
			[new DdgiRelocationHit(Vector3.UnitX, 0.0f)],
			keepDistance: 0.2f,
			maxRelocationDistance: 0.9f);
		var offset = Vector3.Zero;

		for (var update = 0; update < 1000; update++)
		{
			offset = DdgiUtilities.UpdateProbeRelocation(offset, target, 0.9f, active: true);
		}

		AssertVector3(offset, new Vector3(-0.2f, 0.0f, 0.0f), 1e-4f);
	}

	[Test]
	public void DdgiRelocationAlternatingTargetsCannotCauseLargeJumps()
	{
		var offset = Vector3.Zero;
		var positiveTarget = new Vector3(0.9f, 0.0f, 0.0f);
		var negativeTarget = -positiveTarget;

		for (var update = 0; update < 64; update++)
		{
			var previous = offset;
			var target = update % 2 == 0 ? positiveTarget : negativeTarget;
			offset = DdgiUtilities.UpdateProbeRelocation(previous, target, 0.9f, active: true);

			Assert.That(Vector3.Distance(previous, offset), Is.LessThanOrEqualTo(0.0091f));
		}
	}

	[Test]
	public void DdgiRelocationClampsTargetAndPersistentOffset()
	{
		var target = DdgiUtilities.ComputeProbeRelocationTarget(
			[
				new DdgiRelocationHit(-Vector3.UnitX, 0.0f),
				new DdgiRelocationHit(-Vector3.UnitX, 0.0f),
				new DdgiRelocationHit(-Vector3.UnitY, 0.0f)
			],
			keepDistance: 1.0f,
			maxRelocationDistance: 0.25f);
		var updated = DdgiUtilities.UpdateProbeRelocation(
			new Vector3(10.0f, -10.0f, 10.0f),
			target,
			maxRelocationDistance: 0.25f,
			active: true);

		AssertVector3(target, new Vector3(0.25f, 0.25f, 0.0f));
		Assert.That(updated.X, Is.InRange(-0.25f, 0.25f));
		Assert.That(updated.Y, Is.InRange(-0.25f, 0.25f));
		Assert.That(updated.Z, Is.InRange(-0.25f, 0.25f));
	}

	[Test]
	public void DdgiRelocationInactiveProbePreservesPreviousOffset()
	{
		var previous = new Vector3(0.2f, -0.1f, 0.05f);
		var updated = DdgiUtilities.UpdateProbeRelocation(
			previous,
			new Vector3(-0.9f),
			maxRelocationDistance: 0.9f,
			active: false);

		Assert.That(updated, Is.EqualTo(previous));
	}

	[Test]
	public void RenderPipeline_DdgiProbeDebugToggleInjectsAlphaBlendedSpherePrimitives()
	{
		var config = new RenderConfig
		{
			DiffuseGlobalIllumination = new DiffuseGlobalIlluminationConfig
			{
				Enabled = true,
				Mode = DiffuseGlobalIlluminationMode.RayTracedDdgi,
				ProbeCounts = new DdgiProbeCounts { X = 2, Y = 1, Z = 2 },
				ProbeSpacing = 3.0f,
				DebugProbeSpheres = true,
				DebugProbeSphereRadius = 0.25f
			}
		};
		var database = new GpuDrawDatabase();
		var meshFactory = new DebugPrimitiveMeshFactory();

		database.BeginSync();
		RenderPipeline.CollectDdgiProbeDebugPrimitives(config, database, meshFactory);
		database.EndSync();

		var entries = new List<GpuDrawEntry>();
		database.CollectDrawEntries(entries);

		Assert.That(entries, Has.Count.EqualTo(4));
		Assert.That(entries.Select(entry => entry.DrawKind), Is.All.EqualTo(GpuDrawKind.DebugPrimitive));
		Assert.That(entries.Select(entry => entry.Material.AlphaMode), Is.All.EqualTo(AlphaMode.AlphaBlend));
		Assert.That(entries.Select(entry => entry.Material.Color), Is.All.EqualTo(ColorRGBA.White));
		Assert.That(entries.Select(entry => entry.World.M11), Is.All.EqualTo(0.5f).Within(0.0001f));
	}

	[Test]
	public void RecordUpdate_BootstrapBuildsOpaqueMeshSceneAndReportsSkippedDraws()
	{
		var database = new GpuDrawDatabase();
		var opaqueMesh = CreateTestMesh();
		var alphaMesh = CreateTestMesh();
		var terrainMesh = CreateTestMesh();
		var opaqueMaterial = new Material("opaque");
		var alphaMaterial = new Material("alpha") { AlphaMode = AlphaMode.AlphaTest };
		var terrainMaterial = new Material("__terrain__");
		database.BeginSync();
		database.TouchMesh(new Entity(1, 1), opaqueMesh, opaqueMaterial, Matrix4x4.Identity);
		database.TouchMesh(new Entity(2, 1), alphaMesh, alphaMaterial, Matrix4x4.Identity);
		database.TouchTerrainChunk(
			new Entity(3, 1),
			0,
			terrainMesh,
			terrainMaterial,
			terrainMesh.BoundingSphere,
			CreateTerrainInstanceData(),
			CreateTerrainSurface(),
			Matrix4x4.Identity);
		database.EndSync();
		var updates = new List<GpuDrawUpdate>();
		database.CopyUpdates(updates);
		var commandList = new TestCommandList();
		var context = CreateContext(database, commandList);
		var resources = new RayTracingSceneResources();

		resources.RecordUpdate(context, new TestRenderer(new TestDevice()), updates);

		Assert.That(resources.LastStats.BottomLevelAccelerationStructureCount, Is.EqualTo(1));
		Assert.That(resources.LastStats.TopLevelInstanceCount, Is.EqualTo(1));
		Assert.That(resources.LastStats.PendingBottomLevelBuildCount, Is.EqualTo(1));
		Assert.That(resources.LastStats.TopLevelRebuildCount, Is.EqualTo(1));
		Assert.That(resources.LastStats.TopLevelRebuildReason, Is.EqualTo(RayTracingSceneRebuildReason.Bootstrap));
		Assert.That(resources.LastStats.SkippedTerrainCount, Is.EqualTo(1));
		Assert.That(resources.LastStats.SkippedTransparentOrAlphaCount, Is.EqualTo(1));
		Assert.That(resources.LastStats.SidecarHitShadingAvailable, Is.True);
		Assert.That(resources.InstanceIndexToInstanceHandleBuffer, Is.Not.Null);
		Assert.That(commandList.BottomLevelBuildCount, Is.EqualTo(1));
		Assert.That(commandList.TopLevelBuildCount, Is.EqualTo(1));
	}

	[Test]
	public void RecordUpdate_MaterialOnlyUpdateDoesNotRebuildTlas()
	{
		var database = new GpuDrawDatabase();
		var mesh = CreateTestMesh();
		var materialA = new Material("a");
		var materialB = new Material("b");
		var entity = new Entity(1, 1);
		var commandList = new TestCommandList();
		var context = CreateContext(database, commandList);
		var resources = new RayTracingSceneResources();
		var renderer = new TestRenderer(new TestDevice());

		database.BeginSync();
		database.TouchMesh(entity, mesh, materialA, Matrix4x4.Identity);
		database.EndSync();
		var updates = new List<GpuDrawUpdate>();
		database.CopyUpdates(updates);
		resources.RecordUpdate(context, renderer, updates);
		database.ConsumeUpdates(updates);

		database.BeginSync();
		database.TouchMesh(entity, mesh, materialB, Matrix4x4.Identity);
		database.EndSync();
		database.CopyUpdates(updates);
		commandList.ResetCounts();

		resources.RecordUpdate(context, renderer, updates);

		Assert.That(resources.LastStats.TopLevelRebuildCount, Is.EqualTo(0));
		Assert.That(resources.LastStats.TopLevelRebuildReason, Is.EqualTo(RayTracingSceneRebuildReason.None));
		Assert.That(commandList.BottomLevelBuildCount, Is.EqualTo(0));
		Assert.That(commandList.TopLevelBuildCount, Is.EqualTo(0));
	}

	[Test]
	public void RecordUpdate_TransformAndMeshSwapMarkTlasDirtyButCameraFreeFrameDoesNot()
	{
		var database = new GpuDrawDatabase();
		var meshA = CreateTestMesh();
		var meshB = CreateOffsetMesh();
		var material = new Material("opaque");
		var entity = new Entity(1, 1);
		var commandList = new TestCommandList();
		var context = CreateContext(database, commandList);
		var resources = new RayTracingSceneResources();
		var renderer = new TestRenderer(new TestDevice());

		database.BeginSync();
		database.TouchMesh(entity, meshA, material, Matrix4x4.Identity);
		database.EndSync();
		var updates = new List<GpuDrawUpdate>();
		database.CopyUpdates(updates);
		resources.RecordUpdate(context, renderer, updates);
		database.ConsumeUpdates(updates);

		database.BeginSync();
		database.TouchMesh(entity, meshA, material, Matrix4x4.CreateTranslation(1.0f, 0.0f, 0.0f));
		database.EndSync();
		database.CopyUpdates(updates);
		commandList.ResetCounts();
		resources.RecordUpdate(context, renderer, updates);
		Assert.That(resources.LastStats.TopLevelRebuildReason, Is.EqualTo(RayTracingSceneRebuildReason.Transform));
		Assert.That(commandList.TopLevelBuildCount, Is.EqualTo(1));
		database.ConsumeUpdates(updates);

		database.BeginSync();
		database.TouchMesh(entity, meshB, material, Matrix4x4.CreateTranslation(1.0f, 0.0f, 0.0f));
		database.EndSync();
		database.CopyUpdates(updates);
		commandList.ResetCounts();
		resources.RecordUpdate(context, renderer, updates);
		Assert.That(resources.LastStats.TopLevelRebuildReason.HasFlag(RayTracingSceneRebuildReason.Mesh), Is.True);
		Assert.That(resources.LastStats.PendingBottomLevelBuildCount, Is.EqualTo(1));
		Assert.That(commandList.BottomLevelBuildCount, Is.EqualTo(1));
		Assert.That(commandList.TopLevelBuildCount, Is.EqualTo(1));
		database.ConsumeUpdates(updates);

		commandList.ResetCounts();
		resources.RecordUpdate(context, renderer, Array.Empty<GpuDrawUpdate>());
		Assert.That(resources.LastStats.TopLevelRebuildCount, Is.EqualTo(0));
		Assert.That(resources.LastStats.TopLevelRebuildReason, Is.EqualTo(RayTracingSceneRebuildReason.None));
		Assert.That(commandList.TopLevelBuildCount, Is.EqualTo(0));
	}

	private static RenderGraphContext CreateContext(GpuDrawDatabase database, TestCommandList commandList)
	{
		var context = new RenderGraphContext(new RenderGraphResourceRegistry(), "RayTracingSceneResourcesTest")
		{
			CommandList = commandList,
			GpuDrawDatabase = database
		};
		return context;
	}

	private static Mesh CreateTestMesh()
	{
		return new Mesh(
			[
				new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
				new Vector4(1.0f, 0.0f, 0.0f, 1.0f),
				new Vector4(0.0f, 1.0f, 0.0f, 1.0f)
			],
			[0u, 1u, 2u]);
	}

	private static Mesh CreateOffsetMesh()
	{
		return new Mesh(
			[
				new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
				new Vector4(2.0f, 0.0f, 0.0f, 1.0f),
				new Vector4(0.0f, 2.0f, 0.0f, 1.0f)
			],
			[0u, 1u, 2u]);
	}

	private static TerrainDrawSurface CreateTerrainSurface()
	{
		return new TerrainDrawSurface(
			heightmap: null,
			layerIndexMap: null,
			layerWeightMap: null,
			heightScale: 16.0f,
			layerCount: 1,
			heightBlendSharpness: 4.0f,
			layers:
			[
				new TerrainResolvedLayer(null, null, null, null, 8.0f)
			]);
	}

	private static TerrainChunkInstanceData CreateTerrainInstanceData()
	{
		return new TerrainChunkInstanceData(
			new Vector4(0.0f, 0.0f, 8.0f, 8.0f),
			new Vector4(0.25f, 0.25f, 0.0f, 0.0f));
	}

	private static Vector3 SphericalFibonacci(int sampleIndex, int sampleCount)
	{
		const float goldenAngle = 2.39996322973f;
		var sample = sampleIndex + 0.5f;
		var cosTheta = 1.0f - 2.0f * sample / sampleCount;
		var sinTheta = MathF.Sqrt(MathF.Max(0.0f, 1.0f - cosTheta * cosTheta));
		var phi = sampleIndex * goldenAngle;
		return new Vector3(MathF.Cos(phi) * sinTheta, cosTheta, MathF.Sin(phi) * sinTheta);
	}

	private static void AssertVector3(Vector3 actual, Vector3 expected, float tolerance = 1e-6f)
	{
		Assert.That(actual.X, Is.EqualTo(expected.X).Within(tolerance));
		Assert.That(actual.Y, Is.EqualTo(expected.Y).Within(tolerance));
		Assert.That(actual.Z, Is.EqualTo(expected.Z).Within(tolerance));
	}

	private sealed class TestRenderer : IRenderer
	{
		private readonly IGfxDevice _device;
		private readonly TestBuffer _vertexBuffer = new(BufferUsage.Vertex);
		private readonly TestBuffer _indexBuffer = new(BufferUsage.Index);

		public TestRenderer(IGfxDevice device)
		{
			_device = device;
		}

		public void Run(Action startup, Action<float> update, Action<float> render) => throw new NotSupportedException();
		public IMaterialResources CreateMaterialResources(Material material) => throw new NotSupportedException();
		public ITextureResources CreateTextureResources(Texture texture) => throw new NotSupportedException();
		public IGfxDevice GetGfxDevice() => _device;
		public Int2 GetFrameBufferSize() => throw new NotSupportedException();
		public Int2 GetWindowSize() => throw new NotSupportedException();
		public void BeginFrame() => throw new NotSupportedException();
		public void Render(RenderGraphResourceRegistry resourceRegistry, RenderGraphResourceHandle finalColor) => throw new NotSupportedException();
		public RenderGraphResourceHandle ImportBackbuffer(RenderGraphResourceRegistry registry, int width, int height) => throw new NotSupportedException();
		public void ReleaseMeshResources(Mesh mesh) { }
		public IGfxBuffer GetPackedMeshVertexBuffer() => _vertexBuffer;
		public IGfxBuffer GetPackedMeshIndexBuffer() => _indexBuffer;
		public bool SupportsGpuCapture => false;
		public bool IsGpuCaptureActive => false;
		public string LastGpuCapturePath => string.Empty;
		public bool TryStartGpuCapture(string outputPath, out string error)
		{
			error = string.Empty;
			return false;
		}
		public bool TryStopGpuCapture(out string error)
		{
			error = string.Empty;
			return false;
		}

		public void EnsureMeshResources(Mesh mesh)
		{
			mesh.VertexBuffer ??= _vertexBuffer;
			mesh.IndexBuffer ??= _indexBuffer;
			mesh.StrideInBytes = 16;
			mesh.IndexCount = (uint)mesh.Indices.Length;
		}
	}

	private sealed class TestDevice : IGfxDevice
	{
		public GraphicsBackendKind BackendKind => GraphicsBackendKind.Metal;
		public IGfxDescriptorTable GlobalTable { get; } = new TestDescriptorTable();
		public IGfxCommandList BeginGraphics() => throw new NotSupportedException();
		public IGfxCommandList BeginCompute() => throw new NotSupportedException();
		public void Submit(IGfxCommandList commandList) => throw new NotSupportedException();
		public void WaitForIdle() => throw new NotSupportedException();
		public IGfxTexture CreateTexture(in TextureDescriptor descriptor) => throw new NotSupportedException();
		public IGfxBuffer CreateBuffer(in BufferDescriptor descriptor) => new TestBuffer(descriptor.Usage);
		public IGfxIndirectCommandBuffer CreateIndirectCommandBuffer(in IndirectCommandBufferDescriptor descriptor) => throw new NotSupportedException();
		public IGfxBottomLevelAccelerationStructure CreateBottomLevelAccelerationStructure(in BottomLevelAccelerationStructureDescriptor descriptor) => new TestBottomLevelAccelerationStructure(descriptor);
		public IGfxTopLevelAccelerationStructure CreateTopLevelAccelerationStructure(in TopLevelAccelerationStructureDescriptor descriptor) => new TestTopLevelAccelerationStructure(descriptor);
		public IGfxPipeline GetOrCreatePipeline(PipelineKey key, in ShaderBytecodeSet shaders) => throw new NotSupportedException();
		public IGfxDescriptorSetBuilder CreateDescriptorSetBuilder() => throw new NotSupportedException();
	}

	private sealed class TestCommandList : IGfxCommandList
	{
		public int BottomLevelBuildCount { get; private set; }
		public int TopLevelBuildCount { get; private set; }
		public GraphicsBackendKind BackendKind => GraphicsBackendKind.Metal;
		public void ResetCounts()
		{
			BottomLevelBuildCount = 0;
			TopLevelBuildCount = 0;
		}

		public void BuildBottomLevelAccelerationStructure(IGfxBottomLevelAccelerationStructure accelerationStructure) => BottomLevelBuildCount++;
		public void BuildTopLevelAccelerationStructure(IGfxTopLevelAccelerationStructure accelerationStructure, ReadOnlySpan<RayTracingInstanceDescription> instances) => TopLevelBuildCount++;
		public void SynchronizeAccelerationStructureBuildForComputeRead(IGfxTopLevelAccelerationStructure accelerationStructure) { }
		public void BeginPass(in PassTargets targets, in Viewport viewport) => throw new NotSupportedException();
		public void EndPass() => throw new NotSupportedException();
		public void BindPipeline(IGfxPipeline pipeline) => throw new NotSupportedException();
		public void SetPrimitiveTopology(PrimitiveTopology topology) => throw new NotSupportedException();
		public void SetScissorRect(in RectInt rect) => throw new NotSupportedException();
		public void ClearColorAttachment(uint index, ColorRGBA color) => throw new NotSupportedException();
		public void ClearDepthStencil(float depth) => throw new NotSupportedException();
		public void BindGraphicsDescriptorSet(uint slot, IGfxDescriptorSet descriptorSet) => throw new NotSupportedException();
		public void BindComputeDescriptorSet(uint slot, IGfxDescriptorSet descriptorSet) => throw new NotSupportedException();
		public void SetBindlessTable(IGfxDescriptorTable table) => throw new NotSupportedException();
		public void BindConstantBuffer(uint slot, IGfxBuffer buffer, ulong offset = 0) => throw new NotSupportedException();
		public void SetGraphicsConstants(uint slot, ReadOnlySpan<byte> data) => throw new NotSupportedException();
		public void SetComputeConstants(uint slot, ReadOnlySpan<byte> data) => throw new NotSupportedException();
		public void SetComputeBuffer(uint slot, IGfxBuffer buffer, ulong offset = 0) => throw new NotSupportedException();
		public void PushConstants<T>(in T data) where T : unmanaged => throw new NotSupportedException();
		public void SetVertexBuffer(in VertexBufferView vertexBuffer) => throw new NotSupportedException();
		public void SetVertexBuffers(ReadOnlySpan<VertexBufferView> vertexBuffers) => throw new NotSupportedException();
		public void SetIndexBuffer(in IndexBufferView indexBuffer) => throw new NotSupportedException();
		public void Draw(in DrawArguments arguments) => throw new NotSupportedException();
		public void DrawIndexedIndirect(in IndexBufferView indexBuffer, IGfxBuffer indirectArgsBuffer, ulong indirectArgsOffset) => throw new NotSupportedException();
		public void ExecuteIndirectCommandBuffer(IGfxIndirectCommandBuffer commandBuffer, uint maxCommandCount) => throw new NotSupportedException();
		public void ExecuteIndirectCommandBufferIndexed(IGfxIndirectCommandBuffer commandBuffer, IGfxBuffer commandIndicesBuffer, ulong indicesOffsetBytes, IGfxBuffer commandCountBuffer, ulong commandCountOffsetBytes) => throw new NotSupportedException();
		public void SetComputeAccelerationStructure(uint slot, IGfxTopLevelAccelerationStructure accelerationStructure) => throw new NotSupportedException();
		public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ) => throw new NotSupportedException();
		public void CopyBuffer(IGfxBuffer source, ulong sourceOffset, IGfxBuffer destination, ulong destinationOffset, ulong sizeInBytes) => throw new NotSupportedException();
		public void Barrier(in ResourceBarrierDescription barrier) => throw new NotSupportedException();
	}

	private sealed class TestBottomLevelAccelerationStructure : IGfxBottomLevelAccelerationStructure
	{
		public TestBottomLevelAccelerationStructure(in BottomLevelAccelerationStructureDescriptor descriptor)
		{
			Descriptor = descriptor;
		}

		public string? Name => null;
		public BottomLevelAccelerationStructureDescriptor Descriptor { get; }
	}

	private sealed class TestTopLevelAccelerationStructure : IGfxTopLevelAccelerationStructure
	{
		public TestTopLevelAccelerationStructure(in TopLevelAccelerationStructureDescriptor descriptor)
		{
			Descriptor = descriptor;
		}

		public string? Name => null;
		public TopLevelAccelerationStructureDescriptor Descriptor { get; }
	}

	private sealed class TestBuffer : IGfxBuffer
	{
		public TestBuffer(BufferUsage usage)
		{
			Descriptor = new BufferDescriptor(256, usage);
		}

		public string? Name => null;
		public BufferDescriptor Descriptor { get; }
	}

	private sealed class TestDescriptorTable : IGfxDescriptorTable
	{
		public DescriptorHandle AllocateShaderResourceView(IGfxResource resource) => throw new NotSupportedException();
		public DescriptorHandle AllocateDepthShaderResourceView(IGfxTexture texture) => throw new NotSupportedException();
		public DescriptorHandle AllocateUnorderedAccessView(IGfxResource resource) => throw new NotSupportedException();
		public DescriptorHandle AllocateConstantBufferView(IGfxBuffer buffer) => throw new NotSupportedException();
		public DescriptorHandle AllocateSampler(in SamplerDescriptor sampler) => throw new NotSupportedException();
		public BindlessFallbackHandles GetOrCreateFallbackHandles() => throw new NotSupportedException();
		public void Free(DescriptorHandle handle) => throw new NotSupportedException();
	}
}
