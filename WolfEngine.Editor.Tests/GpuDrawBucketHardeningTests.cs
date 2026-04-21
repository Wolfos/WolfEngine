using System.Linq;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class GpuDrawBucketHardeningTests
{
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
	public void ExecutionLanes_DefaultConfiguration_PreservesMeshOrderingAndAppendsDebugPrimitiveLanes()
	{
		var definitions = GpuDrawExecutionLanes.Definitions.ToArray();

		Assert.That(definitions.Select(definition => definition.Key), Is.EqualTo(new[]
		{
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.Opaque),
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.AlphaBlend),
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.AlphaTest),
			new GpuDrawExecutionKey(GpuDrawKind.DebugPrimitive, GpuDrawBucketId.Opaque),
			new GpuDrawExecutionKey(GpuDrawKind.DebugPrimitive, GpuDrawBucketId.AlphaBlend)
		}));
		Assert.That(definitions.Select(definition => definition.ExecutionIndex), Is.EqualTo(new[] { 0, 1, 2, 3, 4 }));
	}

	[Test]
	public void ExecutionLanes_PassParticipation_ExcludesDebugPrimitiveFromShadowCasterPath()
	{
		var gbuffer = GpuDrawExecutionLanes.GetDefinitionsForPass(DrawPassParticipation.GBuffer).ToArray();
		var transparent = GpuDrawExecutionLanes.GetDefinitionsForPass(DrawPassParticipation.ForwardTransparent).ToArray();
		var shadow = GpuDrawExecutionLanes.GetDefinitionsForPass(DrawPassParticipation.ShadowCaster).ToArray();

		Assert.That(gbuffer.Select(definition => definition.Key), Is.EqualTo(new[]
		{
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.Opaque),
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.AlphaTest),
			new GpuDrawExecutionKey(GpuDrawKind.DebugPrimitive, GpuDrawBucketId.Opaque)
		}));
		Assert.That(transparent.Select(definition => definition.Key), Is.EqualTo(new[]
		{
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.AlphaBlend),
			new GpuDrawExecutionKey(GpuDrawKind.DebugPrimitive, GpuDrawBucketId.AlphaBlend)
		}));
		Assert.That(shadow.Select(definition => definition.Key), Is.EqualTo(new[]
		{
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.Opaque),
			new GpuDrawExecutionKey(GpuDrawKind.Mesh, GpuDrawBucketId.AlphaTest)
		}));
	}
}
