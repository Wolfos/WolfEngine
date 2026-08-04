using System.Numerics;
using System.Text.Json;
using WolfEngine.AssetPipeline;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;
using WolfEngine.Mathematics;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class TemporalAntiAliasingTests
{
	[Test]
	public void DefaultsExposeOnlyFsr3HostControls()
	{
		var settings = new Fsr3UpscalerConfig();

		Assert.Multiple(() =>
		{
			Assert.That(settings.Enabled, Is.True);
			Assert.That(settings.EnableSharpening, Is.False);
			Assert.That(settings.Sharpness, Is.EqualTo(0.2f));
			Assert.That(settings.AlphaTestReactiveScale, Is.EqualTo(1.0f));
			Assert.That(settings.TransparencyAndCompositionMaskScale, Is.EqualTo(1.0f));
		});
	}

	[Test]
	public void LegacyTemporalAntiAliasingJsonKeyMigratesToFsr3Settings()
	{
		const string json =
			"""
			{
			  "TemporalAntiAliasing": {
			    "Enabled": false,
			    "PhaseCount": 8,
			    "EnableCasSharpen": true,
			    "CasSharpness": 1.0,
			    "StaticHistoryWeight": 0.97
			  }
			}
			""";

		var config = JsonSerializer.Deserialize<RenderConfig>(
			json,
			AssetJson.SerializerOptions)!;

		Assert.Multiple(() =>
		{
			Assert.That(config.Fsr3.Enabled, Is.False);
			// Legacy sharpening fields must not silently enable the old post-tonemap CAS pass.
			Assert.That(config.Fsr3.EnableSharpening, Is.False);
			Assert.That(config.Fsr3.Sharpness, Is.EqualTo(0.2f));
			Assert.That(config.Fsr3.AlphaTestReactiveScale, Is.EqualTo(1.0f));
			Assert.That(config.Fsr3.TransparencyAndCompositionMaskScale, Is.EqualTo(1.0f));
		});
	}

	[Test]
	public void EightPhaseJitterRepeatsExactly()
	{
		for (ulong frameIndex = 0; frameIndex < 8; frameIndex++)
		{
			Assert.That(
				TemporalJitter.GetHaltonJitterPixels(frameIndex, 8),
				Is.EqualTo(TemporalJitter.GetHaltonJitterPixels(frameIndex + 8, 8)));
		}
	}

	[Test]
	public void ProjectionChangeUsesConfiguredEpsilon()
	{
		var projection = Matrix4x4.CreatePerspectiveFieldOfView(
			MathF.PI / 3.0f,
			16.0f / 9.0f,
			0.1f,
			1000.0f);
		var withinTolerance = projection;
		withinTolerance.M11 += 0.5e-5f;
		var changed = projection;
		changed.M11 += 2.0e-5f;

		Assert.That(
			TemporalJitter.HasProjectionChanged(projection, withinTolerance),
			Is.False);
		Assert.That(
			TemporalJitter.HasProjectionChanged(projection, changed),
			Is.True);
	}

	[TestCase(1280, 720, 640, 360)]
	[TestCase(1279, 719, 639, 359)]
	[TestCase(1, 1, 1, 1)]
	public void Fsr3ShadingChangeTargetUsesHalfRenderResolution(
		int renderWidth,
		int renderHeight,
		int expectedWidth,
		int expectedHeight)
	{
		var size = RenderGraphFrameBuilder.GetFsr3ShadingChangeSize(
			new Int2(renderWidth, renderHeight));

		Assert.That(size, Is.EqualTo(new Int2(expectedWidth, expectedHeight)));
	}

	[Test]
	public void ResolveShaderCompilesWithFocusedBindingsForD3D12()
	{
		if (OperatingSystem.IsWindows() == false)
		{
			Assert.Ignore("DirectX shader validation only runs on Windows.");
		}

		var compiled = new ShaderCompiler().GetComputeShaderWithReflection(
			ShaderPath("Taa/taa_resolve.compute.slang"),
			"TaaResolveCS",
			GraphicsBackendKind.D3D12);

		Assert.That(compiled.Bytecode.IsEmpty, Is.False);
		Assert.That(compiled.ThreadGroupSize.X, Is.EqualTo(8));
		Assert.That(compiled.ThreadGroupSize.Y, Is.EqualTo(8));
		Assert.That(
			compiled.ReflectionLayout.GetConstantBuffer("BindlessHandles")
				.GetFieldOrThrow("normalHandle").ValueKind,
			Is.EqualTo(ShaderConstantFieldValueKind.UInt));
		Assert.That(
			compiled.ReflectionLayout.GetConstantBuffer("TaaSettings")
				.GetFieldOrThrow("staticHistoryWeight").ValueKind,
			Is.EqualTo(ShaderConstantFieldValueKind.Float));
		Assert.That(
			compiled.ReflectionLayout.GetConstantBuffer("TaaSettings")
				.GetFieldOrThrow("inverseUnjitteredViewProjection").ValueKind,
			Is.EqualTo(ShaderConstantFieldValueKind.Matrix4x4));
	}

	private static string ShaderPath(string relativePath) => Path.GetFullPath(Path.Combine(
		TestContext.CurrentContext.TestDirectory,
		"..",
		"..",
		"..",
		"..",
		"WolfEngine",
		"Shaders",
		relativePath));
}
