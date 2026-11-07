using Moq;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class GpuDrawBucketHardeningTests
{
	[Test]
	public void ShadowResourceOffsets_AreDisjointAcrossAllCascadesAndLanes()
	{
		var resources = CreateResources();
		var drawArgsStride = resources.GetShadowDrawArgsOffsetBytes(1)
		                     - resources.GetShadowDrawArgsOffsetBytes(0);
		Assert.That(drawArgsStride, Is.GreaterThan(0));

		for (var cascadeIndex = 0; cascadeIndex < GpuDrawResources.MaxShadowViewCount; cascadeIndex++)
		{
			Assert.That(
				resources.GetShadowDrawArgsElementOffset(cascadeIndex),
				Is.EqualTo(cascadeIndex * resources.InstanceCapacity));
			Assert.That(
				resources.GetShadowDrawArgsOffsetBytes(cascadeIndex),
				Is.EqualTo((ulong)cascadeIndex * drawArgsStride));

			for (var laneIndex = 0; laneIndex < GpuDrawExecutionLanes.ExecutionLaneCount; laneIndex++)
			{
				var expectedLaneIndex =
					(cascadeIndex * GpuDrawExecutionLanes.ExecutionLaneCount) + laneIndex;
				Assert.That(
					GpuDrawResources.GetShadowLaneElementOffset(cascadeIndex, laneIndex),
					Is.EqualTo(expectedLaneIndex));
				Assert.That(
					GpuDrawResources.GetShadowExecutionRangeOffsetBytes(cascadeIndex, laneIndex),
					Is.EqualTo((ulong)(expectedLaneIndex * 2 * sizeof(uint))));
			}
		}
	}

	[Test]
	public void ShadowResourceOffsets_RejectOutOfRangeIndices()
	{
		var resources = CreateResources();
		Assert.Throws<ArgumentOutOfRangeException>(
			() => resources.GetShadowDrawArgsElementOffset(-1));
		Assert.Throws<ArgumentOutOfRangeException>(
			() => resources.GetShadowDrawArgsElementOffset(GpuDrawResources.MaxShadowViewCount));
		Assert.Throws<ArgumentOutOfRangeException>(
			() => GpuDrawResources.GetShadowLaneElementOffset(0, -1));
		Assert.Throws<ArgumentOutOfRangeException>(
			() => GpuDrawResources.GetShadowLaneElementOffset(0, GpuDrawExecutionLanes.ExecutionLaneCount));
	}

	[Test]
	public void ShadowDrawArgsSlices_FollowTheInstanceCapacity_WhenTheTablesGrow()
	{
		var resources = CreateResources();
		var device = CreateDevice();

		resources.EnsureTableCapacity(device, GpuDrawResources.InitialInstanceCapacity * 3, 1, 1);

		Assert.Multiple(() =>
		{
			Assert.That(resources.InstanceCapacity, Is.EqualTo(GpuDrawResources.InitialInstanceCapacity * 4));
			Assert.That(resources.GetShadowDrawArgsElementOffset(2), Is.EqualTo(2 * resources.InstanceCapacity));
			Assert.That(
				resources.ShadowDrawArgsBuffer!.Descriptor.SizeInBytes,
				Is.EqualTo((ulong)(GpuDrawResources.MaxShadowViewCount * resources.InstanceCapacity) * 32UL));
		});
	}

	[Test]
	public void EnsureTableCapacity_ReplacesOnlyTheTablesThatGrow()
	{
		var resources = CreateResources();
		var device = CreateDevice();
		var instances = resources.InstanceBuffer;
		var meshes = resources.MeshBuffer;
		var generation = resources.TableGeneration;

		Assert.That(resources.EnsureTableCapacity(device, 10, 10, 10), Is.False, "everything still fits");
		Assert.That(resources.TableGeneration, Is.EqualTo(generation));

		Assert.That(resources.EnsureTableCapacity(device, 1, GpuDrawResources.InitialMeshCapacity + 1, 1), Is.True);

		Assert.Multiple(() =>
		{
			Assert.That(resources.InstanceBuffer, Is.SameAs(instances));
			Assert.That(resources.MeshBuffer, Is.Not.SameAs(meshes));
			Assert.That(resources.MeshCapacity, Is.EqualTo(GpuDrawResources.InitialMeshCapacity * 2));
			Assert.That(resources.TableGeneration, Is.GreaterThan(generation));
			Assert.That(resources.IndirectBindingVersion, Is.GreaterThan(1UL), "a replacement re-encodes every record");
		});
	}

	private static GpuDrawResources CreateResources()
	{
		var resources = new GpuDrawResources(new ShaderCompiler());
		resources.EnsureTableCapacity(CreateDevice(), 1, 1, 1);
		return resources;
	}

	/// <summary>A device whose buffers report the descriptor they were created with.</summary>
	internal static IGfxDevice CreateDevice()
	{
		var device = new Mock<IGfxDevice>();
		device
			.Setup(d => d.CreateBuffer(in It.Ref<BufferDescriptor>.IsAny))
			.Returns(new CreateBufferCallback((in BufferDescriptor descriptor) =>
			{
				var buffer = new Mock<IGfxBuffer>();
				buffer.SetupGet(b => b.Descriptor).Returns(descriptor);
				return buffer.Object;
			}));
		return device.Object;
	}

	private delegate IGfxBuffer CreateBufferCallback(in BufferDescriptor descriptor);

	[Test]
	public void Registry_DefaultBucketsExposeStableIdsAndAlphaModeMappings()
	{
		var definitions = GBufferDrawBuckets.Definitions.ToArray();

		Assert.That(definitions.Select(definition => definition.BucketId), Is.EqualTo(new[]
		{
			GpuDrawBucketId.Opaque,
			GpuDrawBucketId.AlphaBlend,
			GpuDrawBucketId.AlphaTest
		}));
		Assert.That(GBufferDrawBuckets.ResolveBucketId(AlphaMode.Opaque), Is.EqualTo(GpuDrawBucketId.Opaque));
		Assert.That(GBufferDrawBuckets.ResolveBucketId(AlphaMode.AlphaBlend), Is.EqualTo(GpuDrawBucketId.AlphaBlend));
		Assert.That(GBufferDrawBuckets.ResolveBucketId(AlphaMode.AlphaTest), Is.EqualTo(GpuDrawBucketId.AlphaTest));
		Assert.That(GBufferDrawBuckets.GetExecutionIndex(GpuDrawBucketId.Opaque), Is.EqualTo(0));
		Assert.That(GBufferDrawBuckets.GetExecutionIndex(GpuDrawBucketId.AlphaBlend), Is.EqualTo(1));
		Assert.That(GBufferDrawBuckets.GetExecutionIndex(GpuDrawBucketId.AlphaTest), Is.EqualTo(2));
		var opaqueMaterial = new Material("opaque") { AlphaMode = AlphaMode.Opaque };
		var alphaBlendMaterial = new Material("alpha-blend") { AlphaMode = AlphaMode.AlphaBlend };
		var alphaTestMaterial = new Material("alpha-test") { AlphaMode = AlphaMode.AlphaTest };
		Assert.That(GpuDrawClassification.ResolveBucketId(GpuDrawKind.Mesh, opaqueMaterial), Is.EqualTo(GpuDrawBucketId.Opaque));
		Assert.That(GpuDrawClassification.ResolveBucketId(GpuDrawKind.Mesh, alphaBlendMaterial), Is.EqualTo(GpuDrawBucketId.AlphaBlend));
		Assert.That(GpuDrawClassification.ResolveBucketId(GpuDrawKind.Mesh, alphaTestMaterial), Is.EqualTo(GpuDrawBucketId.AlphaTest));
	}

	[Test]
	public void Registry_ExtraBucketDoesNotReorderLegacyMeshPassBuckets()
	{
		var registry = new GBufferDrawBucketRegistry(
			new GBufferDrawBucketDefinition(
				GpuDrawBucketId.Opaque,
				executionIndex: 0,
				"Opaque",
				"Opaque",
				string.Empty,
				DrawPassParticipation.GBuffer | DrawPassParticipation.ShadowCaster,
				AlphaMode.Opaque),
			new GBufferDrawBucketDefinition(
				(GpuDrawBucketId)99,
				executionIndex: 1,
				"DebugExtra",
				"DebugExtra",
				string.Empty,
				DrawPassParticipation.None),
			new GBufferDrawBucketDefinition(
				GpuDrawBucketId.AlphaBlend,
				executionIndex: 2,
				"AlphaBlend",
				"AlphaBlend",
				string.Empty,
				DrawPassParticipation.ForwardTransparent,
				AlphaMode.AlphaBlend),
			new GBufferDrawBucketDefinition(
				GpuDrawBucketId.AlphaTest,
				executionIndex: 3,
				"AlphaTest",
				"AlphaTest",
				"WOLF_ALPHA_CLIP",
				DrawPassParticipation.GBuffer | DrawPassParticipation.ShadowCaster,
				AlphaMode.AlphaTest));

		Assert.That(registry.ResolveBucketId(AlphaMode.Opaque), Is.EqualTo(GpuDrawBucketId.Opaque));
		Assert.That(registry.ResolveBucketId(AlphaMode.AlphaBlend), Is.EqualTo(GpuDrawBucketId.AlphaBlend));
		Assert.That(registry.ResolveBucketId(AlphaMode.AlphaTest), Is.EqualTo(GpuDrawBucketId.AlphaTest));

		var gbufferBuckets = registry.GetDefinitionsForPass(DrawPassParticipation.GBuffer).ToArray();
		var transparentBuckets = registry.GetDefinitionsForPass(DrawPassParticipation.ForwardTransparent).ToArray();
		var shadowBuckets = registry.GetDefinitionsForPass(DrawPassParticipation.ShadowCaster).ToArray();

		Assert.That(gbufferBuckets.Select(bucket => bucket.BucketId), Is.EqualTo(new[]
		{
			GpuDrawBucketId.Opaque,
			GpuDrawBucketId.AlphaTest
		}));
		Assert.That(transparentBuckets.Select(bucket => bucket.BucketId), Is.EqualTo(new[]
		{
			GpuDrawBucketId.AlphaBlend
		}));
		Assert.That(shadowBuckets.Select(bucket => bucket.BucketId), Is.EqualTo(new[]
		{
			GpuDrawBucketId.Opaque,
			GpuDrawBucketId.AlphaTest
		}));
	}

	[Test]
	public void HardeningSnapshot_ReportsBucketDiagnosticsInStableIdOrder()
	{
		var stats = new GpuDrawHardeningStats();
		stats.ResetSubmissionDiagnostics();
		stats.SetSubmittedDrawCount(GpuDrawBucketId.AlphaTest, 7);
		stats.SetSubmittedDrawCount(GpuDrawBucketId.Opaque, 3);
		stats.SetVisibleDrawCount(GpuDrawBucketId.AlphaTest, 5);
		stats.SetExecutionRange(GpuDrawBucketId.AlphaTest, 11, 21);
		stats.AddMaterialFallbackIncident(GpuDrawBucketId.AlphaTest, 2);

		var snapshot = stats.Snapshot();

		Assert.That(snapshot.BucketDiagnostics.Select(bucket => bucket.BucketId), Is.EqualTo(new[]
		{
			GpuDrawBucketId.Opaque,
			GpuDrawBucketId.AlphaBlend,
			GpuDrawBucketId.AlphaTest
		}));

		var opaque = snapshot.BucketDiagnostics[0];
		var alphaBlend = snapshot.BucketDiagnostics[1];
		var alphaTest = snapshot.BucketDiagnostics[2];

		Assert.That(opaque.SubmittedDrawCount, Is.EqualTo(3));
		Assert.That(alphaBlend.SubmittedDrawCount, Is.EqualTo(0));
		Assert.That(alphaTest.SubmittedDrawCount, Is.EqualTo(7));
		Assert.That(alphaTest.VisibleDrawCount, Is.EqualTo(5));
		Assert.That(alphaTest.ExecutionRangeStart, Is.EqualTo(11));
		Assert.That(alphaTest.ExecutionRangeEndExclusive, Is.EqualTo(21));
		Assert.That(alphaTest.ExecutionRangeSpan, Is.EqualTo(10));
		Assert.That(alphaTest.MaterialFallbackIncidents, Is.EqualTo(2));
	}

	[Test]
	public void Classification_UnsupportedDrawKind_FailsClosed()
	{
		var material = new Material("unsupported");

		Assert.That(GpuDrawClassification.TryResolveBucketId((GpuDrawKind)99, material, out var bucketId), Is.False);
		Assert.That(bucketId, Is.EqualTo(GpuDrawBucketId.Opaque));
	}

	[Test]
	public void Classification_DebugPrimitive_UsesOnlyOpaqueAndTransparentBuckets()
	{
		var opaqueMaterial = new Material("debug-opaque") { AlphaMode = AlphaMode.Opaque };
		var alphaBlendMaterial = new Material("debug-alpha") { AlphaMode = AlphaMode.AlphaBlend };
		var alphaTestMaterial = new Material("debug-alpha-test") { AlphaMode = AlphaMode.AlphaTest };

		Assert.That(
			GpuDrawClassification.ResolveBucketId(GpuDrawKind.DebugPrimitive, opaqueMaterial),
			Is.EqualTo(GpuDrawBucketId.Opaque));
		Assert.That(
			GpuDrawClassification.ResolveBucketId(GpuDrawKind.DebugPrimitive, alphaBlendMaterial),
			Is.EqualTo(GpuDrawBucketId.AlphaBlend));
		Assert.That(
			GpuDrawClassification.ResolveBucketId(GpuDrawKind.DebugPrimitive, alphaTestMaterial),
			Is.EqualTo(GpuDrawBucketId.Opaque));
	}

	[Test]
	public void Classification_Terrain_UsesOnlyOpaqueBucket()
	{
		var opaqueMaterial = new Material("terrain") { AlphaMode = AlphaMode.Opaque };
		var alphaBlendMaterial = new Material("terrain-alpha") { AlphaMode = AlphaMode.AlphaBlend };

		Assert.That(
			GpuDrawClassification.ResolveBucketId(GpuDrawKind.Terrain, opaqueMaterial),
			Is.EqualTo(GpuDrawBucketId.Opaque));
		Assert.That(
			GpuDrawClassification.ResolveBucketId(GpuDrawKind.Terrain, alphaBlendMaterial),
			Is.EqualTo(GpuDrawBucketId.Opaque));
	}

	[Test]
	public void ExecutionLanes_DefaultConfiguration_PreservesMeshOrderingAndAppendsDebugPrimitiveLanes()
	{
		var definitions = GpuDrawExecutionLanes.Definitions.ToArray();

		Assert.That(definitions.Where(definition => !definition.ReverseWinding).Select(definition => definition.Key), Is.EqualTo(new[]
		{
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.Opaque, GpuDrawSidedness.SingleSided),
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.AlphaBlend, GpuDrawSidedness.SingleSided),
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.AlphaTest, GpuDrawSidedness.SingleSided),
			new GpuDrawExecutionKey(GpuDrawKind.DebugPrimitive, GpuDrawBucketId.Opaque, GpuDrawSidedness.SingleSided),
			new GpuDrawExecutionKey(GpuDrawKind.DebugPrimitive, GpuDrawBucketId.AlphaBlend, GpuDrawSidedness.SingleSided),
			new GpuDrawExecutionKey(GpuDrawKind.Terrain, GpuDrawBucketId.Opaque, GpuDrawSidedness.SingleSided),
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.Opaque, GpuDrawSidedness.DoubleSided),
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.AlphaBlend, GpuDrawSidedness.DoubleSided),
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.AlphaTest, GpuDrawSidedness.DoubleSided),
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.Opaque, GpuDrawSidedness.SingleSided, true),
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.AlphaTest, GpuDrawSidedness.SingleSided, true),
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.Opaque, GpuDrawSidedness.DoubleSided, true),
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.AlphaTest, GpuDrawSidedness.DoubleSided, true)
		}));
		Assert.That(
			definitions.Select(definition => definition.ExecutionIndex),
			Is.EqualTo(Enumerable.Range(0, 26)));
	}

	[Test]
	public void ExecutionLanes_DoubleSidedMaterial_ResolvesToLaneThatDisablesCulling()
	{
		var singleSided = new Material("opaque") { AlphaMode = AlphaMode.Opaque };
		var doubleSided = new Material("opaque") { AlphaMode = AlphaMode.Opaque, DoubleSided = true };

		var singleSidedLane = GpuDrawClassification.ResolveExecutionLane(GpuDrawKind.Mesh, singleSided);
		var doubleSidedLane = GpuDrawClassification.ResolveExecutionLane(GpuDrawKind.Mesh, doubleSided);

		Assert.That(singleSidedLane.Sidedness, Is.EqualTo(GpuDrawSidedness.SingleSided));
		Assert.That(doubleSidedLane.Sidedness, Is.EqualTo(GpuDrawSidedness.DoubleSided));
		Assert.That(doubleSidedLane.BucketId, Is.EqualTo(singleSidedLane.BucketId));
		Assert.That(doubleSidedLane.ExecutionIndex, Is.Not.EqualTo(singleSidedLane.ExecutionIndex));
		Assert.That(singleSidedLane.ResolveCullMode(CullMode.Back), Is.EqualTo(CullMode.Back));
		Assert.That(doubleSidedLane.ResolveCullMode(CullMode.Back), Is.EqualTo(CullMode.None));
	}

	[Test]
	public void ExecutionLanes_DoubleSidedFlag_IsIgnoredForKindsWithoutADoubleSidedLane()
	{
		var doubleSided = new Material("terrain") { AlphaMode = AlphaMode.Opaque, DoubleSided = true };

		Assert.That(
			GpuDrawClassification.ResolveSidedness(GpuDrawKind.Terrain, doubleSided),
			Is.EqualTo(GpuDrawSidedness.SingleSided));
		Assert.That(
			GpuDrawClassification.ResolveSidedness(GpuDrawKind.DebugPrimitive, doubleSided),
			Is.EqualTo(GpuDrawSidedness.SingleSided));
	}

	[Test]
	public void ExecutionLanes_PassParticipation_ExcludesDebugPrimitiveFromShadowCasterPath()
	{
		var gbuffer = GpuDrawExecutionLanes.GetDefinitionsForPass(DrawPassParticipation.GBuffer).ToArray();
		var transparent = GpuDrawExecutionLanes.GetDefinitionsForPass(DrawPassParticipation.ForwardTransparent).ToArray();
		var shadow = GpuDrawExecutionLanes.GetDefinitionsForPass(DrawPassParticipation.ShadowCaster).ToArray();

		Assert.That(gbuffer.Where(definition => !definition.ReverseWinding).Select(definition => definition.Key), Is.EqualTo(new[]
		{
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.Opaque, GpuDrawSidedness.SingleSided),
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.AlphaTest, GpuDrawSidedness.SingleSided),
			new GpuDrawExecutionKey(GpuDrawKind.DebugPrimitive, GpuDrawBucketId.Opaque, GpuDrawSidedness.SingleSided),
			new GpuDrawExecutionKey(GpuDrawKind.Terrain, GpuDrawBucketId.Opaque, GpuDrawSidedness.SingleSided),
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.Opaque, GpuDrawSidedness.DoubleSided),
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.AlphaTest, GpuDrawSidedness.DoubleSided),
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.Opaque, GpuDrawSidedness.SingleSided, true),
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.AlphaTest, GpuDrawSidedness.SingleSided, true),
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.Opaque, GpuDrawSidedness.DoubleSided, true),
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.AlphaTest, GpuDrawSidedness.DoubleSided, true)
		}));
		Assert.That(transparent.Where(definition => !definition.ReverseWinding).Select(definition => definition.Key), Is.EqualTo(new[]
		{
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.AlphaBlend, GpuDrawSidedness.SingleSided),
			new GpuDrawExecutionKey(GpuDrawKind.DebugPrimitive, GpuDrawBucketId.AlphaBlend, GpuDrawSidedness.SingleSided),
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.AlphaBlend, GpuDrawSidedness.DoubleSided)
		}));
		Assert.That(shadow.Where(definition => !definition.ReverseWinding).Select(definition => definition.Key), Is.EqualTo(new[]
		{
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.Opaque, GpuDrawSidedness.SingleSided),
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.AlphaTest, GpuDrawSidedness.SingleSided),
			new GpuDrawExecutionKey(GpuDrawKind.Terrain, GpuDrawBucketId.Opaque, GpuDrawSidedness.SingleSided),
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.Opaque, GpuDrawSidedness.DoubleSided),
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.AlphaTest, GpuDrawSidedness.DoubleSided),
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.Opaque, GpuDrawSidedness.SingleSided, true),
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.AlphaTest, GpuDrawSidedness.SingleSided, true),
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.Opaque, GpuDrawSidedness.DoubleSided, true),
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.AlphaTest, GpuDrawSidedness.DoubleSided, true)
		}));
	}
}
