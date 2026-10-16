using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class DdgiVisibilityResolutionTests
{
	[Test]
	public void HalfResolutionStoresAQuarterOfTheVisibilityTexels()
	{
		var full = new DiffuseGlobalIlluminationConfig();
		var half = new DiffuseGlobalIlluminationConfig
		{
			VisibilityResolution = DdgiVisibilityResolution.Half
		};
		var shape = DdgiUtilities.GetGridShape(full);

		Assert.That(full.VisibilityResolution, Is.EqualTo(DdgiVisibilityResolution.Full));
		Assert.That(DdgiUtilities.GetVisibilityTileInteriorSize(full), Is.EqualTo(16));
		Assert.That(DdgiUtilities.GetVisibilityTileInteriorSize(half), Is.EqualTo(8));
		Assert.That(
			DdgiUtilities.GetAtlasSize(shape, DdgiUtilities.GetVisibilityTileInteriorSize(half)),
			Is.EqualTo(new Int2(460, 450)));
	}

	[TestCase(GraphicsBackendKind.Metal)]
	[TestCase(GraphicsBackendKind.D3D12)]
	public void CatalogDeclaresTheHalfResolutionIntegrateVariant(GraphicsBackendKind backend)
	{
		var catalog = new EngineShaderCatalog();
		var define = DdgiUtilities.GetVisibilityIntegrateDefine(DdgiUtilities.HalfVisibilityTileInteriorSize)!;
		var request = ShaderRequest.Compute(EngineShaderPrograms.DdgiIntegrate, "DdgiVisibilityIntegrateCS", backend, define);

		Assert.That(DdgiUtilities.GetVisibilityIntegrateDefine(DdgiUtilities.FullVisibilityTileInteriorSize), Is.Null);
		Assert.That(() => catalog.ValidateRequest(request), Throws.Nothing);
		Assert.That(catalog.GetDeclaredRuntimeRequests(backend), Does.Contain(request));
		Assert.That(
			() => catalog.ValidateRequest(ShaderRequest.Compute(EngineShaderPrograms.DdgiTrace, "DdgiProbeTraceCS", backend, define)),
			Throws.InvalidOperationException);
	}

	[Test]
	public void IntegrateVariantsUseOneThreadPerVisibilityTexel()
	{
		if (OperatingSystem.IsMacOS() == false && OperatingSystem.IsWindows() == false)
		{
			Assert.Ignore("Requires a Metal or D3D12 shader compiler.");
		}

		var backend = OperatingSystem.IsMacOS() ? GraphicsBackendKind.Metal : GraphicsBackendKind.D3D12;
		var compiler = new ShaderCompiler();
		foreach (var tileSize in new[] { DdgiUtilities.FullVisibilityTileInteriorSize, DdgiUtilities.HalfVisibilityTileInteriorSize })
		{
			var define = DdgiUtilities.GetVisibilityIntegrateDefine(tileSize);
			var shader = define is null
				? compiler.GetComputeShaderWithReflection(EngineShaderPrograms.DdgiIntegrate, "DdgiVisibilityIntegrateCS", backend)
				: compiler.GetComputeShaderWithReflection(EngineShaderPrograms.DdgiIntegrate, "DdgiVisibilityIntegrateCS", backend, define);

			Assert.That(shader.ThreadGroupSize.X, Is.EqualTo((uint)tileSize));
			Assert.That(shader.ThreadGroupSize.Y, Is.EqualTo((uint)tileSize));
		}
	}
}
