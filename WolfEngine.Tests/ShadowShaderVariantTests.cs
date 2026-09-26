using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class ShadowShaderVariantTests
{
	[TestCase(GraphicsBackendKind.Metal)]
	[TestCase(GraphicsBackendKind.D3D12)]
	public void CatalogDeclaresTerrainAndMeshVariantsForEveryCascade(GraphicsBackendKind backend)
	{
		var catalog = new EngineShaderCatalog();
		var requests = catalog.GetDeclaredRuntimeRequests(backend);
		for (var cascade = 0; cascade < ShadowMapPass.MaxCascadeCount; cascade++)
		{
			var cascadeDefine = $"WOLF_SHADOW_CASCADE_INDEX={cascade}";
			foreach (var geometryDefine in new[] { "", "WOLF_ALPHA_CLIP", "WOLF_SHADOW_TERRAIN" })
			{
				var request = ShaderRequest.Graphics(EngineShaderPrograms.ShadowMap,
					"vertexShader", "fragmentShader", backend, cascadeDefine, geometryDefine);
				Assert.That(() => catalog.ValidateRequest(request), Throws.Nothing);
				Assert.That(requests, Does.Contain(request));
			}
		}
		Assert.That(() => catalog.ValidateRequest(ShaderRequest.Graphics(EngineShaderPrograms.GBuffer,
			"vertexShader", "fragmentShader", backend, "WOLF_SHADOW_TERRAIN")), Throws.InvalidOperationException);
	}

	[TestCase(0)]
	[TestCase(1)]
	[TestCase(2)]
	public void CompiledShadowVariantsOnlyBindTerrainForTerrainDraws(int cascade)
	{
		if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
			Assert.Ignore("Requires a Metal or D3D12 shader compiler.");
		var backend = OperatingSystem.IsMacOS() ? GraphicsBackendKind.Metal : GraphicsBackendKind.D3D12;
		var compiler = new ShaderCompiler();
		foreach (var geometryDefine in new[] { "", "WOLF_ALPHA_CLIP", "WOLF_SHADOW_TERRAIN" })
		{
			var request = ShaderRequest.Graphics(EngineShaderPrograms.ShadowMap,
				"vertexShader", "fragmentShader", backend, $"WOLF_SHADOW_CASCADE_INDEX={cascade}", geometryDefine);
			var artifact = compiler.GetArtifact(request);
			var bindings = SharedDrawGraphicsBufferBindings.FromShadowReflection(artifact.ReflectionLayout);
			var isTerrain = geometryDefine == "WOLF_SHADOW_TERRAIN";
			Assert.That(artifact.ReflectionLayout.TryGetResource("g_TerrainMaterialTable", out _), Is.EqualTo(isTerrain));
			Assert.That(bindings.TerrainMaterialRegisterIndex.HasValue, Is.EqualTo(isTerrain));
			Assert.That(bindings.InstanceRegisterIndex, Is.EqualTo(10u));
			Assert.That(bindings.DrawArgsRegisterIndex, Is.EqualTo(12u));
			Assert.That(() => artifact.ReflectionLayout.GetConstantBuffer("CameraParams").GetFieldOrThrow("viewProjection2"), Throws.Nothing);
		}
	}
}
